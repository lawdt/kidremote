using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

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

    public async Task<long?> SendMessageAsync(long chatId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = markup
        };

        try
        {
            var response = await _http.PostAsJsonAsync(_baseUrl + "sendMessage", payload, Json, ct).ConfigureAwait(false);
            var parsed = await response.Content.ReadFromJsonAsync<ApiResponse<Message>>(Json, ct).ConfigureAwait(false);
            return parsed?.Result?.MessageId;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> EditMessageAsync(long chatId, long messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = markup
        };

        try
        {
            var response = await _http.PostAsJsonAsync(_baseUrl + "editMessageText", payload, Json, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["callback_query_id"] = callbackId,
            ["text"] = text
        };

        try
        {
            await _http.PostAsJsonAsync(_baseUrl + "answerCallbackQuery", payload, Json, ct).ConfigureAwait(false);
        }
        catch
        {
            // Ответ на кнопку — косметика, молча пропускаем.
        }
    }

    public void Dispose() => _http.Dispose();
}
