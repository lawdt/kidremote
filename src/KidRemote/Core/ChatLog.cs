using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidRemote.Core;

internal sealed class ChatMessage
{
    /// <summary>Локальный идентификатор — по нему помечаем доставку.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Написал родитель из Telegram, а не ребёнок с компьютера.</summary>
    [JsonPropertyName("fromParent")]
    public bool FromParent { get; set; }

    /// <summary>Реплика ребёнка ушла в Telegram. Пока нет — лежит в очереди и повторяется.</summary>
    [JsonPropertyName("delivered")]
    public bool Delivered { get; set; }
}

/// <summary>
/// Переписка ребёнка и родителей. Живёт на диске, чтобы разговор не терялся
/// при перезапуске приложения.
/// </summary>
internal sealed class ChatLog
{
    private const int MaxMessages = 100;

    private readonly object _sync = new();
    private readonly string _path = Path.Combine(Paths.DataDirectory, "chat.dat");
    private readonly List<ChatMessage> _messages = new();

    public event Action<ChatMessage>? Added;

    /// <summary>Переписка изменилась: пришло сообщение или сменился признак доставки.</summary>
    public event Action? Changed;

    public ChatLog() => Load();

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_sync) return _messages.ToList(); }
    }

    /// <summary>Последние сообщения, новые в конце.</summary>
    public IReadOnlyList<ChatMessage> Tail(int count)
    {
        lock (_sync)
        {
            return _messages.Count <= count
                ? _messages.ToList()
                : _messages.GetRange(_messages.Count - count, count);
        }
    }

    public ChatMessage Add(string author, string text, bool fromParent)
    {
        var message = new ChatMessage
        {
            Id = DateTime.UtcNow.Ticks,
            Time = DateTime.Now,
            Author = author,
            Text = text,
            FromParent = fromParent,
            // Реплика родителя уже у нас на экране, доставлять её никуда не нужно.
            Delivered = fromParent
        };

        lock (_sync)
        {
            _messages.Add(message);
            if (_messages.Count > MaxMessages) _messages.RemoveRange(0, _messages.Count - MaxMessages);
        }

        Save();
        Added?.Invoke(message);
        Changed?.Invoke();

        return message;
    }

    /// <summary>Реплики ребёнка, которые ещё не ушли в Telegram, от старых к новым.</summary>
    public IReadOnlyList<ChatMessage> Pending()
    {
        lock (_sync)
        {
            return _messages.Where(message => !message.FromParent && !message.Delivered).ToList();
        }
    }

    public void MarkDelivered(long id)
    {
        lock (_sync)
        {
            var message = _messages.FirstOrDefault(item => item.Id == id);
            if (message is null || message.Delivered) return;

            message.Delivered = true;
        }

        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (!Dpapi.TryUnprotect(File.ReadAllText(_path), out var json)) return;

            var restored = JsonSerializer.Deserialize<List<ChatMessage>>(json);
            if (restored is null) return;

            lock (_sync)
            {
                _messages.Clear();
                _messages.AddRange(restored);
            }
        }
        catch
        {
            // Потерянная переписка не повод не запускаться.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDirectory);

            string json;
            lock (_sync) json = JsonSerializer.Serialize(_messages);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, Dpapi.Protect(json));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Молча: следующее сообщение попробует снова.
        }
    }
}
