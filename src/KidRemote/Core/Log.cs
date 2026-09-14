using System.IO;

namespace KidRemote.Core;

/// <summary>
/// Простой журнал в файл. Нужен прежде всего для ошибок Telegram: без него сбой отправки
/// выглядит как «бот молчит» и никак не диагностируется.
/// </summary>
internal static class Log
{
    private const long MaxSizeBytes = 512 * 1024;

    private static readonly object Sync = new();

    public static string Path => System.IO.Path.Combine(Paths.DataDirectory, "kidremote.log");

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDirectory);

                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxSizeBytes) file.Delete();

                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Журнал не должен мешать работе.
            }
        }
    }
}
