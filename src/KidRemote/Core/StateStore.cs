using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidRemote.Core;

internal sealed class PersistedState
{
    [JsonPropertyName("remainingSeconds")]
    public long RemainingSeconds { get; set; }

    [JsonPropertyName("unlimited")]
    public bool Unlimited { get; set; }

    /// <summary>Идентификатор следующего необработанного апдейта Telegram.</summary>
    [JsonPropertyName("updateOffset")]
    public long UpdateOffset { get; set; }

    /// <summary>Сообщение с панелью управления — его переиспользуем при нажатии кнопок.</summary>
    [JsonPropertyName("panelMessages")]
    public Dictionary<string, long> PanelMessages { get; set; } = new();

    [JsonPropertyName("savedAtUtc")]
    public DateTime SavedAtUtc { get; set; }
}

/// <summary>
/// Состояние на диске. Пишется через временный файл, чтобы внезапное выключение
/// не оставило обрезанный json.
/// </summary>
internal sealed class StateStore
{
    private readonly string _path = Path.Combine(Paths.DataDirectory, "state.dat");
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PersistedState Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return new PersistedState();

                var payload = File.ReadAllText(_path);
                if (!Dpapi.TryUnprotect(payload, out var json)) return new PersistedState();

                return JsonSerializer.Deserialize<PersistedState>(json, Options) ?? new PersistedState();
            }
            catch
            {
                return new PersistedState();
            }
        }
    }

    public void Save(PersistedState state)
    {
        lock (_sync)
        {
            try
            {
                state.SavedAtUtc = DateTime.UtcNow;
                Directory.CreateDirectory(Paths.DataDirectory);

                var payload = Dpapi.Protect(JsonSerializer.Serialize(state, Options));
                var temp = _path + ".tmp";
                File.WriteAllText(temp, payload);
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // Потеря одного сохранения не должна ронять приложение.
            }
        }
    }
}
