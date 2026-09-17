using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using KidRemote.Core;
using KidRemote.Telegram;
using KidRemote.Ui;

namespace KidRemote;

public partial class App : Application
{
    private const double TickIntervalMs = 250;

    /// <summary>Разрыв между тиками больше этого — значит был сон или зависание, время не списываем.</summary>
    private const double MaxTrustedDeltaSeconds = 2.0;

    /// <summary>Последние секунды отсчитываются посекундно.</summary>
    private const long FinalCountdownSeconds = 10;

    /// <summary>
    /// Разрыв между тиками больше этого означает, что система спала. Нужен потому, что
    /// в современном режиме ожидания событие питания может не прийти вовсе.
    /// </summary>
    private static readonly TimeSpan SleepGapThreshold = TimeSpan.FromSeconds(45);

    private Mutex? _singleInstance;
    private CancellationTokenSource? _guardCts;
    private bool _guardMode;

    private AppConfig _config = null!;
    private StateStore _store = null!;
    private PersistedState _state = null!;
    private TimeBank _bank = null!;
    private ActivityMonitor _activity = null!;
    private BotService _bot = null!;
    private TrayIcon _tray = null!;
    private CountdownWindow _countdown = null!;
    private AlertFrameWindow _frame = null!;
    private OverlayManager _overlay = null!;
    private ScheduleService _schedule = null!;
    private ChatLog _chat = null!;
    private ToastWindow? _toast;
    private HotkeyListener _hotkey = null!;

    private readonly Stopwatch _tickWatch = Stopwatch.StartNew();
    private DispatcherTimer? _ticker;
    private DispatcherTimer? _persistTimer;
    private DispatcherTimer? _panelTimer;
    private DispatcherTimer? _guardTimer;

    /// <summary>Сколько игра уже пробыла на переднем плане, по идентификатору процесса.</summary>
    private readonly Dictionary<uint, double> _gamePresence = new();

    private static readonly TimeSpan ChildMessageCooldown = TimeSpan.FromSeconds(60);

    private DateTime _lastChildMessageUtc = DateTime.MinValue;
    private bool _settling;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private DateTime _lastWakeUtc = DateTime.MinValue;
    private long _lastRenderedSeconds = -1;
    private BankState _lastState = BankState.Locked;
    private bool? _lastConsuming;
    private bool _lastSettling;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HookFailureLogging();

        // Сторожевой режим: ни окон, ни трея — только присмотр за основным процессом.
        if (e.Args.Any(a => string.Equals(a, Watchdog.GuardArgument, StringComparison.OrdinalIgnoreCase)))
        {
            StartGuardMode();
            return;
        }

        _singleInstance = new Mutex(true, Watchdog.MainMutexName, out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        Watchdog.ClearShutdownFlag();

        _config = AppConfig.LoadOrCreate();
        if (string.IsNullOrWhiteSpace(_config.ResolvedToken))
        {
            // Без токена приложение бесполезно, поэтому сторожу тоже незачем его поднимать.
            Watchdog.AllowShutdown();
            PromptForToken();
            Shutdown();
            return;
        }

        Autostart.Apply(_config.Autostart);

        _store = new StateStore();
        _state = _store.Load();

        _bank = new TimeBank();
        _bank.Restore(_state.RemainingSeconds, _state.Unlimited);
        _bank.Changed += OnBankChanged;

        _activity = new ActivityMonitor(_config);
        _activity.SystemResumed += OnSystemResumed;
        _overlay = new OverlayManager(_config);

        _countdown = new CountdownWindow(_config);
        _countdown.Show();

        _frame = new AlertFrameWindow();

        _tray = new TrayIcon(_config.CountdownMode);
        _tray.OpenConfigRequested += OpenConfig;
        _tray.ExitRequested += ExitByPassword;
        _tray.CountdownModeChanged += OnCountdownModeChanged;
        _tray.MessageRequested += SendMessageToParents;

        _hotkey = new HotkeyListener();
        _hotkey.Pressed += OnUnlockHotkey;

        _bot = new BotService(_config, _bank, _store, _state, () => _activity.Capture(), _chat);
        _bot.ShutdownRequested += RequestShutdown;
        _bot.Start();

        if (_config.WatchdogEnabled) Watchdog.EnsureGuardRunning();

        Log.Write($"запуск: остаток {_bank.RemainingSeconds} с, безлимит {_bank.IsUnlimited}");

        StartTimers();
        ApplyResumeGrant();
        RenderAll(force: true);

        _ = _bot.NotifyAsync(AlertKind.Startup, BuildResumeText("💻 Компьютер включён"));

        _tray.ShowMessage("KidRemote",
            _config.NeedsParentBinding
                ? "Напишите боту любое сообщение — этот чат станет родительским."
                : "Работает. Значок в трее показывает остаток времени.");
    }

    /// <summary>
    /// Включение и пробуждение снимают безлимит: режим «играй сколько хочешь» не должен
    /// переживать сон. Обычный остаток при этом сохраняется — выданные минуты не сгорают.
    /// </summary>
    private void ApplyResumeGrant()
    {
        if (!_bank.IsUnlimited) return;

        _bank.Set(Math.Max(0, _config.ResumeGrantSeconds));
        Persist();
    }

    private string BuildResumeText(string headline)
    {
        var remaining = _bank.IsUnlimited
            ? "безлимит"
            : TimeFormat.Human(_bank.RemainingSeconds);

        return $"{headline}\nОсталось: {remaining}";
    }

    private void OnSystemResumed() => Dispatcher.BeginInvoke(() => HandleWake("событие питания"));

    /// <summary>
    /// Пробуждение приходит двумя путями — событием питания и разрывом в тиках.
    /// Второй раз подряд отрабатывать его незачем.
    /// </summary>
    private void HandleWake(string reason)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWakeUtc < TimeSpan.FromSeconds(15)) return;
        _lastWakeUtc = now;

        Log.Write($"пробуждение: {reason}");

        ApplyResumeGrant();
        RenderAll(force: true);

        _ = _bot.NotifyAsync(AlertKind.Wake, BuildResumeText("⏰ Компьютер проснулся"));
        _ = _bot.RefreshPanelsAsync(force: true);
    }

    /// <summary>
    /// Без этого падение выглядит как «приложение само перезапустилось»: сторож поднимает
    /// процесс, а причина нигде не остаётся.
    /// </summary>
    private void HookFailureLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"сбой в интерфейсе: {args.Exception}");

            // Приложение должно пережить единичную ошибку отрисовки, а не уходить в перезапуск.
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"необработанный сбой: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"сбой в фоновой задаче: {args.Exception}");
            args.SetObserved();
        };
    }

    private void StartGuardMode()
    {
        _guardMode = true;
        _guardCts = new CancellationTokenSource();
        var token = _guardCts.Token;

        Task.Run(() =>
        {
            Watchdog.RunGuard(token);
            Dispatcher.BeginInvoke(() => Shutdown());
        });
    }

    private void StartTimers()
    {
        _ticker = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(TickIntervalMs)
        };
        _ticker.Tick += OnTick;
        _ticker.Start();

        _persistTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _persistTimer.Tick += (_, _) => Persist();
        _persistTimer.Start();

        // Панель в боте перерисовывается сама — родитель видит остаток на телефоне без нажатий.
        _panelTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _panelTimer.Tick += (_, _) => _ = _bot.RefreshPanelsAsync();
        _panelTimer.Start();

        if (!_config.WatchdogEnabled) return;

        _guardTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _guardTimer.Tick += (_, _) => Watchdog.EnsureGuardRunning();
        _guardTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _tickWatch.Elapsed.TotalSeconds;
        _tickWatch.Restart();

        // Таймер не тикает, пока машина спит: большая дыра во времени и есть признак сна.
        var now = DateTime.UtcNow;
        var gap = now - _lastTickUtc;
        _lastTickUtc = now;

        if (gap > SleepGapThreshold) HandleWake($"разрыв {gap.TotalSeconds:0} с");

        var snapshot = _activity.Capture();
        var settled = snapshot.ShouldConsume && HasSettledIn(snapshot, delta);

        if (delta <= MaxTrustedDeltaSeconds && settled)
            _bank.Consume(delta);

        // Игра запущена, но фора ещё не вышла — показываем это отдельным цветом.
        _settling = snapshot.ShouldConsume && !settled;

        _countdown.UpdateCursorProximity();
        Render(snapshot);
    }

    private void Render(ActivitySnapshot snapshot, bool force = false)
    {
        var state = _bank.State;
        var remaining = _bank.RemainingSeconds;
        var valueChanged = remaining != _lastRenderedSeconds;
        var stateChanged = state != _lastState;
        // Смена режима расхода меняет цвет плашки, хотя остаток при этом стоит на месте.
        var consumingChanged = _lastConsuming != snapshot.ShouldConsume || _lastSettling != _settling;

        if (!force && !valueChanged && !stateChanged && !consumingChanged) return;
        _lastConsuming = snapshot.ShouldConsume;
        _lastSettling = _settling;

        var blocked = state == BankState.Locked;
        var danger = _config.AlarmsEnabled && state == BankState.Running && snapshot.ShouldConsume
                     && remaining > 0 && remaining <= _config.DangerSeconds;

        _frame.SetActive(danger);

        if (blocked)
        {
            _overlay.SetChat(_chat.Tail(4));
            _overlay.Show();
            if (_countdown.IsVisible) _countdown.Hide();
        }
        else
        {
            _overlay.Hide();

            if (ShouldShowCountdown(state, remaining))
            {
                if (!_countdown.IsVisible) _countdown.Show();
                _countdown.Render(state, remaining, snapshot.ShouldConsume, valueChanged, _settling);
            }
            else if (_countdown.IsVisible)
            {
                _countdown.Hide();
            }
        }

        if (valueChanged && state == BankState.Running && snapshot.ShouldConsume)
            Signal(remaining);

        _tray.Render(state, remaining);

        if (stateChanged)
        {
            OnStateTransition(_lastState, state);
            _lastState = state;
        }

        // Предупреждение родителям ровно один раз, на пересечении порога.
        if (valueChanged && state == BankState.Running && remaining == _config.WarnSeconds)
            _ = _bot.NotifyAsync(AlertKind.Warning, $"⏳ У ребёнка осталось {TimeFormat.Human(remaining)}.");

        _lastRenderedSeconds = remaining;
    }

    /// <summary>
    /// Первые секунды после запуска игры не списываются: загрузка, заставки и меню
    /// не должны съедать выданное время. Фора считается на процесс, поэтому свернуть
    /// и развернуть игру ради новой форы не выйдет.
    /// </summary>
    private bool HasSettledIn(ActivitySnapshot snapshot, double delta)
    {
        var required = Math.Max(0, _config.GameStartDelaySeconds);
        if (required == 0 || snapshot.ProcessId == 0) return true;

        _gamePresence.TryGetValue(snapshot.ProcessId, out var presence);
        presence += delta;
        _gamePresence[snapshot.ProcessId] = presence;

        // Идентификаторы завершённых процессов накапливаются — изредка чистим.
        if (_gamePresence.Count > 32) TrimPresence(snapshot.ProcessId);

        return presence >= required;
    }

    private void TrimPresence(uint keep)
    {
        var survivors = _gamePresence.Where(pair => pair.Key == keep).ToList();
        _gamePresence.Clear();

        foreach (var pair in survivors) _gamePresence[pair.Key] = pair.Value;
    }

    /// <summary>Чат с родителями. Пароля не требует — просить о помощи должно быть просто.</summary>
    private void SendMessageToParents()
    {
        CloseToast();

        // Пока открыт чат, экран блокировки не лезет наверх — владелец окну не нужен,
        // а привязка к окну, которое может закрыться, роняла диалог.
        _overlay.SuspendGuard();

        try
        {
            var window = new ChatWindow(_chat);
            if (window.ShowDialog() != true) return;

            var text = window.Text;
            if (text.Length == 0) return;

            var since = DateTime.UtcNow - _lastChildMessageUtc;
            if (since < ChildMessageCooldown)
            {
                _tray.ShowMessage("KidRemote",
                    $"Подождите {(int)(ChildMessageCooldown - since).TotalSeconds} с перед следующим сообщением.");
                return;
            }

            _lastChildMessageUtc = DateTime.UtcNow;
            _ = _bot.SendFromChildAsync(text);
            _tray.ShowMessage("KidRemote", "Сообщение отправлено.");
        }
        catch (Exception ex)
        {
            Log.Write($"чат: {ex}");
            _tray.ShowMessage("KidRemote", "Не удалось открыть чат, подробности в журнале.");
        }
        finally
        {
            _overlay.ResumeGuard();
        }
    }

    /// <summary>Ответ родителя: показываем карточкой поверх игры и обновляем экран блокировки.</summary>
    private void OnChatMessage(ChatMessage message)
    {
        if (!message.FromParent) return;

        Dispatcher.BeginInvoke(() =>
        {
            _overlay.SetChat(_chat.Tail(4));

            // На закрытом экране реплика уже видна в углу — карточка поверх была бы лишней
            // и мешала бы удержанию блокировки наверху.
            if (_overlay.IsVisible) return;

            CloseToast();

            _toast = new ToastWindow(message.Author, message.Text);
            _toast.Clicked += SendMessageToParents;
            _toast.Show();
        });
    }

    private void CloseToast()
    {
        _toast?.CloseQuietly();
        _toast = null;
    }

    private bool ShouldShowCountdown(BankState state, long remaining) => _config.CountdownMode switch
    {
        CountdownMode.Never => false,
        // На последней минуте плашка важнее всего, в остальное время не мозолит глаза.
        CountdownMode.LastMinute => state == BankState.Running && remaining > 0 && remaining <= _config.DangerSeconds,
        _ => true
    };

    private void OnScheduleUpdated() =>
        Dispatcher.BeginInvoke(() => _overlay.SetSchedule(_schedule.Title, _schedule.Lines));

    private void OnCountdownModeChanged(CountdownMode mode)
    {
        _config.CountdownMode = mode;
        _config.Save();
        RenderAll(force: true);
    }

    /// <summary>
    /// Звук и вспышка рамки: последние 10 секунд — каждую секунду, до этого в красной зоне —
    /// раз в 10 секунд, в жёлтой — на каждой минуте.
    /// </summary>
    private void Signal(long remaining)
    {
        if (remaining <= 0 || !_config.AlarmsEnabled) return;

        if (remaining <= FinalCountdownSeconds)
        {
            Alarm.LastMinute();
            _frame.Flash();
            return;
        }

        if (remaining <= _config.DangerSeconds)
        {
            if (remaining % 10 != 0) return;
            Alarm.LastMinute();
            _frame.Flash();
            return;
        }

        if (remaining <= _config.WarnSeconds && remaining % 60 == 0)
            Alarm.Minute();
    }

    private void OnStateTransition(BankState previous, BankState current)
    {
        Persist();
        _ = _bot.RefreshPanelsAsync(force: true);

        if (current == BankState.Locked && previous != BankState.Locked)
            _ = _bot.NotifyTimeUpAsync();
    }

    private void OnBankChanged()
    {
        // Команды из бота приходят из фонового потока, а окна трогать можно только из UI.
        Dispatcher.BeginInvoke(() => RenderAll(force: true));
    }

    private void RenderAll(bool force) => Render(_activity.Capture(), force);

    private void Persist()
    {
        _state.RemainingSeconds = _bank.RemainingSeconds;
        _state.Unlimited = _bank.IsUnlimited;
        _store.Save(_state);
    }

    internal void RequestShutdown() => Dispatcher.BeginInvoke(() =>
    {
        // Сторож не должен воскрешать приложение после осознанного выхода.
        Watchdog.AllowShutdown();

        // Экран блокировки отменяет закрытие окна, поэтому снимаем его до Shutdown,
        // иначе процесс остаётся висеть.
        _overlay?.Dispose();
        _frame?.SetActive(false);

        Shutdown();
    });

    /// <summary>
    /// Ctrl+Alt+F1 — разблокировка за компьютером, когда телефона под рукой нет.
    /// Работает только по родительскому паролю.
    /// </summary>
    private void OnUnlockHotkey()
    {
        if (!AskPassword("Разблокировка компьютера. Введите родительский пароль.")) return;

        _overlay.SuspendGuard();

        try
        {
            var unlock = new UnlockWindow(_bank.RemainingSeconds);
            if (unlock.ShowDialog() != true) return;

            if (unlock.Unlimited) _bank.SetUnlimited(true);
            else _bank.Add(unlock.Seconds);

            Persist();
            _ = _bot.RefreshPanelsAsync(force: true);
            _ = _bot.NotifyAsync(AlertKind.System, "🔓 Разблокировано с компьютера по паролю.");
        }
        finally
        {
            _overlay.ResumeGuard();
        }
    }

    /// <summary>Настройки из трея открываются только по родительскому паролю.</summary>
    private void OpenConfig()
    {
        if (!AskPassword("Введите родительский пароль, чтобы открыть файл настроек.")) return;
        OpenConfigFile();
    }

    /// <summary>Выход из трея тоже под паролем: иначе защита снимается одним кликом.</summary>
    private void ExitByPassword()
    {
        if (!AskPassword("Введите родительский пароль, чтобы закрыть KidRemote.")) return;

        // Даём уведомлению шанс уйти до того, как процесс завершится.
        try
        {
            _bot.NotifyAsync(AlertKind.System, "🚪 KidRemote закрыт с компьютера.").Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Нет сети — выходим всё равно.
        }

        RequestShutdown();
    }

    private bool AskPassword(string caption)
    {
        if (!_config.HasPassword)
        {
            _tray.ShowMessage("KidRemote",
                "Сначала задайте пароль в боте: /password ваш_пароль");
            return false;
        }

        _overlay.SuspendGuard();

        try
        {
            return new PasswordWindow(caption, _config.VerifyPassword).ShowDialog() == true;
        }
        finally
        {
            _overlay.ResumeGuard();
        }
    }

    private void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppConfig.ConfigPath) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(AppConfig.ConfigPath, "Файл настроек");
        }
    }

    private void PromptForToken()
    {
        MessageBox.Show(
            "Укажите токен бота в файле настроек и запустите приложение снова:\n\n" + AppConfig.ConfigPath,
            "KidRemote", MessageBoxButton.OK, MessageBoxImage.Information);

        OpenConfigFile();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_guardMode) Log.Write("завершение работы");

        if (_guardMode)
        {
            _guardCts?.Cancel();
            _guardCts?.Dispose();
            base.OnExit(e);
            return;
        }

        _ticker?.Stop();
        _persistTimer?.Stop();
        _panelTimer?.Stop();
        _guardTimer?.Stop();

        if (_bank is not null) Persist();

        // Ждём бота ограниченно: зависшее сетевое ожидание не должно держать процесс.
        try
        {
            _bot?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Останавливаемся в любом случае.
        }

        CloseToast();
        _bot?.Dispose();
        _schedule?.Dispose();
        _hotkey?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        _activity?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);

        // Страховка от чужих фоновых потоков, удерживающих процесс живым.
        Environment.Exit(0);
    }
}
