using System.Text.Json.Serialization;

namespace KidRemote.Telegram;

internal sealed class ApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class Update
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    [JsonPropertyName("message")]
    public Message? Message { get; set; }

    [JsonPropertyName("callback_query")]
    public CallbackQuery? CallbackQuery { get; set; }
}

internal sealed class Message
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("chat")]
    public Chat? Chat { get; set; }

    [JsonPropertyName("from")]
    public User? From { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class Chat
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class User
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

internal sealed class CallbackQuery
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public User? From { get; set; }

    [JsonPropertyName("message")]
    public Message? Message { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

internal sealed class InlineKeyboardMarkup
{
    [JsonPropertyName("inline_keyboard")]
    public List<List<InlineKeyboardButton>> InlineKeyboard { get; set; } = new();
}

/// <summary>Постоянная клавиатура под полем ввода.</summary>
internal sealed class ReplyKeyboardMarkup
{
    [JsonPropertyName("keyboard")]
    public List<List<KeyboardButton>> Keyboard { get; set; } = new();

    [JsonPropertyName("resize_keyboard")]
    public bool ResizeKeyboard { get; set; } = true;

    [JsonPropertyName("is_persistent")]
    public bool IsPersistent { get; set; } = true;
}

internal sealed class KeyboardButton
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    public static KeyboardButton Create(string text) => new() { Text = text };
}

internal sealed class BotCommand
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    public static BotCommand Create(string command, string description) =>
        new() { Command = command, Description = description };
}

internal sealed class InlineKeyboardButton
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("callback_data")]
    public string CallbackData { get; set; } = string.Empty;

    public static InlineKeyboardButton Create(string text, string data) => new() { Text = text, CallbackData = data };
}
