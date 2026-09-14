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
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(15);

    private readonly AppConfig _config;
    private readonly TimeBank _bank;
    private readonly StateStore _store;
    private readonly PersistedState _state;
    private readonly Func<ActivitySnapshot> _activity;
    private readonly TelegramClient _client;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Родитель попросил закрыть приложение командой /quit.</summary>
    public event Action? ShutdownRequested;

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

        if (TryParseBalanceEdit(text, out var deltaSeconds, out var absolute))
        {
            if (absolute) _bank.Set(deltaSeconds);
            else _bank.Add(deltaSeconds);

            await SendPanelAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        var command = text.Split(' ', 2)[0].ToLowerInvariant();
        var argument = text.Contains(' ') ? text[(text.IndexOf(' ') + 1)..].Trim() : string.Empty;

        switch (command)
        {
            case "/start":
            case "/menu":
            case "/status":
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/add" when TryParseDuration(argument, out var add):
                _bank.Add(add);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/sub" when TryParseDuration(argument, out var sub):
                _bank.Add(-sub);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/set" when TryParseDuration(argument, out var set):
                _bank.Set(set);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/pause":
                _bank.SetPaused(true);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/resume":
                _bank.SetPaused(false);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/free":
                _bank.SetUnlimited(true);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/limit":
                _bank.SetUnlimited(false);
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/lock":
                _bank.LockNow();
                await SendPanelAsync(chatId, ct).ConfigureAwait(false);
                break;

            case "/invite":
                await _client.SendMessageAsync(chatId, BuildInviteText(), null, ct).ConfigureAwait(false);
                break;

            case "/quit":
                await _client.SendMessageAsync(chatId, "Закрываю приложение на компьютере.", null, ct).ConfigureAwait(false);
                ShutdownRequested?.Invoke();
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
                "Готово, этот чат привязан как родительский.\n\nВторого родителя добавьте кнопкой «👪 Родители».",
                null, ct).ConfigureAwait(false);
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
            await _client.SendMessageAsync(chatId, "Код принят. Теперь вы тоже можете управлять временем.", null, ct).ConfigureAwait(false);
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
        if (chatId == 0 || !_config.IsParent(chatId))
        {
            await _client.AnswerCallbackAsync(callback.Id, "Нет доступа", ct).ConfigureAwait(false);
            return;
        }

        var data = callback.Data ?? string.Empty;
        string? toast = null;
        var showParents = false;

        if (data.StartsWith("add:", StringComparison.Ordinal) && long.TryParse(data[4..], out var add))
        {
            _bank.Add(add);
            toast = $"Добавлено {TimeFormat.Human(add)}";
        }
        else if (data.StartsWith("sub:", StringComparison.Ordinal) && long.TryParse(data[4..], out var sub))
        {
            _bank.Add(-sub);
            toast = $"Списано {TimeFormat.Human(sub)}";
        }
        else if (data.StartsWith("unbind:", StringComparison.Ordinal) && long.TryParse(data[7..], out var unbind))
        {
            toast = _config.RemoveParent(unbind) ? "Отвязан" : "Не найден";
            showParents = true;
        }
        else
        {
            switch (data)
            {
                case "pause":
                    _bank.SetPaused(true);
                    toast = "Пауза";
                    break;
                case "resume":
                    _bank.SetPaused(false);
                    toast = "Продолжаем";
                    break;
                case "free":
                    _bank.SetUnlimited(true);
                    toast = "Безлимит включён";
                    break;
                case "unfree":
                    _bank.SetUnlimited(false);
                    toast = "Лимит вернулся";
                    break;
                case "lock":
                    _bank.LockNow();
                    toast = "Заблокировано";
                    break;
                case "parents":
                    showParents = true;
                    break;
                case "invite":
                    await _client.SendMessageAsync(chatId, BuildInviteText(), null, ct).ConfigureAwait(false);
                    toast = "Код отправлен";
                    showParents = true;
                    break;
            }
        }

        await _client.AnswerCallbackAsync(callback.Id, toast, ct).ConfigureAwait(false);

        var messageId = callback.Message?.MessageId ?? 0;
        if (messageId == 0) return;

        if (showParents)
        {
            await _client.EditMessageAsync(chatId, messageId, BuildParentsText(),
                Keyboards.Parents(_config.ParentChatIds, chatId), ct).ConfigureAwait(false);
        }
        else
        {
            RememberPanel(chatId, messageId);
            await _client.EditMessageAsync(chatId, messageId, BuildStatusText(),
                Keyboards.Main(_bank.State), ct).ConfigureAwait(false);
        }
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
            await _client.EditMessageAsync(chatId, messageId, text, markup, ct).ConfigureAwait(false);
        }
    }

    public async Task NotifyAsync(string text)
    {
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;

        foreach (var chatId in _config.ParentChatIds.ToArray())
        {
            await _client.SendMessageAsync(chatId, text, null, ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(string text, long exceptChatId, CancellationToken ct)
    {
        foreach (var chatId in _config.ParentChatIds.ToArray())
        {
            if (chatId == exceptChatId) continue;
            await _client.SendMessageAsync(chatId, text, null, ct).ConfigureAwait(false);
        }
    }

    private async Task SendPanelAsync(long chatId, CancellationToken ct)
    {
        _lastPanelText = BuildStatusText();
        var messageId = await _client.SendMessageAsync(chatId, _lastPanelText, Keyboards.Main(_bank.State), ct).ConfigureAwait(false);
        if (messageId is null) return;

        RememberPanel(chatId, messageId.Value);
        _store.Save(_state);
    }

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
                break;
            case BankState.Paused:
                sb.AppendLine("⏸ Пауза — экран заблокирован, время не расходуется");
                sb.AppendLine($"⏳ Осталось: <b>{TimeFormat.Compact(_bank.RemainingSeconds)}</b>");
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
        "/pause, /resume — пауза и продолжение\n" +
        "/free, /limit — безлимит и возврат к лимиту\n" +
        "/lock — заблокировать сейчас\n" +
        "/menu — панель с кнопками\n" +
        "/invite — код для второго родителя\n" +
        "/quit — закрыть приложение на компьютере";

    [GeneratedRegex(@"^(?<sign>[+\-−=])(?<value>\d{1,3}(:\d{1,2})?)$")]
    private static partial Regex BalanceRegex();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.Dispose();
    }
}
