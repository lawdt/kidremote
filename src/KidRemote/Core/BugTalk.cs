namespace KidRemote.Core;

/// <summary>Что коровка отвечает, если стучать по её домику.</summary>
internal static class BugTalk
{
    private static readonly string[] Phrases =
    {
        "Себе по башке постучи!",
        "Хватит стучать!",
        "Занято!",
        "Я обиделся.",
        "Ну сколько можно?",
        "Тут вообще-то приличная коровка живёт.",
        "Ещё раз стукнешь — улечу.",
        "Не открою, и не проси.",
        "У меня обед!",
        "Иди уроки делай.",
        "Я сплю. Совсем.",
        "Стучат и стучат…"
    };

    /// <summary>Фразы идут по кругу: чем дольше стучат, тем недовольнее ответ.</summary>
    public static string Next(int knock) => Phrases[knock % Phrases.Length];
}
