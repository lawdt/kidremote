using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace KidRemote.Core;

/// <summary>
/// Запуск вместе с Windows. Обычный список автозапуска оболочка намеренно тормозит —
/// программы оттуда стартуют заметно позже входа в систему. Поэтому основной способ здесь
/// задача планировщика: она срабатывает сразу и без задержки.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "KidRemote";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KidRemote";

    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        if (!enabled)
        {
            RemoveTask();
            WriteRunValue(null);
            return;
        }

        // Запись в списке автозапуска остаётся запасным вариантом, если планировщик недоступен.
        WriteRunValue(TryCreateTask(exe) ? null : exe);
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not null) return true;
        }
        catch
        {
            // Ниже проверим задачу.
        }

        return RunSchtasks($"/query /tn {TaskName}") == 0;
    }

    private static bool TryCreateTask(string exe)
    {
        try
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var path = Path.Combine(Path.GetTempPath(), "kidremote-task.xml");

            File.WriteAllText(path, BuildTaskXml(user, exe), Encoding.Unicode);

            var created = RunSchtasks($"/create /tn {TaskName} /xml \"{path}\" /f") == 0;

            try { File.Delete(path); } catch { }

            if (!created) Log.Write("автозапуск: планировщик отказал, остаёмся в списке автозапуска");

            return created;
        }
        catch (Exception ex)
        {
            Log.Write($"автозапуск: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static void RemoveTask() => RunSchtasks($"/delete /tn {TaskName} /f");

    private static int RunSchtasks(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process is null) return -1;

            process.WaitForExit(10_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void WriteRunValue(string? exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (exe is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, $"\"{exe}\"");
        }
        catch
        {
            // Политики могут запретить запись — не повод падать.
        }
    }

    /// <summary>
    /// Триггер без задержки, высокий приоритет и запуск даже на батарее: приложение должно
    /// оказаться на экране одним из первых, а не в хвосте очереди.
    /// </summary>
    private static string BuildTaskXml(string user, string exe) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Ограничение игрового времени</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{user}</UserId>
              <Delay>PT0S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{user}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{exe}"</Command>
            </Exec>
          </Actions>
        </Task>
        """;
}
