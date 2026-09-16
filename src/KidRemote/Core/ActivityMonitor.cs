using System.Diagnostics;
using KidRemote.Interop;
using Microsoft.Win32;

namespace KidRemote.Core;

internal sealed record ActivitySnapshot(
    bool SessionActive,
    bool SystemAwake,
    bool GameActive,
    bool UserActive,
    string? ForegroundProcess,
    bool KnownGame,
    uint ProcessId)
{
    /// <summary>Время списывается только когда сошлось всё сразу.</summary>
    public bool ShouldConsume => SessionActive && SystemAwake && GameActive && UserActive;

    public string Explain()
    {
        if (!SystemAwake) return "система в спящем режиме";
        if (!SessionActive) return "сессия заблокирована";
        if (!GameActive) return "игра не запущена";
        if (!UserActive) return "простой";
        return KnownGame ? "идёт расход, игра опознана" : "идёт расход";
    }
}

/// <summary>
/// Следит за тем, играет ли ребёнок прямо сейчас: активная сессия, разбуженная система,
/// полноэкранное окно на переднем плане и недавний ввод с клавиатуры или мыши.
/// </summary>
internal sealed class ActivityMonitor : IDisposable
{
    private readonly AppConfig _config;
    private readonly uint _ownProcessId;

    // Снимок берётся четыре раза в секунду, а чтение пути процесса дорогое —
    // держим результат для текущего процесса на переднем плане.
    private uint _cachedPid;
    private string? _cachedName;
    private bool _cachedFromStore;
    private volatile bool _sessionActive = true;
    private volatile bool _systemAwake = true;
    private bool _disposed;

    public event Action? Resumed;

    /// <summary>Только выход из сна, без разблокировки сессии.</summary>
    public event Action? SystemResumed;

    public ActivityMonitor(AppConfig config)
    {
        _config = config;
        _ownProcessId = (uint)Environment.ProcessId;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public ActivitySnapshot Capture()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var (processName, fromStore, pid) = Describe(foreground);

        // Знакомую игру засчитываем и в окне: играют далеко не всегда на весь экран.
        var knownGame = _config.DetectKnownGames
                        && (fromStore || GameCatalog.IsGame(processName, _config.ExtraGames));

        var active = !_config.RequireFullscreen || knownGame || IsFullscreen(foreground, out _);

        return new ActivitySnapshot(
            SessionActive: _sessionActive,
            SystemAwake: _systemAwake,
            GameActive: active,
            UserActive: _config.IdlePauseSeconds <= 0 || IdleSeconds() < _config.IdlePauseSeconds,
            ForegroundProcess: processName,
            KnownGame: knownGame,
            ProcessId: pid);
    }

    public static double IdleSeconds()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return 0;

        // TickCount переполняется примерно раз в 49 дней — беззнаковая разность это переживает.
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return elapsed / 1000.0;
    }

    /// <summary>Окно занимает монитор целиком и не является рабочим столом или нашей собственной плашкой.</summary>
    public bool IsFullscreen(IntPtr hWnd, out string? processName)
    {
        processName = null;
        if (hWnd == IntPtr.Zero) return false;
        if (hWnd == NativeMethods.GetShellWindow() || hWnd == NativeMethods.GetDesktopWindow()) return false;

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == _ownProcessId) return false;

        if (!NativeMethods.GetWindowRect(hWnd, out var rect)) return false;

        var monitor = NativeMethods.MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return false;

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        // Небольшой допуск: борды у игр иногда отличаются на пиксель-другой.
        const int tolerance = 2;
        var covers =
            rect.Left <= info.rcMonitor.Left + tolerance &&
            rect.Top <= info.rcMonitor.Top + tolerance &&
            rect.Right >= info.rcMonitor.Right - tolerance &&
            rect.Bottom >= info.rcMonitor.Bottom - tolerance;

        if (covers) processName = TryGetProcessName(hWnd);
        return covers;
    }

    /// <summary>Имя процесса и признак «запущен из папки игрового магазина».</summary>
    private (string? Name, bool FromStore, uint Pid) Describe(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return (null, false, 0);

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0) return (null, false, 0);
        if (pid == _cachedPid) return (_cachedName, _cachedFromStore, pid);

        string? name = null;
        var fromStore = false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;

            // Путь читается не у всех процессов: у системных и у чужих учётных записей будет отказ.
            try
            {
                fromStore = GameCatalog.IsGamePath(process.MainModule?.FileName);
            }
            catch
            {
                fromStore = false;
            }
        }
        catch
        {
            name = null;
        }

        _cachedPid = pid;
        _cachedName = name;
        _cachedFromStore = fromStore;

        return (name, fromStore, pid);
    }

    private static string? TryGetProcessName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;

        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Log.Write($"сессия: {e.Reason}");

        _sessionActive = e.Reason switch
        {
            SessionSwitchReason.SessionLock => false,
            SessionSwitchReason.SessionLogoff => false,
            SessionSwitchReason.ConsoleDisconnect => false,
            SessionSwitchReason.RemoteDisconnect => false,
            SessionSwitchReason.SessionUnlock => true,
            SessionSwitchReason.SessionLogon => true,
            SessionSwitchReason.ConsoleConnect => true,
            SessionSwitchReason.RemoteConnect => true,
            _ => _sessionActive
        };

        if (_sessionActive) Resumed?.Invoke();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Log.Write($"питание: {e.Mode}");

        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _systemAwake = false;
                break;
            case PowerModes.Resume:
                _systemAwake = true;
                Resumed?.Invoke();
                SystemResumed?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
