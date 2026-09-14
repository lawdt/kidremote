using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidRemote.Core;

internal sealed class AppConfig
{
    /// <summary>Токен бота в открытом виде. Заполняется руками один раз; после старта переносится в ProtectedBotToken.</summary>
    [JsonPropertyName("botToken")]
    public string BotToken { get; set; } = string.Empty;

    [JsonPropertyName("botTokenProtected")]
    public string ProtectedBotToken { get; set; } = string.Empty;

    /// <summary>Chat id родителей. Команды от остальных игнорируются.</summary>
    [JsonPropertyName("parentChatIds")]
    public List<long> ParentChatIds { get; set; } = new();

    /// <summary>Сколько секунд без ввода считать простоем (таймер замирает).</summary>
    [JsonPropertyName("idlePauseSeconds")]
    public int IdlePauseSeconds { get; set; } = 60;

    /// <summary>Списывать время только когда на переднем плане полноэкранное приложение.</summary>
    [JsonPropertyName("requireFullscreen")]
    public bool RequireFullscreen { get; set; } = true;

    /// <summary>Порог перехода в жёлтый режим, секунды.</summary>
    [JsonPropertyName("warnSeconds")]
    public int WarnSeconds { get; set; } = 300;

    /// <summary>Порог перехода в красный режим, секунды.</summary>
    [JsonPropertyName("dangerSeconds")]
    public int DangerSeconds { get; set; } = 60;

    /// <summary>Угол экрана для плашки: TopRight, TopLeft, BottomRight, BottomLeft.</summary>
    [JsonPropertyName("overlayCorner")]
    public string OverlayCorner { get; set; } = "TopRight";

    /// <summary>Сворачивать активное окно при блокировке — помогает выйти из эксклюзивного полноэкранного режима.</summary>
    [JsonPropertyName("minimizeForegroundOnLock")]
    public bool MinimizeForegroundOnLock { get; set; } = true;

    /// <summary>Блокировать Alt+Tab, Win и Alt+F4, пока показан экран блокировки.</summary>
    [JsonPropertyName("blockHotkeysWhenLocked")]
    public bool BlockHotkeysWhenLocked { get; set; } = true;

    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; } = true;

    /// <summary>Разрешить выход из приложения через меню в трее. По умолчанию выход только из бота командой /quit.</summary>
    [JsonPropertyName("allowTrayExit")]
    public bool AllowTrayExit { get; set; }

    [JsonIgnore]
    public string ResolvedToken { get; private set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ConfigPath => Path.Combine(Paths.DataDirectory, "config.json");

    public static AppConfig LoadOrCreate()
    {
        Directory.CreateDirectory(Paths.DataDirectory);

        AppConfig config;
        if (File.Exists(ConfigPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Options) ?? new AppConfig();
            }
            catch
            {
                config = new AppConfig();
            }
        }
        else
        {
            config = new AppConfig();
            config.Save();
        }

        // Токен, введённый руками, переносим в защищённое поле и стираем открытую копию.
        if (!string.IsNullOrWhiteSpace(config.BotToken))
        {
            config.ResolvedToken = config.BotToken.Trim();
            config.ProtectedBotToken = Dpapi.Protect(config.ResolvedToken);
            config.BotToken = string.Empty;
            config.Save();
        }
        else if (!string.IsNullOrWhiteSpace(config.ProtectedBotToken)
                 && Dpapi.TryUnprotect(config.ProtectedBotToken, out var plain))
        {
            config.ResolvedToken = plain;
        }

        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
    }

    public bool IsParent(long chatId) => ParentChatIds.Contains(chatId);

    /// <summary>Первый запуск: список родителей пуст, привяжем того, кто первым напишет боту.</summary>
    public bool NeedsParentBinding => ParentChatIds.Count == 0;

    public bool AddParent(long chatId)
    {
        if (ParentChatIds.Contains(chatId)) return false;
        ParentChatIds.Add(chatId);
        Save();
        return true;
    }

    public bool RemoveParent(long chatId)
    {
        if (!ParentChatIds.Remove(chatId)) return false;
        Save();
        return true;
    }
}

internal static class Paths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KidRemote");
}
