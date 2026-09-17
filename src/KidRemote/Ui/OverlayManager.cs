using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using KidRemote.Core;
using KidRemote.Interop;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

/// <summary>
/// Экран блокировки: по одному окну на каждый монитор плюс удержание поверх всего.
/// Игры не закрываются — как только время добавили, ребёнок возвращается туда же, где был.
/// </summary>
internal sealed class OverlayManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly KeyboardBlocker _keyboard = new();
    private readonly List<LockOverlayWindow> _windows = new();
    private readonly DispatcherTimer _keepOnTop;
    private readonly DispatcherTimer _clock;

    /// <summary>Ребёнок отправил реплику прямо с экрана блокировки.</summary>
    public event Action<string>? MessageSubmitted;

    private bool _visible;
    private string _phrase = string.Empty;
    private string _scheduleTitle = string.Empty;
    private IReadOnlyList<string> _scheduleLines = Array.Empty<string>();
    private IReadOnlyList<ChatMessage> _chatMessages = Array.Empty<ChatMessage>();

    public OverlayManager(AppConfig config)
    {
        _config = config;
        _keepOnTop = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _keepOnTop.Tick += (_, _) => Reassert();

        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clock.Tick += (_, _) => UpdateClock();
    }

    /// <summary>Поздний вечер и ночь считаем через полночь: с 21 до 9 — это два куска суток.</summary>
    private bool IsLateHour(DateTime now)
    {
        var from = _config.LateHourFrom;
        var to = _config.LateHourTo;
        if (from == to) return false;

        return from < to
            ? now.Hour >= from && now.Hour < to
            : now.Hour >= from || now.Hour < to;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var late = IsLateHour(now);

        foreach (var window in _windows) window.UpdateClock(now, late);
    }

    public bool IsVisible => _visible;

    public void Show()
    {
        if (_visible) return;

        _visible = true;
        Log.Write("экран блокировки показан");

        // Новая фраза на каждую блокировку.
        _phrase = Motivation.Next();

        // Эксклюзивный полноэкранный режим перекрывает любые чужие окна — сворачиваем игру,
        // иначе ребёнок просто не увидит блокировку.
        if (_config.MinimizeForegroundOnLock)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != NativeMethods.GetShellWindow())
                NativeMethods.ShowWindow(foreground, NativeMethods.SW_MINIMIZE);
        }

        foreach (var screen in Forms.Screen.AllScreens)
        {
            var window = new LockOverlayWindow();
            window.Show();
            window.BindToScreen(screen);
            window.Render(_phrase);

            window.ShowSchedule(_scheduleTitle, _scheduleLines);
            window.ShowChat(_chatMessages);

            if (screen.Primary) window.MessageSubmitted += text => MessageSubmitted?.Invoke(text);
            else window.HideChatInput();

            _windows.Add(window);
        }

        UpdateClock();

        if (_config.BlockHotkeysWhenLocked) _keyboard.Enable();
        _keepOnTop.Start();
        _clock.Start();
        Reassert();

        // Курсор уже в поле ввода: попросить время можно сразу, ничего не нажимая.
        if (_windows.Count > 0) _windows[0].FocusChatInput();
    }

    /// <summary>
    /// Отпускает экран на время родительского диалога: иначе удержание поверх всех окон
    /// перекрывает ввод пароля, а хук съедает клавиши.
    /// </summary>
    public void SuspendGuard()
    {
        if (!_visible) return;

        _keepOnTop.Stop();
        _clock.Stop();
        _keyboard.Disable();

        foreach (var window in _windows) window.Topmost = false;
    }

    public void ResumeGuard()
    {
        if (!_visible) return;

        foreach (var window in _windows) window.Topmost = true;

        UpdateClock();

        if (_config.BlockHotkeysWhenLocked) _keyboard.Enable();
        _keepOnTop.Start();
        _clock.Start();
        Reassert();

        // Курсор уже в поле ввода: попросить время можно сразу, ничего не нажимая.
        if (_windows.Count > 0) _windows[0].FocusChatInput();
    }

    public void SetChat(IReadOnlyList<ChatMessage> messages)
    {
        _chatMessages = messages;

        foreach (var window in _windows) window.ShowChat(messages);
    }

    public void SetSchedule(string title, IReadOnlyList<string> lines)
    {
        _scheduleTitle = title;
        _scheduleLines = lines;

        foreach (var window in _windows) window.ShowSchedule(title, lines);
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        Log.Write("экран блокировки снят");

        _keepOnTop.Stop();
        _clock.Stop();
        _keyboard.Disable();

        foreach (var window in _windows) window.CloseForReal();
        _windows.Clear();
    }

    /// <summary>
    /// Возвращает окна наверх, если что-то успело перекрыть их. Пока впереди наше же окно,
    /// фокус не трогаем: иначе постоянная переактивация сбивает нажатие кнопки.
    /// </summary>
    private void Reassert()
    {
        foreach (var window in _windows)
        {
            if (!window.Topmost) window.Topmost = true;
        }

        if (_windows.Count > 0 && _windows[0].WindowState == WindowState.Minimized)
            _windows[0].WindowState = WindowState.Normal;

        if (IsOwnWindowActive()) return;

        var primary = _windows.Count > 0 ? new WindowInteropHelper(_windows[0]).Handle : IntPtr.Zero;
        if (primary != IntPtr.Zero) NativeMethods.SetForegroundWindow(primary);
    }

    private bool IsOwnWindowActive()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        Hide();
        _keyboard.Dispose();
    }
}
