using System.Diagnostics;
using KidRemote.Core;
using KidRemote.Interop;

namespace KidRemote.Ui;

/// <summary>
/// Замораживает игру на время блокировки. Клавиатуру можно перехватить хуком, а джойстик
/// игра читает напрямую с устройства — никакой перехват до него не доберётся. Поэтому
/// на время закрытого экрана процесс игры приостанавливается целиком.
/// </summary>
internal sealed class GameFreezer
{
    private readonly List<int> _frozen = new();

    public void Freeze(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return;
        if (_frozen.Contains(processId)) return;

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            Log.Write($"заморозка: нет доступа к процессу {processId}");
            return;
        }

        try
        {
            if (NativeMethods.NtSuspendProcess(handle) == 0)
            {
                _frozen.Add(processId);
                Log.Write($"заморозка: процесс {processId} приостановлен");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Отпускает все замороженные процессы. Вызывается и при выходе, чтобы игра не осталась висеть.</summary>
    public void ThawAll()
    {
        foreach (var processId in _frozen.ToArray())
        {
            var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, (uint)processId);
            if (handle == IntPtr.Zero) continue;

            try
            {
                NativeMethods.NtResumeProcess(handle);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        _frozen.Clear();
    }

    /// <summary>Процесс окна на переднем плане, если это похоже на игру.</summary>
    public static int GameInFront(AppConfig config)
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;

        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return 0;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;

            if (GameCatalog.IsLauncher(name)) return 0;

            var known = GameCatalog.IsGame(name, config.ExtraGames);

            try
            {
                known |= GameCatalog.IsGamePath(process.MainModule?.FileName);
            }
            catch
            {
                // Путь недоступен — ориентируемся на имя.
            }

            return known ? (int)pid : 0;
        }
        catch
        {
            return 0;
        }
    }
}
