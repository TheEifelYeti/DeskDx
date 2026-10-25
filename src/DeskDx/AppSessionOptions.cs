namespace DeskDx;

/// <summary>
/// Options controlling an <see cref="AppSession"/>.
/// </summary>
/// <remarks>
/// The options are captured by <see cref="AppSession.Launch(string, string?, System.TimeSpan?, AppSessionOptions?)"/>
/// and <see cref="AppSession.Attach(string, System.TimeSpan?, AppSessionOptions?)"/>. Changing a property afterwards
/// does not reconfigure a running session.
/// </remarks>
public sealed class AppSessionOptions
{
    /// <summary>
    /// Default timeout for operations. Schritt 0: 5 s, not 30.
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// UIA2 fallback (FlaUI warning: UIA3 "bugs with WinForms"). UIA3 is used by default.
    /// </summary>
    public bool UseUia2 { get; init; }

    /// <summary>
    /// Root directory for per-session trace directories. Relative paths resolve against the current directory.
    /// </summary>
    public string TraceDirectory { get; init; } = "traces";

    /// <summary>
    /// How the session's dialog watcher reacts to a modal dialog. Defaults to
    /// <see cref="DialogAction.AutoDismiss"/>.
    /// </summary>
    public DialogAction DialogAction { get; init; } = DialogAction.AutoDismiss;

    /// <summary>Poll interval of the dialog watcher. Default 250 ms.</summary>
    public TimeSpan DialogPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Expected title of the session's main window (substring, case-insensitive). When set, the
    /// session selects that window instead of guessing.
    /// </summary>
    /// <remarks>
    /// Needed for applications whose process owns several top-level windows. Win32's
    /// <c>MainWindowHandle</c> is a heuristic, and for VB6 MDI applications it resolves to the
    /// control-less <c>ThunderRT6Main</c> container rather than the form that carries the UI.
    /// </remarks>
    public string? MainWindowTitle { get; init; }
}
