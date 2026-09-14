using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using KidRemote.Core;

namespace KidRemote.Telegram;

internal sealed class TelegramClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public TelegramClient(string token)
    {
        _baseUrl = $"https://api.telegram.org/bot{token}/";
        _http = new HttpClient
        {
            // Чуть больше, чем long polling timeout, иначе валимся по таймауту на каждом цикле.
            Timeout = TimeSpan.FromSeconds(90)
        };
    }

    public async Task<bool> CheckTokenAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(_baseUrl + "getMe", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<Update>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var url = $"{_baseUrl}getUpdates?timeout={timeoutSeconds}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D";
        if (offset > 0) url += $"&offset={offset}";

        var response = await _http.GetFromJsonAsync<ApiResponse<List<Update>>>(url, Json, ct).ConfigureAwait(false);
        return response?.Result ?? new List<Update>();
    }

    public async Task<long?> SendMessageAsync(long chatId, string text, object? markup, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML"
        };

        // Пустую разметку не отправляем вовсе: reply_markup со значением null Telegram отвергает.
        if (markup is not null) payload["reply_markup"] = markup;

        var result = await PostAsync<Message>("sendMessage", payload, ct).ConfigureAwait(false);
        return result?.MessageId;
    }

    public async Task<bool> EditMessageAsync(long chatId, long messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = text,
            ["parse_mode"] = "HTML"
        };

        if (markup is not null) payload["reply_markup"] = markup;

        return await PostAsync<Message>("editMessageText", payload, ct).ConfigureAwait(false) is not null;
    }

    public async Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["callback_query_id"] = callbackId
        };

        if (!string.IsNullOrEmpty(text)) payload["text"] = text;

        await PostAsync<bool>("answerCallbackQuery", payload, ct).ConfigureAwait(false);
    }

    /// <summary>Список команд для стандартной кнопки «Меню» в интерфейсе Telegram.</summary>
    public async Task SetCommandsAsync(IEnumerable<BotCommand> commands, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["commands"] = commands.ToList()
        };

        await PostAsync<bool>("setMyCommands", payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Единая точка вызова API: ошибки не теряются молча, а попадают в журнал —
    /// иначе неудачная отправка выглядит просто как «бот ничего не прислал».
    /// </summary>
    private async Task<T?> PostAsync<T>(string method, Dictionary<string, object> payload, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(_baseUrl + method, payload, Json, ct).ConfigureAwait(false);
            var parsed = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(Json, ct).ConfigureAwait(false);

            if (parsed is { Ok: true }) return parsed.Result;

            Log.Write($"telegram {method}: {(int)response.StatusCode} {parsed?.Description ?? "без описания"}");
            return default;
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            Log.Write($"telegram {method}: {ex.GetType().Name} {ex.Message}");
            return default;
        }
    }

    public void Dispose() => _http.Dispose();
}
