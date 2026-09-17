using System.Text;
using System.Text.RegularExpressions;
using KidRemote.Core;

namespace KidRemote.Telegram;

/// <summary>
/// Long polling к Telegram. Разрывы связи, сон и выключение компьютера не теряют команды:
/// неподтверждённые апдейты лежат на серверах Telegram до суток и приходят после пробуждения.
/// </summary>
internal sealed partial class BotService : IDisposable
{
    private const int PollTimeoutSeconds = 30;
    private const int MaxDraftMinutes = 600;
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Черновик точного значения: сколько минут набрано и что с ними сделать.</summary>
    private sealed class Draft
    {
        public int Minutes { get; set; }
        public DraftMode Mode { get; set; } = DraftMode.Add;
    }

    /// <summary>Родитель попросил закрыть приложение командой /quit.</summary>
    public event Action? ShutdownRequested;

    private readonly AppConfig _config;
    private readonly TimeBank _bank;
    private readonly StateStore _store;
    private readonly PersistedState _state;
    private readonly Func<ActivitySnapshot> _activity;
    private readonly TelegramClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, Draft> _drafts = new();

    /// <summary>Чаты, где сейчас открыт экран подтверждения или настройки: их нельзя затирать автообновлением.</summary>
    private readonly HashSet<long> _busyChats = new();
    private readonly object _busySync = new();

    private Task? _loop;
    private string _lastPanelText = string.Empty;
    private string? _inviteCode;
    private DateTime _inviteExpiresUtc;

    public BotService(AppConfig config, TimeBank bank, StateStore store, PersistedState state, Func<ActivitySnapshot> activity)
    {
        _config = config;
        _bank = bank;
        _store = store;
        _state = state;
        _activity = activity;
        _client = new TelegramClient(config.ResolvedToken);
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);

        await _client.SetCommandsAsync(Keyboards.Commands(), ct).ConfigureAwait(false);

        // Первый запуск: пропускаем всё, что накопилось до установки, чтобы старые команды не сработали.
        if (_state.UpdateOffset == 0)
        {
            await SkipBacklogAsync(ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _client.GetUpdatesAsync(_state.UpdateOffset, PollTimeoutSeconds, ct).ConfigureAwait(false);

                foreach (var update in updates)
                {
                    await HandleUpdateAsync(update, ct).ConfigureAwait(false);
                    _state.UpdateOffset = update.UpdateId + 1;
                }

                if (updates.Count > 0) _store.Save(_state);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Сон, потеря сети, недоступность API — ждём и пробуем снова.
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task SkipBacklogAsync(CancellationToken ct)
    {
        try
        {
            var updates = await _client.GetUpdatesAsync(-1, 0, ct).ConfigureAwait(false);
            if (updates.Count > 0)
            {
                _state.UpdateOffset = updates[^1].UpdateId + 1;
                _store.Save(_state);
            }
        }
        catch
        {
            // Не страшно: разберёмся на первой удачной итерации.
        }
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, ct).ConfigureAwait(false);
            return;
        }

        if (update.Message is { Chat: not null } message)
        {
            await HandleMessageAsync(message, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat!.Id;
        var text = (message.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;

        if (!_config.IsParent(chatId))
        {
            await HandleStrangerAsync(chatId, text, ct).ConfigureAwait(false);
            return;
        }

        if (text == Keyboards.MenuButtonText)
        {
            await SendPanelAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        if (TryParseBalanceEdit(text, out var seconds, out var absolute))
        {
            await RequestAsync(chatId, absolute ? "set" : seconds < 0 ? "sub" : "add", Math.Abs(seconds), ct).ConfigureAwait(false);
            return;
        }

        var command = text.Split(' ', 2)[0].ToLowerInvariant();
        var argument = text.Contains(' ') ? text[(text.IndexOf(' ') + 1)..].Trim() : string.Empty;

        switch (command)
        {
            case "/start":
                await _client.SendMessageAsync(chatId, "Кнопки под полем ввода всегда под рукой.",
                    Keyboards.Persistent(), ct).ConfigureAwait(false);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/menu":
            case "/status":
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/add" when TryParseDuration(argument, out var add):
                await RequestAsync(chatId, "add", add, ct).ConfigureAwait(false);
                break;

            case "/sub" when TryParseDuration(argument, out var sub):
                await RequestAsync(chatId, "sub", sub, ct).ConfigureAwait(false);
                break;

            case "/set" when TryParseDuration(argument, out var set):
                await RequestAsync(chatId, "set", set, ct).ConfigureAwait(false);
                break;

            case "/free":
                await RequestAsync(chatId, "free", 0, ct).ConfigureAwait(false);
                break;

            case "/limit":
                await RequestAsync(chatId, "unfree", 0, ct).ConfigureAwait(false);
                break;

            case "/lock":
                await RequestAsync(chatId, "lock", 0, ct).ConfigureAwait(false);
                break;

            case "/password" when argument.Length >= 4:
                _config.SetPassword(argument);
                await _client.SendMessageAsync(chatId,
                    "Пароль сохранён. Он спрашивается при открытии настроек из трея.\n\n" +
                    "Удалите сообщение с паролем из чата — в истории ему не место.",
                    null, ct).ConfigureAwait(false);
                break;

            case "/password":
                await _client.SendMessageAsync(chatId,
                    "Задайте пароль не короче четырёх символов:\n<code>/password ваш_пароль</code>",
                    null, ct).ConfigureAwait(false);
                break;

            case "/invite":
                await _client.SendMessageAsync(chatId, BuildInviteText(), null, ct).ConfigureAwait(false);
                break;

            case "/quit":
                await RequestAsync(chatId, "quit", 0, ct).ConfigureAwait(false);
                break;

            case "/debug":
                await _client.SendMessageAsync(chatId, BuildDebugText(), null, ct).ConfigureAwait(false);
                break;

            case "/help":
                await _client.SendMessageAsync(chatId, HelpText, null, ct).ConfigureAwait(false);
                break;

            default:
                await _client.SendMessageAsync(chatId, "Не понял команду. /help — список того, что я умею.", null, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Незнакомый собеседник: либо первичная привязка, либо код приглашения.</summary>
    private async Task HandleStrangerAsync(long chatId, string text, CancellationToken ct)
    {
        if (_config.NeedsParentBinding)
        {
            _config.AddParent(chatId);
            await _client.SendMessageAsync(chatId,
                "Готово, этот чат привязан как родительский.\n\n" +
                "Уведомления выключены — включить нужные можно в «⚙️ Настройки» → «🔔 Мои уведомления».\n\n" +
                "Второго родителя добавьте кнопкой «👪 Родители».",
                Keyboards.Persistent(), ct).ConfigureAwait(false);
            await SendPanelAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        var code = text.StartsWith("/join", StringComparison.OrdinalIgnoreCase)
            ? text[5..].Trim()
            : text.Trim();

        if (IsInviteValid(code))
        {
            _inviteCode = null;
            _config.AddParent(chatId);
            await _client.SendMessageAsync(chatId, "Код принят. Теперь вы тоже можете управлять временем.",
                Keyboards.Persistent(), ct).ConfigureAwait(false);
            await SendPanelAsync(chatId, ct).ConfigureAwait(false);
            await BroadcastAsync($"К управлению подключился ещё один родитель (chat id {chatId}).", chatId, ct).ConfigureAwait(false);
        }
        else
        {
            await _client.SendMessageAsync(chatId, "Этот чат не привязан. Нужен код приглашения: /join КОД", null, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message?.Chat?.Id ?? 0;
        var messageId = callback.Message?.MessageId ?? 0;

        if (chatId == 0 || !_config.IsParent(chatId))
        {
            await _client.AnswerCallbackAsync(callback.Id, "Нет доступа", ct).ConfigureAwait(false);
            return;
        }

        var data = callback.Data ?? string.Empty;
        var parts = data.Split(':');
        var verb = parts[0];

        switch (verb)
        {
            // Запрос подтверждения: состояние ещё не меняется.
            case "ask":
            {
                var kind = parts.Length > 1 ? parts[1] : string.Empty;
                var argument = parts.Length > 2 && long.TryParse(parts[2], out var parsed) ? parsed : 0;

                MarkBusy(chatId, true);
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await EditAsync(chatId, messageId, BuildConfirmText(kind, argument), Keyboards.Confirm(kind, argument), ct).ConfigureAwait(false);
                return;
            }

            // Подтверждено — применяем.
            case "ok":
            {
                var kind = parts.Length > 1 ? parts[1] : string.Empty;
                var argument = parts.Length > 2 && long.TryParse(parts[2], out var parsed) ? parsed : 0;

                if (kind == "quit")
                {
                    await _client.AnswerCallbackAsync(callback.Id, "Выключаюсь", ct).ConfigureAwait(false);
                    await QuitAsync(chatId, messageId, ct).ConfigureAwait(false);
                    return;
                }

                var toast = Apply(kind, argument);
                await _client.AnswerCallbackAsync(callback.Id, toast, ct).ConfigureAwait(false);
                await ShowPanelAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "draft":
            {
                _drafts[chatId] = new Draft();
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await ShowDraftAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "d":
            {
                var draft = GetDraft(chatId);
                if (parts.Length > 1 && int.TryParse(parts[1], out var delta))
                    draft.Minutes = Math.Clamp(draft.Minutes + delta, 0, MaxDraftMinutes);

                await _client.AnswerCallbackAsync(callback.Id, $"{draft.Minutes} мин", ct).ConfigureAwait(false);
                await ShowDraftAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "dmode":
            {
                var draft = GetDraft(chatId);
                draft.Mode = draft.Mode switch
                {
                    DraftMode.Add => DraftMode.Subtract,
                    DraftMode.Subtract => DraftMode.Set,
                    _ => DraftMode.Add
                };

                // В режиме правки удобнее стартовать от текущего остатка.
                if (draft.Mode == DraftMode.Set)
                    draft.Minutes = Math.Clamp((int)Math.Round(_bank.RemainingSeconds / 60.0), 0, MaxDraftMinutes);

                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await ShowDraftAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "dapply":
            {
                var draft = GetDraft(chatId);
                var kind = draft.Mode switch
                {
                    DraftMode.Subtract => "sub",
                    DraftMode.Set => "set",
                    _ => "add"
                };
                var argument = draft.Minutes * 60L;

                MarkBusy(chatId, true);
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await EditAsync(chatId, messageId, BuildConfirmText(kind, argument), Keyboards.Confirm(kind, argument), ct).ConfigureAwait(false);
                return;
            }

            case "settings":
            {
                MarkBusy(chatId, true);
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await ShowSettingsAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "toggle":
            {
                var what = parts.Length > 1 ? parts[1] : string.Empty;
                string toast;

                switch (what)
                {
                    case "alarms":
                        _config.AlarmsEnabled = !_config.AlarmsEnabled;
                        toast = _config.AlarmsEnabled ? "Сигнализация включена" : "Сигнализация выключена";
                        break;
                    default:
                        toast = string.Empty;
                        break;
                }

                _config.Save();
                await _client.AnswerCallbackAsync(callback.Id, toast, ct).ConfigureAwait(false);
                await ShowSettingsAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "alerts":
            {
                MarkBusy(chatId, true);
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await ShowAlertsAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "alert":
            {
                if (parts.Length > 1 && Enum.TryParse<AlertKind>(parts[1], out var kind))
                {
                    var settings = _config.GetAlerts(chatId);
                    settings.Toggle(kind);
                    _config.Save();

                    await _client.AnswerCallbackAsync(callback.Id,
                        $"{AlertSettings.Caption(kind)}: {(settings.IsEnabled(kind) ? "включено" : "выключено")}",
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                }

                await ShowAlertsAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "track":
            {
                if (parts.Length > 1 && Enum.TryParse<TrackingMode>(parts[1], out var mode))
                {
                    _config.Tracking = mode;
                    _config.Save();
                }

                await _client.AnswerCallbackAsync(callback.Id, TrackingCaption(_config.Tracking), ct).ConfigureAwait(false);
                await ShowSettingsAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "idle":
            {
                if (parts.Length > 1 && int.TryParse(parts[1], out var seconds))
                {
                    _config.IdlePauseSeconds = Math.Clamp(seconds, 0, 3600);
                    _config.Save();
                }

                await _client.AnswerCallbackAsync(callback.Id,
                    _config.IdlePauseSeconds > 0
                        ? $"Пауза через {_config.IdlePauseSeconds} с простоя"
                        : "Простой не учитывается",
                    ct).ConfigureAwait(false);
                await ShowSettingsAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }

            case "parents":
            {
                MarkBusy(chatId, true);
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await EditAsync(chatId, messageId, BuildParentsText(), Keyboards.Parents(_config.ParentChatIds, chatId), ct).ConfigureAwait(false);
                return;
            }

            case "invite":
            {
                var sent = await _client.SendMessageAsync(chatId, BuildInviteText(), null, ct).ConfigureAwait(false);
                await _client.AnswerCallbackAsync(callback.Id,
                    sent is null ? "Не удалось отправить код, смотрите журнал" : "Код отправлен",
                    ct).ConfigureAwait(false);
                return;
            }

            case "unbind":
            {
                var removed = parts.Length > 1 && long.TryParse(parts[1], out var target) && _config.RemoveParent(target);
                await _client.AnswerCallbackAsync(callback.Id, removed ? "Отвязан" : "Не найден", ct).ConfigureAwait(false);
                await EditAsync(chatId, messageId, BuildParentsText(), Keyboards.Parents(_config.ParentChatIds, chatId), ct).ConfigureAwait(false);
                return;
            }

            default:
            {
                await _client.AnswerCallbackAsync(callback.Id, null, ct).ConfigureAwait(false);
                await ShowPanelAsync(chatId, messageId, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Применяет действие и возвращает короткий текст для всплывающей подсказки.</summary>
    private string Apply(string kind, long argument)
    {
        switch (kind)
        {
            case "add":
                _bank.Add(argument);
                return $"Добавлено {TimeFormat.Human(argument)}";
            case "sub":
                _bank.Add(-argument);
                return $"Списано {TimeFormat.Human(argument)}";
            case "set":
                _bank.Set(argument);
                return $"Выставлено {TimeFormat.Human(argument)}";
            case "free":
                _bank.SetUnlimited(true);
                return "Безлимит включён";
            case "unfree":
                _bank.SetUnlimited(false);
                return "Лимит вернулся";
            case "lock":
                _bank.LockNow();
                return "Заблокировано";
            default:
                return string.Empty;
        }
    }

    /// <summary>Показывает экран подтверждения, а без подтверждений применяет сразу.</summary>
    private async Task RequestAsync(long chatId, string kind, long argument, CancellationToken ct)
    {
        if (!_config.ConfirmActions)
        {
            if (kind == "quit")
            {
                await QuitAsync(chatId, 0, ct).ConfigureAwait(false);
                return;
            }

            Apply(kind, argument);
            await SendPanelAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        await _client.SendMessageAsync(chatId, BuildConfirmText(kind, argument), Keyboards.Confirm(kind, argument), ct).ConfigureAwait(false);
    }

    private void MarkBusy(long chatId, bool busy)
    {
        lock (_busySync)
        {
            if (busy) _busyChats.Add(chatId);
            else _busyChats.Remove(chatId);
        }
    }

    private bool IsBusy(long chatId)
    {
        lock (_busySync) return _busyChats.Contains(chatId);
    }

    /// <summary>Сообщает о выходе и только потом просит приложение закрыться.</summary>
    private async Task QuitAsync(long chatId, long messageId, CancellationToken ct)
    {
        const string text = "🚪 Приложение закрыто на компьютере.\n\n" +
                            "Пока оно не запущено, ограничение не действует. " +
                            "Запустите KidRemote снова, чтобы вернуть контроль.";

        if (messageId != 0)
            await EditAsync(chatId, messageId, text, null, ct).ConfigureAwait(false);
        else
            await _client.SendMessageAsync(chatId, text, null, ct).ConfigureAwait(false);

        await BroadcastAsync("🚪 KidRemote закрыт на компьютере.", chatId, ct).ConfigureAwait(false);

        ShutdownRequested?.Invoke();
    }

    private Draft GetDraft(long chatId)
    {
        if (!_drafts.TryGetValue(chatId, out var draft))
        {
            draft = new Draft();
            _drafts[chatId] = draft;
        }

        return draft;
    }

    private string BuildConfirmText(string kind, long argument)
    {
        var remaining = _bank.RemainingSeconds;
        var unlimited = _bank.IsUnlimited;
        var sb = new StringBuilder();

        switch (kind)
        {
            case "add":
                sb.AppendLine($"Добавить <b>{TimeFormat.Human(argument)}</b>?");
                sb.AppendLine($"Было {TimeFormat.Compact(remaining)} → станет {TimeFormat.Compact(remaining + argument)}");
                if (unlimited) sb.AppendLine("Безлимит отключится, вернётся обычный отсчёт.");
                break;
            case "sub":
                sb.AppendLine($"Списать <b>{TimeFormat.Human(argument)}</b>?");
                sb.AppendLine($"Было {TimeFormat.Compact(remaining)} → станет {TimeFormat.Compact(Math.Max(0, remaining - argument))}");
                if (unlimited) sb.AppendLine("Безлимит отключится, вернётся обычный отсчёт.");
                break;
            case "set":
                sb.AppendLine($"Выставить ровно <b>{TimeFormat.Human(argument)}</b>?");
                sb.AppendLine($"Сейчас {TimeFormat.Compact(remaining)}");
                if (unlimited) sb.AppendLine("Безлимит отключится, вернётся обычный отсчёт.");
                break;
            case "free":
                sb.AppendLine("Включить безлимит?");
                sb.AppendLine("Блокировки не будет, пока не отмените.");
                if (remaining > 0) sb.AppendLine($"Текущий остаток {TimeFormat.Human(remaining)} сгорит.");
                break;
            case "unfree":
                sb.AppendLine("Вернуть лимит?");
                sb.AppendLine("Компьютер заблокируется сразу — время выдаётся заново.");
                break;
            case "lock":
                sb.AppendLine("Заблокировать прямо сейчас?");
                sb.AppendLine($"Остаток {TimeFormat.Human(remaining)} сгорит.");
                break;
            case "quit":
                sb.AppendLine("Закрыть приложение на компьютере?");
                sb.AppendLine("Ограничение перестанет действовать, пока вы не запустите его снова.");
                break;
            default:
                sb.AppendLine("Подтвердить действие?");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildDraftText(long chatId)
    {
        var draft = GetDraft(chatId);
        var remaining = _bank.RemainingSeconds;
        var value = draft.Minutes * 60L;

        var sb = new StringBuilder();
        sb.AppendLine("<b>Точная настройка</b>");
        sb.AppendLine();
        sb.AppendLine($"Значение: <b>{draft.Minutes} мин</b>");

        switch (draft.Mode)
        {
            case DraftMode.Subtract:
                sb.AppendLine("Режим: вычесть из остатка");
                sb.AppendLine($"Станет {TimeFormat.Compact(Math.Max(0, remaining - value))}");
                break;
            case DraftMode.Set:
                sb.AppendLine($"Режим: выставить ровно (сейчас {TimeFormat.Compact(remaining)})");
                break;
            default:
                sb.AppendLine("Режим: добавить к остатку");
                sb.AppendLine($"Станет {TimeFormat.Compact(remaining + value)}");
                break;
        }

        if (_bank.IsUnlimited)
        {
            sb.AppendLine();
            sb.AppendLine("Сейчас безлимит — он отключится, как только выдадите время.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Перерисовывает панели во всех родительских чатах, чтобы остаток был виден на телефоне
    /// без нажатий. Редактирование сообщения не шлёт push, поэтому дёргать можно часто.
    /// </summary>
    public async Task RefreshPanelsAsync(bool force = false)
    {
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;

        var text = BuildStatusText();
        if (!force && text == _lastPanelText) return;
        _lastPanelText = text;

        var markup = Keyboards.Main(_bank.State);

        foreach (var (chatKey, messageId) in _state.PanelMessages.ToArray())
        {
            if (!long.TryParse(chatKey, out var chatId)) continue;
            if (IsBusy(chatId)) continue;
            await _client.EditMessageAsync(chatId, messageId, text, markup, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Сообщение о блокировке с кнопками быстрой добавки.</summary>
    public async Task NotifyTimeUpAsync()
    {
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;

        var snapshot = _activity();
        var sb = new StringBuilder();
        sb.AppendLine("🔒 <b>Время вышло</b>");
        sb.AppendLine("Компьютер заблокирован.");

        if (snapshot.ForegroundProcess is { Length: > 0 } process)
        {
            sb.AppendLine();
            sb.AppendLine($"Последнее приложение: {process}");
        }

        sb.AppendLine();
        sb.AppendLine("Можно добавить немного времени прямо отсюда.");

        var text = sb.ToString().TrimEnd();

        foreach (var chatId in _config.ParentChatIds.ToArray())
        {
            if (!_config.GetAlerts(chatId).IsEnabled(AlertKind.TimeUp)) continue;
            await _client.SendMessageAsync(chatId, text, Keyboards.QuickAdd(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Рассылает только тем родителям, у кого этот повод не выключен.</summary>
    public async Task NotifyAsync(AlertKind kind, string text)
    {
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;

        foreach (var chatId in _config.ParentChatIds.ToArray())
        {
            if (!_config.GetAlerts(chatId).IsEnabled(kind)) continue;
            await _client.SendMessageAsync(chatId, text, null, ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(string text, long exceptChatId, CancellationToken ct)
    {
        foreach (var chatId in _config.ParentChatIds.ToArray())
        {
            if (chatId == exceptChatId) continue;
            if (!_config.GetAlerts(chatId).IsEnabled(AlertKind.System)) continue;
            await _client.SendMessageAsync(chatId, text, null, ct).ConfigureAwait(false);
        }
    }

    private async Task SendPanelAsync(long chatId, CancellationToken ct)
    {
        MarkBusy(chatId, false);
        _lastPanelText = BuildStatusText();
        var messageId = await _client.SendMessageAsync(chatId, _lastPanelText, Keyboards.Main(_bank.State), ct).ConfigureAwait(false);
        if (messageId is null) return;

        RememberPanel(chatId, messageId.Value);
        _store.Save(_state);
    }

    private async Task ShowPanelAsync(long chatId, long messageId, CancellationToken ct)
    {
        if (messageId == 0) return;

        MarkBusy(chatId, false);
        RememberPanel(chatId, messageId);
        _lastPanelText = BuildStatusText();
        await EditAsync(chatId, messageId, _lastPanelText, Keyboards.Main(_bank.State), ct).ConfigureAwait(false);
    }

    private async Task ShowAlertsAsync(long chatId, long messageId, CancellationToken ct)
    {
        if (messageId == 0) return;

        MarkBusy(chatId, true);
        await EditAsync(chatId, messageId, BuildAlertsText(),
            Keyboards.Alerts(_config.GetAlerts(chatId)), ct).ConfigureAwait(false);
    }

    private static string BuildAlertsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>Мои уведомления</b>");
        sb.AppendLine();
        sb.AppendLine("Настройки личные: у второго родителя свой набор.");
        sb.AppendLine("Колокольчик — сообщения приходят, перечёркнутый — нет.");
        sb.AppendLine();
        sb.AppendLine("Изначально выключено всё. Панель управления обновляется в любом случае.");
        return sb.ToString().TrimEnd();
    }

    private async Task ShowSettingsAsync(long chatId, long messageId, CancellationToken ct)
    {
        if (messageId == 0) return;

        MarkBusy(chatId, true);
        await EditAsync(chatId, messageId, BuildSettingsText(),
            Keyboards.Settings(_config.Tracking, _config.AlarmsEnabled, _config.IdlePauseSeconds),
            ct).ConfigureAwait(false);
    }

    private string BuildSettingsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>Настройки</b>");
        sb.AppendLine();
        sb.AppendLine($"Время идёт: <b>{TrackingCaption(_config.Tracking)}</b>");
        sb.AppendLine(TrackingHint(_config.Tracking));
        sb.AppendLine();
        sb.AppendLine(_config.AlarmsEnabled
            ? "На последней минуте звучит сигнал и мигает красная рамка."
            : "Сигнал и рамка отключены, остаётся только таймер в углу.");
        sb.AppendLine();
        sb.AppendLine(_config.IdlePauseSeconds > 0
            ? $"Если ничего не нажимать {_config.IdlePauseSeconds} с, отсчёт замирает."
            : "Простой не учитывается: время идёт, пока открыта игра.");
        sb.AppendLine();
        sb.AppendLine(_config.HasPassword
            ? "Пароль на настройки в трее: задан."
            : "Пароль на настройки в трее: не задан, пункт меню закрыт. Команда /password.");
        return sb.ToString().TrimEnd();
    }

    private async Task ShowDraftAsync(long chatId, long messageId, CancellationToken ct)
    {
        if (messageId == 0) return;
        MarkBusy(chatId, true);
        await EditAsync(chatId, messageId, BuildDraftText(chatId), Keyboards.Draft(GetDraft(chatId).Mode), ct).ConfigureAwait(false);
    }

    private Task EditAsync(long chatId, long messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct) =>
        messageId == 0 ? Task.CompletedTask : _client.EditMessageAsync(chatId, messageId, text, markup, ct);

    private void RememberPanel(long chatId, long messageId)
    {
        _state.PanelMessages[chatId.ToString()] = messageId;
    }

    private string BuildStatusText()
    {
        var snapshot = _activity();
        var sb = new StringBuilder();
        sb.AppendLine("<b>KidRemote</b>");
        sb.AppendLine();

        switch (_bank.State)
        {
            case BankState.Unlimited:
                sb.AppendLine("♾ Безлимит — блокировки нет");
                sb.AppendLine("Выдача времени вернёт обычный отсчёт.");
                break;
            case BankState.Locked:
                sb.AppendLine("🔒 Время вышло — экран заблокирован");
                break;
            case BankState.Running:
                sb.AppendLine($"⏳ Осталось: <b>{TimeFormat.Compact(_bank.RemainingSeconds)}</b>  ({TimeFormat.Human(_bank.RemainingSeconds)})");
                sb.AppendLine($"Сейчас: {snapshot.Explain()}");
                if (snapshot.ForegroundProcess is { Length: > 0 } process)
                    sb.AppendLine($"На экране: {process}");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Что именно приложение видит прямо сейчас — для разбора «почему время идёт».</summary>
    private string BuildDebugText()
    {
        var snapshot = _activity();
        var sb = new StringBuilder();

        sb.AppendLine("<b>Что видит приложение</b>");
        sb.AppendLine();
        sb.AppendLine($"Окно: <code>{snapshot.ForegroundProcess ?? "нет"}</code>");
        sb.AppendLine($"Опознано как игра: {(snapshot.KnownGame ? "да" : "нет")}");
        sb.AppendLine($"Засчитывается время: {(snapshot.GameActive ? "да" : "нет")}");
        sb.AppendLine($"Ввод недавно был: {(snapshot.UserActive ? "да" : "нет")}");
        sb.AppendLine($"Сессия активна: {(snapshot.SessionActive ? "да" : "нет")}");
        sb.AppendLine();
        sb.AppendLine($"Итог: {snapshot.Explain()}");
        sb.AppendLine();
        sb.AppendLine($"Режим: {TrackingCaption(_config.Tracking)}");

        return sb.ToString().TrimEnd();
    }

    private static string TrackingCaption(TrackingMode mode) => mode switch
    {
        TrackingMode.Always => "всегда",
        TrackingMode.Fullscreen => "только полноэкранные",
        _ => "все игры"
    };

    private static string TrackingHint(TrackingMode mode) => mode switch
    {
        TrackingMode.Always => "Считается любое использование компьютера.",
        TrackingMode.Fullscreen => "Считается только приложение, развёрнутое на весь монитор.",
        _ => "Считаются полноэкранные приложения и опознанные игры, в том числе оконные. Лаунчеры не в счёт."
    };

    private string BuildParentsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>Родители</b>");
        sb.AppendLine();
        foreach (var id in _config.ParentChatIds)
            sb.AppendLine($"• <code>{id}</code>");

        sb.AppendLine();
        sb.AppendLine("Чтобы добавить ещё одного — «Пригласить» и передайте код.");
        return sb.ToString().TrimEnd();
    }

    private string BuildInviteText()
    {
        _inviteCode = Random.Shared.Next(1000, 9999).ToString();
        _inviteExpiresUtc = DateTime.UtcNow + InviteLifetime;

        return $"Код приглашения: <code>{_inviteCode}</code>\n\n" +
               $"Действует {InviteLifetime.TotalMinutes:0} минут. Второй родитель должен найти этого же бота и отправить:\n" +
               $"<code>/join {_inviteCode}</code>";
    }

    private bool IsInviteValid(string code) =>
        !string.IsNullOrEmpty(_inviteCode) &&
        DateTime.UtcNow <= _inviteExpiresUtc &&
        string.Equals(code.Trim(), _inviteCode, StringComparison.Ordinal);

    /// <summary>Понимает "+30", "-15", "=1:20", "+1:05".</summary>
    private static bool TryParseBalanceEdit(string text, out long seconds, out bool absolute)
    {
        seconds = 0;
        absolute = false;

        var match = BalanceRegex().Match(text.Replace(" ", string.Empty));
        if (!match.Success) return false;

        if (!TryParseDuration(match.Groups["value"].Value, out var parsed)) return false;

        switch (match.Groups["sign"].Value)
        {
            case "+":
                seconds = parsed;
                return true;
            case "-":
            case "−":
                seconds = -parsed;
                return true;
            case "=":
                seconds = parsed;
                absolute = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>"30" — минуты, "1:20" — час двадцать.</summary>
    private static bool TryParseDuration(string text, out long seconds)
    {
        seconds = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        var parts = text.Split(':');
        if (parts.Length == 1)
        {
            if (!long.TryParse(parts[0], out var minutes)) return false;
            seconds = minutes * 60;
            return true;
        }

        if (parts.Length == 2 && long.TryParse(parts[0], out var hours) && long.TryParse(parts[1], out var mins))
        {
            seconds = hours * 3600 + mins * 60;
            return true;
        }

        return false;
    }

    private const string HelpText =
        "<b>Команды</b>\n" +
        "<code>+30</code> — добавить 30 минут\n" +
        "<code>+1:30</code> — добавить час тридцать\n" +
        "<code>-15</code> — списать 15 минут\n" +
        "<code>=45</code> — выставить ровно 45 минут\n" +
        "/free, /limit — безлимит и возврат к лимиту\n" +
        "/lock — заблокировать сейчас\n" +
        "/menu — панель с кнопками\n" +
        "/invite — код для второго родителя\n" +
        "/password — пароль на настройки в трее\n" +
        "/debug — что приложение видит на экране сейчас\n" +
        "/quit — закрыть приложение на компьютере\n\n" +
        "Любое изменение сначала показывает экран подтверждения.";

    [GeneratedRegex(@"^(?<sign>[+\-−=])(?<value>\d{1,3}(:\d{1,2})?)$")]
    private static partial Regex BalanceRegex();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.Dispose();
    }
}
