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

    private readonly Stopwatch _tickWatch = Stopwatch.StartNew();
    private DispatcherTimer? _ticker;
    private DispatcherTimer? _persistTimer;
    private DispatcherTimer? _panelTimer;
    private DispatcherTimer? _guardTimer;

    private long _lastRenderedSeconds = -1;
    private BankState _lastState = BankState.Locked;
    private bool? _lastConsuming;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        _bank.Restore(_state.RemainingSeconds, _state.Paused, _state.Unlimited);
        _bank.Changed += OnBankChanged;

        _activity = new ActivityMonitor(_config);
        _overlay = new OverlayManager(_config);

        _countdown = new CountdownWindow(_config);
        _countdown.Show();

        _frame = new AlertFrameWindow();

        _tray = new TrayIcon();
        _tray.OpenConfigRequested += OpenConfig;
        _tray.ExitRequested += OnTrayExitRequested;

        _bot = new BotService(_config, _bank, _store, _state, () => _activity.Capture());
        _bot.ShutdownRequested += RequestShutdown;
        _bot.Start();

        if (_config.WatchdogEnabled) Watchdog.EnsureGuardRunning();

        StartTimers();
        RenderAll(force: true);

        _tray.ShowMessage("KidRemote",
            _config.NeedsParentBinding
                ? "Напишите боту любое сообщение — этот чат станет родительским."
                : "Работает. Значок в трее показывает остаток времени.");
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

        var snapshot = _activity.Capture();
        if (delta <= MaxTrustedDeltaSeconds && snapshot.ShouldConsume)
            _bank.Consume(delta);

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
        var consumingChanged = _lastConsuming != snapshot.ShouldConsume;

        if (!force && !valueChanged && !stateChanged && !consumingChanged) return;
        _lastConsuming = snapshot.ShouldConsume;

        var blocked = state is BankState.Locked or BankState.Paused;
        var danger = state == BankState.Running && snapshot.ShouldConsume
                     && remaining > 0 && remaining <= _config.DangerSeconds;

        _frame.SetActive(danger);

        if (blocked)
        {
            _overlay.Show(state);
            if (_countdown.IsVisible) _countdown.Hide();
        }
        else
        {
            _overlay.Hide();
            if (!_countdown.IsVisible) _countdown.Show();
            _countdown.Render(state, remaining, snapshot.ShouldConsume, valueChanged);
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
            _ = _bot.NotifyAsync($"⏳ У ребёнка осталось {TimeFormat.Human(remaining)}.");

        _lastRenderedSeconds = remaining;
    }

    /// <summary>
    /// Звук и вспышка рамки: последние 10 секунд — каждую секунду, до этого в красной зоне —
    /// раз в 10 секунд, в жёлтой — на каждой минуте.
    /// </summary>
    private void Signal(long remaining)
    {
        if (remaining <= 0) return;

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
            _ = _bot.NotifyAsync("🔒 Время вышло, компьютер заблокирован.");
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
        _state.Paused = _bank.IsPaused;
        _state.Unlimited = _bank.IsUnlimited;
        _store.Save(_state);
    }

    private void OnTrayExitRequested()
    {
        if (!_config.AllowTrayExit)
        {
            _tray.ShowMessage("KidRemote", "Выход отключён. Закрыть приложение можно командой /quit в боте.");
            return;
        }

        RequestShutdown();
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

    private void OpenConfig()
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

        OpenConfig();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

        _bot?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        _activity?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);

        // Страховка от чужих фоновых потоков, удерживающих процесс живым.
        Environment.Exit(0);
    }
}
