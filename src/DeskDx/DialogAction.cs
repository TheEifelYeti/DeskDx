namespace DeskDx;

/// <summary>How the session's dialog watcher reacts to a modal dialog.</summary>
public enum DialogAction
{
    /// <summary>
    /// Log the dialog, then click the registered answer, else the default answer
    /// (OK if present, otherwise the first button). This is the default.
    /// </summary>
    AutoDismiss,

    /// <summary>
    /// Log the dialog only — the test handles it itself (explicit dialog assertions).
    /// </summary>
    LogOnly,
}
