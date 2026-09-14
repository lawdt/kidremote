using KidRemote.Core;

namespace KidRemote.Telegram;

internal static class Keyboards
{
    public static InlineKeyboardMarkup Main(BankState state)
    {
        var rows = new List<List<InlineKeyboardButton>>
        {
            new()
            {
                InlineKeyboardButton.Create("+15 мин", "add:900"),
                InlineKeyboardButton.Create("+30 мин", "add:1800"),
                InlineKeyboardButton.Create("+1 час", "add:3600")
            },
            new()
            {
                InlineKeyboardButton.Create("−5 мин", "sub:300"),
                InlineKeyboardButton.Create("−15 мин", "sub:900"),
                InlineKeyboardButton.Create("−30 мин", "sub:1800")
            }
        };

        var third = new List<InlineKeyboardButton>();
        third.Add(state == BankState.Paused
            ? InlineKeyboardButton.Create("▶️ Продолжить", "resume")
            : InlineKeyboardButton.Create("⏸ Пауза", "pause"));

        third.Add(state == BankState.Unlimited
            ? InlineKeyboardButton.Create("⏱ Вернуть лимит", "unfree")
            : InlineKeyboardButton.Create("♾ Безлимит", "free"));

        rows.Add(third);

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.Create("🔒 Заблокировать", "lock"),
            InlineKeyboardButton.Create("🔄 Обновить", "status")
        });

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.Create("👪 Родители", "parents")
        });

        return new InlineKeyboardMarkup { InlineKeyboard = rows };
    }

    public static InlineKeyboardMarkup Parents(IReadOnlyList<long> parents, long currentChatId)
    {
        var rows = new List<List<InlineKeyboardButton>>
        {
            new() { InlineKeyboardButton.Create("➕ Пригласить второго родителя", "invite") }
        };

        foreach (var id in parents)
        {
            if (id == currentChatId) continue;
            rows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.Create($"🗑 Отвязать {id}", $"unbind:{id}")
            });
        }

        rows.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.Create("⬅️ Назад", "status") });
        return new InlineKeyboardMarkup { InlineKeyboard = rows };
    }
}
