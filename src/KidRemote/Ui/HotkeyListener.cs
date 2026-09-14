using System.Windows.Interop;
using KidRemote.Core;
using KidRemote.Interop;

namespace KidRemote.Ui;

/// <summary>
/// Глобальное сочетание Ctrl+Alt+F1 для разблокировки прямо за компьютером.
/// Слушаем через собственное окно-приёмник сообщений: видимое окно для этого не нужно.
/// </summary>
internal sealed class HotkeyListener : IDisposable
{
    private const int HotkeyId = 0xC0DE;

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyListener()
    {
        var parameters = new HwndSourceParameters("KidRemote.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _registered = NativeMethods.RegisterHotKey(
            _source.Handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_F1);

        if (!_registered) Log.Write("не удалось зарегистрировать Ctrl+Alt+F1: сочетание занято другой программой");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
