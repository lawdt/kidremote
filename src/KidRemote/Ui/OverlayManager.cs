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
    private readonly DispatcherTimer _phraseTimer;

    private bool _visible;
    private string _phrase = string.Empty;
    private BankState _state = BankState.Locked;

    public OverlayManager(AppConfig config)
    {
        _config = config;
        _keepOnTop = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _keepOnTop.Tick += (_, _) => Reassert();

        // Одна и та же фраза за полчаса приедается, поэтому меняем её по кругу.
        _phraseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(25)
        };
        _phraseTimer.Tick += (_, _) => ShufflePhrase();
    }

    private void ShufflePhrase()
    {
        _phrase = Motivation.Next();
        foreach (var window in _windows) window.Render(_state, _phrase);
    }

    public bool IsVisible => _visible;

    public void Show(BankState state)
    {
        if (_visible)
        {
            Update(state);
            return;
        }

        _visible = true;
        _state = state;
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
            window.Render(state, _phrase);
            _windows.Add(window);
        }

        if (_config.BlockHotkeysWhenLocked) _keyboard.Enable();
        _keepOnTop.Start();
        if (state == BankState.Locked) _phraseTimer.Start();
        Reassert();
    }

    public void Update(BankState state)
    {
        if (!_visible) return;

        _state = state;
        foreach (var window in _windows) window.Render(state, _phrase);

        // Фразы нужны только на экране «время вышло»; на паузе там своя подпись.
        if (state == BankState.Locked) _phraseTimer.Start();
        else _phraseTimer.Stop();
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;

        _keepOnTop.Stop();
        _phraseTimer.Stop();
        _keyboard.Disable();

        foreach (var window in _windows) window.CloseForReal();
        _windows.Clear();
    }

    /// <summary>Возвращает окна наверх, если что-то успело перекрыть их.</summary>
    private void Reassert()
    {
        foreach (var window in _windows)
        {
            if (!window.Topmost) window.Topmost = true;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
        }

        if (_windows.Count > 0 && _windows[0].WindowState == WindowState.Minimized)
            _windows[0].WindowState = WindowState.Normal;
    }

    public void Dispose()
    {
        Hide();
        _keyboard.Dispose();
    }
}
