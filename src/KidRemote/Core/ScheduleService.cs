using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KidRemote.Core;

/// <summary>
/// Расписание на завтра. Тянется с сайта школы и показывается на экране блокировки:
/// закрытый экран — подходящий момент вспомнить, что завтра к первому уроку.
/// </summary>
internal sealed class ScheduleService : IDisposable
{
    private static readonly string[] DayIds = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    private static readonly string[] DayNames =
    {
        "воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"
    };

    private readonly AppConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;

    public event Action? Updated;

    public string Title { get; private set; } = string.Empty;

    public IReadOnlyList<string> Lines { get; private set; } = Array.Empty<string>();

    public ScheduleService(AppConfig config) => _config = config;

    public void Start()
    {
        if (!_config.ScheduleEnabled) return;
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct).ConfigureAwait(false);

            var interval = TimeSpan.FromMinutes(Math.Clamp(_config.ScheduleRefreshMinutes, 1, 180));

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(_config.ScheduleUrl, ct).ConfigureAwait(false);
            Parse(json);
            Updated?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Выключаемся.
        }
        catch (Exception ex)
        {
            Log.Write($"расписание: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        // Утром важнее сегодняшние уроки, а ближе к обеду — уже следующие.
        var now = DateTime.Now;
        var from = now.Hour < _config.ScheduleTodayUntilHour ? now : now.AddDays(1);

        // Ищем ближайший учебный день: так пятничный вечер сам показывает понедельник,
        // а выходные и каникулы пропускаются без отдельных правил.
        for (var shift = 0; shift < 7; shift++)
        {
            var target = from.AddDays(shift);
            var lines = ReadDay(document, DayIds[(int)target.DayOfWeek]);

            if (lines.Count == 0) continue;

            Lines = lines;
            Title = BuildTitle(now, target);
            return;
        }

        Lines = Array.Empty<string>();
        Title = string.Empty;
    }

    private List<string> ReadDay(JsonDocument document, string dayId)
    {
        var lines = new List<string>();

        if (!document.RootElement.TryGetProperty("days", out var days)) return lines;

        foreach (var day in days.EnumerateArray())
        {
            if (!day.TryGetProperty("id", out var id) || id.GetString() != dayId) continue;
            if (!day.TryGetProperty("byClass", out var byClass)) continue;
            if (!byClass.TryGetProperty(_config.ScheduleClass, out var lessons)) continue;

            foreach (var lesson in lessons.EnumerateArray())
            {
                var line = FormatLesson(lesson);
                if (line is not null) lines.Add(line);
            }
        }

        return lines;
    }

    private static string BuildTitle(DateTime now, DateTime target)
    {
        var name = DayNames[(int)target.DayOfWeek];

        if (target.Date == now.Date) return $"Сегодня, {name}";
        if (target.Date == now.Date.AddDays(1)) return $"Завтра, {name}";

        return char.ToUpper(name[0]) + name[1..];
    }

    private string? FormatLesson(JsonElement lesson)
    {
        if (Text(lesson, "kind") != "lesson") return null;
        if (lesson.TryGetProperty("paid", out var paid) && paid.ValueKind == JsonValueKind.True) return null;

        var name = BuildName(lesson);
        if (string.IsNullOrWhiteSpace(name)) return null;

        return $"{Text(lesson, "start")}–{Text(lesson, "end")}  {name}";
    }

    /// <summary>
    /// У урока может быть несколько потоков — например, разные языки. Берём свою программу,
    /// а если такой нет, показываем все, чтобы строка не осталась пустой.
    /// </summary>
    private string BuildName(JsonElement lesson)
    {
        if (!lesson.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return Text(lesson, "subject") + Room(lesson);

        var all = tracks.EnumerateArray().ToList();
        if (all.Count == 0) return Text(lesson, "subject") + Room(lesson);

        var mine = all
            .Where(track =>
            {
                var programme = Text(track, "programme");
                return programme.Length == 0 || programme == _config.ScheduleProgramme;
            })
            .ToList();

        var chosen = mine.Count > 0 ? mine : all;

        var builder = new StringBuilder();
        foreach (var track in chosen)
        {
            if (builder.Length > 0) builder.Append(" / ");
            builder.Append(Text(track, "subject")).Append(Room(track));
        }

        return builder.ToString();
    }

    private static string Room(JsonElement element)
    {
        var place = Text(element, "place");
        return place.Length > 0 ? $" (каб. {place})" : string.Empty;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}
