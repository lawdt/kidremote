using System.Runtime.InteropServices;
using KidRemote.Interop;

namespace KidRemote.Ui;

/// <summary>
/// Низкоуровневый хук клавиатуры на время блокировки: гасит Alt+Tab, Win и Alt+F4.
/// Ctrl+Alt+Del перехватить нельзя — это ограничение самой Windows.
/// </summary>
internal sealed class KeyboardBlocker : IDisposable
{
    private const uint VK_TAB = 0x09;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_F4 = 0x73;

    // Ссылку на делегат держим в поле: иначе сборщик мусора уберёт её и хук молча умрёт.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    public KeyboardBlocker() => _proc = HookCallback;

    public void Enable()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    public void Disable()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            // Ввод в собственное окно пропускаем: иначе не написать сообщение родителям.
            if (!IsOwnWindowActive() || ShouldBlockAlways(info.vkCode)) return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Эти сочетания глушим всегда: ими выходят из блокировки.</summary>
    private static bool ShouldBlockAlways(uint vkCode) =>
        vkCode is VK_TAB or VK_ESCAPE or VK_LWIN or VK_RWIN or VK_F4;

    private static bool IsOwnWindowActive()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    public void Dispose() => Disable();
}
