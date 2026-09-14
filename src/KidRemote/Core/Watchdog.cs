using System.Diagnostics;
using System.IO;

namespace KidRemote.Core;

/// <summary>
/// Взаимная страховка двух процессов. Основной процесс поднимает сторожа, сторож поднимает
/// основной. Снять приложение диспетчером задач становится заметно сложнее: нужно успеть
/// закрыть оба быстрее, чем сработает проверка.
///
/// Это не защита от подготовленного взлома, а барьер против «закрыл и играю дальше».
/// Полную неуязвимость даёт только служба под системной учётной записью.
/// </summary>
internal static class Watchdog
{
    public const string GuardArgument = "--watchdog";

    public const string MainMutexName = "KidRemote.SingleInstance";
    private const string GuardMutexName = "KidRemote.Guard";

    private static readonly TimeSpan GuardPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(2);

    private static string FlagPath => Path.Combine(Paths.DataDirectory, "shutdown.flag");

    /// <summary>Разрешает штатный выход: сторож увидит флаг и не станет поднимать процесс заново.</summary>
    public static void AllowShutdown()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDirectory);
            File.WriteAllText(FlagPath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Не смогли поставить флаг — сторож просто перезапустит приложение.
        }
    }

    /// <summary>Снимает флаг при обычном старте, иначе сторож останется бесполезным навсегда.</summary>
    public static void ClearShutdownFlag()
    {
        try
        {
            if (File.Exists(FlagPath)) File.Delete(FlagPath);
        }
        catch
        {
            // Не критично.
        }
    }

    public static bool ShutdownRequested()
    {
        try
        {
            return File.Exists(FlagPath);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRunning(string mutexName)
    {
        try
        {
            return Mutex.TryOpenExisting(mutexName, out var mutex) && Dispose(mutex);
        }
        catch
        {
            return false;
        }
    }

    private static bool Dispose(Mutex mutex)
    {
        mutex.Dispose();
        return true;
    }

    /// <summary>Поднимает сторожа, если его ещё нет.</summary>
    public static void EnsureGuardRunning()
    {
        if (IsRunning(GuardMutexName)) return;
        Spawn(GuardArgument);
    }

    /// <summary>Цикл сторожевого процесса: следит за основным и возвращает его к жизни.</summary>
    public static void RunGuard(CancellationToken ct)
    {
        using var guardMutex = new Mutex(true, GuardMutexName, out var isFirst);
        if (!isFirst) return;

        while (!ct.IsCancellationRequested)
        {
            if (ShutdownRequested()) return;

            if (!IsRunning(MainMutexName))
            {
                // Пауза на случай, если основной процесс как раз штатно завершается.
                if (ct.WaitHandle.WaitOne(RestartGrace)) return;
                if (ShutdownRequested()) return;

                if (!IsRunning(MainMutexName)) Spawn(null);
            }

            if (ct.WaitHandle.WaitOne(GuardPollInterval)) return;
        }
    }

    private static void Spawn(string? argument)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };

            if (!string.IsNullOrEmpty(argument)) info.ArgumentList.Add(argument);

            Process.Start(info);
        }
        catch
        {
            // Следующая итерация попробует снова.
        }
    }
}
