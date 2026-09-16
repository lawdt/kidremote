namespace KidRemote.Core;

/// <summary>
/// Известные игры по имени процесса. Нужны потому, что многие играют в окне, а не на
/// весь экран: без каталога такое время не засчитывалось бы.
///
/// Список заведомо неполный — свои игры добавляются в настройках через `extraGames`.
/// </summary>
internal static class GameCatalog
{
    /// <summary>
    /// Папки игровых магазинов. Всё, что запущено оттуда, считаем игрой независимо от названия —
    /// это покрывает библиотеку Steam целиком, включая то, чего нет ни в одном списке.
    /// </summary>
    private static readonly string[] StoreFolders =
    {
        @"\steamapps\common\",
        @"\epic games\",
        @"\gog galaxy\games\",
        @"\gog games\",
        @"\ea games\",
        @"\origin games\",
        @"\ubisoft game launcher\games\",
        @"\riot games\",
        @"\battle.net\",
        @"\xboxgames\",
        @"\wargaming.net\"
    };

    /// <summary>Хвосты имён, по которым игра опознаётся без списка: так собирают проекты на Unreal Engine.</summary>
    private static readonly string[] EngineSuffixes =
    {
        "-win64-shipping",
        "-wingdk-shipping",
        "-win64-test"
    };

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // песочницы и выживание
        "minecraft.windows", "javaw", "terraria", "starbound", "valheim", "raft",
        "theforest", "sonsoftheforest", "greenhell", "7daystodie", "dayz_x64", "dayz",
        "projectzomboid64", "projectzomboid32", "rustclient", "shootergame", "enshrouded",
        "subnautica", "subnautica-belowzero", "dontstarvetogether", "slimerancher",
        "unturned", "corekeeper", "vintagestory",

        // строительство и стратегии
        "ksp_x64", "ksp2_x64", "riftbreaker", "factorio", "factorygame", "dysonsphereprogram",
        "cities", "citiesskylines2", "civilizationvi", "civilizationv", "anno1800",
        "reliccardinal", "aoe2de_s", "totalwar", "starcraftii", "sc2", "frostpunk",
        "banished", "rimworldwin64", "oxygennotincluded", "timberborn", "stardew valley",

        // шутеры и соревновательные
        "cs2", "csgo", "valorant", "r5apex", "overwatch", "tslgame", "hl2", "portal2",
        "portal", "left4dead2", "gmod", "doometernalx64vk", "doomx64vk", "titanfall2",
        "bf2042", "battlefield", "modernwarfare", "blackopscoldwar", "cod", "escapefromtarkov",
        "destiny2", "warframe.x64", "helldivers2", "thefinals", "deadlock", "paladins",
        "teamfortress2", "hlvr",

        // ролевые и приключения
        "eldenring", "darksoulsiii", "darksoulsremastered", "sekiro", "armoredcore6",
        "skyrimse", "skyrim", "fallout4", "fallout76", "starfield", "witcher3",
        "cyberpunk2077", "bg3", "bg3_dx11", "eocapp", "genshinimpact", "starrail",
        "zenlesszonezero", "nier", "hogwartslegacy", "gta5", "gtav", "rdr2", "mafiadefinitiveedition",

        // онлайн и сетевые
        "robloxplayerbeta", "robloxstudio", "fortniteclient-win64-shipping", "league of legends",
        "dota2", "hearthstone", "wow", "wowclassic", "diablo iv", "diablo3", "worldoftanks",
        "worldofwarships", "aces", "sotgame", "palworld", "onceHuman", "lostark",

        // гонки и симуляторы
        "forzahorizon5", "forzahorizon4", "forzamotorsport", "needforspeed", "needforspeedunbound",
        "f1_23", "f1_24", "acs", "ac2-win64-shipping", "beamng.drive", "eurotrucks2", "amtrucks",
        "farmingsimulator2022", "farmingsimulator2025", "wreckfest", "dirt5", "snowrunner",

        // кооперативные и вечериночные
        "lethal company", "phasmophobia", "amongus", "fallguys_client", "rocketleague",
        "humanfallflat", "gangbeasts", "brawlhalla", "itakestwo", "overcooked2",
        "fsd-win64-shipping", "maine-win64-shipping",

        // платформеры и инди
        "hollow_knight", "celeste", "hades", "hades2", "cuphead", "deadcells", "oribf",
        "oriandthewillofthewisps", "geometrydash", "gettingoverit", "riskofrain2",
        "vampiresurvivors", "balatro", "nomanssky", "nms", "stray", "littlenightmares2",

        // музыкальные и прочее
        "osu!", "beatsaber", "friday night funkin", "sims4", "ts4_x64", "thesims4",

        // небольшие и кооперативные
        "webbed", "untitled goose game", "peak", "webfishing", "crabgame", "ultimate chicken horse",
        "supermarket simulator", "schedule i", "repo", "buckshot roulette", "content warning"
    };

    /// <summary>Игра опознана по расположению файла: запущена из папки игрового магазина.</summary>
    public static bool IsGamePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        foreach (var folder in StoreFolders)
        {
            if (executablePath.Contains(folder, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static bool IsGame(string? processName, IReadOnlyCollection<string>? extra = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var name = processName.Trim();
        if (Known.Contains(name)) return true;

        foreach (var suffix in EngineSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (extra is null) return false;

        foreach (var candidate in extra)
        {
            if (string.Equals(candidate.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
