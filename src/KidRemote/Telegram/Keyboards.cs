using KidRemote.Core;

namespace KidRemote.Telegram;

/// <summary>Что делать с набранным в конструкторе значением.</summary>
internal enum DraftMode
{
    Add,
    Subtract,
    Set
}

internal static class Keyboards
{
    /// <summary>Главная панель. Все изменяющие кнопки ведут на экран подтверждения.</summary>
    public static InlineKeyboardMarkup Main(BankState state)
    {
        var rows = new List<List<InlineKeyboardButton>>
        {
            new()
            {
                InlineKeyboardButton.Create("+15 мин", "ask:add:900"),
                InlineKeyboardButton.Create("+30 мин", "ask:add:1800"),
                InlineKeyboardButton.Create("+1 час", "ask:add:3600")
            },
            new()
            {
                InlineKeyboardButton.Create("−5 мин", "ask:sub:300"),
                InlineKeyboardButton.Create("−15 мин", "ask:sub:900"),
                InlineKeyboardButton.Create("−30 мин", "ask:sub:1800")
            },
            new()
            {
                InlineKeyboardButton.Create("⚙️ Задать точно", "draft")
            }
        };

        var modes = new List<InlineKeyboardButton>
        {
            state == BankState.Paused
                ? InlineKeyboardButton.Create("▶️ Продолжить", "ask:resume")
                : InlineKeyboardButton.Create("⏸ Пауза", "ask:pause"),
            state == BankState.Unlimited
                ? InlineKeyboardButton.Create("⏱ Вернуть лимит", "ask:unfree")
                : InlineKeyboardButton.Create("♾ Безлимит", "ask:free")
        };

        rows.Add(modes);

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.Create("🔒 Заблокировать", "ask:lock"),
            InlineKeyboardButton.Create("🔄 Обновить", "status")
        });

        rows.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.Create("👪 Родители", "parents")
        });

        return new InlineKeyboardMarkup { InlineKeyboard = rows };
    }

    /// <summary>Экран подтверждения: действие применяется только после явного «да».</summary>
    public static InlineKeyboardMarkup Confirm(string kind, long argument)
    {
        var payload = argument > 0 ? $"ok:{kind}:{argument}" : $"ok:{kind}";

        return new InlineKeyboardMarkup
        {
            InlineKeyboard = new List<List<InlineKeyboardButton>>
            {
                new()
                {
                    InlineKeyboardButton.Create("✅ Подтвердить", payload),
                    InlineKeyboardButton.Create("❌ Отмена", "status")
                }
            }
        };
    }

    /// <summary>Конструктор точного значения: шаг в одну минуту.</summary>
    public static InlineKeyboardMarkup Draft(DraftMode mode)
    {
        var modeCaption = mode switch
        {
            DraftMode.Subtract => "Режим: вычесть из остатка",
            DraftMode.Set => "Режим: выставить ровно",
            _ => "Режим: добавить к остатку"
        };

        return new InlineKeyboardMarkup
        {
            InlineKeyboard = new List<List<InlineKeyboardButton>>
            {
                new()
                {
                    InlineKeyboardButton.Create("+1", "d:1"),
                    InlineKeyboardButton.Create("+5", "d:5"),
                    InlineKeyboardButton.Create("+10", "d:10"),
                    InlineKeyboardButton.Create("+30", "d:30")
                },
                new()
                {
                    InlineKeyboardButton.Create("−1", "d:-1"),
                    InlineKeyboardButton.Create("−5", "d:-5"),
                    InlineKeyboardButton.Create("−10", "d:-10"),
                    InlineKeyboardButton.Create("−30", "d:-30")
                },
                new()
                {
                    InlineKeyboardButton.Create(modeCaption, "dmode")
                },
                new()
                {
                    InlineKeyboardButton.Create("✅ Применить", "dapply"),
                    InlineKeyboardButton.Create("⬅️ Назад", "status")
                }
            }
        };
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
