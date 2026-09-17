namespace KidRemote.Core;

/// <summary>Когда расходуется выданное время.</summary>
internal enum TrackingMode
{
    /// <summary>Любое использование компьютера.</summary>
    Always,

    /// <summary>Только приложение, развёрнутое на весь монитор.</summary>
    Fullscreen,

    /// <summary>Полноэкранные приложения и опознанные игры, в том числе в окне.</summary>
    Games
}
