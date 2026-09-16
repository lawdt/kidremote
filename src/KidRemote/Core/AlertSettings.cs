using System.Text.Json.Serialization;

namespace KidRemote.Core;

/// <summary>Повод для сообщения в чат.</summary>
internal enum AlertKind
{
    /// <summary>Время кончилось, компьютер заблокирован.</summary>
    TimeUp,

    /// <summary>Остаток дошёл до порога предупреждения.</summary>
    Warning,

    /// <summary>Компьютер проснулся.</summary>
    Wake,

    /// <summary>Приложение запустилось вместе с компьютером.</summary>
    Startup,

    /// <summary>Разблокировка по паролю, закрытие приложения, привязка родителя.</summary>
    System
}

/// <summary>Какие уведомления получает конкретный родитель.</summary>
internal sealed class AlertSettings
{
    [JsonPropertyName("timeUp")]
    public bool TimeUp { get; set; } = true;

    [JsonPropertyName("warning")]
    public bool Warning { get; set; } = true;

    [JsonPropertyName("wake")]
    public bool Wake { get; set; } = true;

    [JsonPropertyName("startup")]
    public bool Startup { get; set; } = true;

    [JsonPropertyName("system")]
    public bool System { get; set; } = true;

    public bool IsEnabled(AlertKind kind) => kind switch
    {
        AlertKind.TimeUp => TimeUp,
        AlertKind.Warning => Warning,
        AlertKind.Wake => Wake,
        AlertKind.Startup => Startup,
        _ => System
    };

    public void Toggle(AlertKind kind)
    {
        switch (kind)
        {
            case AlertKind.TimeUp: TimeUp = !TimeUp; break;
            case AlertKind.Warning: Warning = !Warning; break;
            case AlertKind.Wake: Wake = !Wake; break;
            case AlertKind.Startup: Startup = !Startup; break;
            default: System = !System; break;
        }
    }

    public static string Caption(AlertKind kind) => kind switch
    {
        AlertKind.TimeUp => "Время кончилось",
        AlertKind.Warning => "Осталось мало времени",
        AlertKind.Wake => "Компьютер проснулся",
        AlertKind.Startup => "Компьютер включён",
        _ => "Служебные события"
    };
}
