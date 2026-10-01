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

    /// <summary>Сообщает наружу, кого держим: список переживает перезапуск приложения.</summary>
    public event Action<IReadOnlyList<int>>? Changed;

    /// <summary>
    /// Отпускает процессы, оставшиеся замороженными после аварийного завершения.
    /// Без этого игра так и висела бы до перезагрузки.
    /// </summary>
    public void ThawLeftovers(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds)
        {
            if (Resume(processId)) Log.Write($"заморозка: отпущен процесс {processId} после перезапуска");
        }
    }

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
                Changed?.Invoke(_frozen.ToArray());
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
        if (_frozen.Count == 0) return;

        foreach (var processId in _frozen.ToArray()) Resume(processId);

        _frozen.Clear();
        Changed?.Invoke(Array.Empty<int>());
    }

    private static bool Resume(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, (uint)processId);
        if (handle == IntPtr.Zero) return false;

        try
        {
            return NativeMethods.NtResumeProcess(handle) == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
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
