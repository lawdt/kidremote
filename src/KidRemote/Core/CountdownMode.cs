namespace KidRemote.Core;

/// <summary>Когда показывать плашку обратного отсчёта.</summary>
internal enum CountdownMode
{
    /// <summary>Всегда, пока идёт обычный режим или безлимит.</summary>
    Always,

    /// <summary>Не показывать вовсе.</summary>
    Never,

    /// <summary>Только на последней минуте, чтобы не мешала в игре.</summary>
    LastMinute
}
