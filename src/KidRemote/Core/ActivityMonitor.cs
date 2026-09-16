using System.Diagnostics;
using KidRemote.Interop;
using Microsoft.Win32;

namespace KidRemote.Core;

internal sealed record ActivitySnapshot(
    bool SessionActive,
    bool SystemAwake,
    bool FullscreenApp,
    bool UserActive,
    string? ForegroundProcess)
{
    /// <summary>Время списывается только когда сошлось всё сразу.</summary>
    public bool ShouldConsume => SessionActive && SystemAwake && FullscreenApp && UserActive;

    public string Explain()
    {
        if (!SystemAwake) return "система в спящем режиме";
        if (!SessionActive) return "сессия заблокирована";
        if (!FullscreenApp) return "нет полноэкранного приложения";
        if (!UserActive) return "простой";
        return "идёт расход";
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
        var fullscreen = !_config.RequireFullscreen || IsFullscreen(foreground, out _);
        var processName = TryGetProcessName(foreground);

        return new ActivitySnapshot(
            SessionActive: _sessionActive,
            SystemAwake: _systemAwake,
            FullscreenApp: fullscreen,
            UserActive: _config.IdlePauseSeconds <= 0 || IdleSeconds() < _config.IdlePauseSeconds,
            ForegroundProcess: processName);
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
