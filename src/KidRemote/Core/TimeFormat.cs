namespace KidRemote.Core;

internal static class TimeFormat
{
    /// <summary>Компактный формат для угловой плашки: 05:30 или 1:05:30.</summary>
    public static string Compact(long totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var t = TimeSpan.FromSeconds(totalSeconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>
    /// Метка времени в переписке. У сегодняшних реплик только часы, у вчерашних и старее
    /// добавляется день — иначе непонятно, когда о чём договаривались.
    /// </summary>
    public static string ChatStamp(DateTime moment)
    {
        var today = DateTime.Today;

        if (moment.Date == today) return moment.ToString("HH:mm");
        if (moment.Date == today.AddDays(-1)) return $"вчера {moment:HH:mm}";

        return moment.ToString("dd.MM HH:mm");
    }

    /// <summary>Человеческий формат для сообщений бота: "1 ч 25 мин".</summary>
    public static string Human(long totalSeconds)
    {
        if (totalSeconds <= 0) return "0 мин";
        var t = TimeSpan.FromSeconds(totalSeconds);
        var hours = (int)t.TotalHours;
        var parts = new List<string>(2);
        if (hours > 0) parts.Add($"{hours} ч");
        if (t.Minutes > 0 || hours == 0) parts.Add($"{t.Minutes} мин");
        return string.Join(' ', parts);
    }
}
