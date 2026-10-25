using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace DeskDx;

/// <summary>
/// Watches for modal dialogs of the session's own process and answers them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> clicking a button that opens a MessageBox blocks the application thread.
/// The click itself returns, but the next UIA operation against the main window hangs, because the
/// application cannot answer property requests while it is stuck inside <c>MessageBox.Show</c>.
/// </para>
/// <para>
/// <b>Polling, not events:</b> subscribing to global UIA WindowOpened events is broad and fragile;
/// polling is simple, tolerant against tree races (any failure just leads to the next poll) and fast
/// enough — the watcher is a safety net, not an interaction path.
/// </para>
/// <para>
/// <b>HWND enumeration, not the UIA desktop tree:</b> <c>MessageBox.Show(owner, …)</c> creates an
/// <em>owned</em> window, and UIA3 reports owned windows as children of their owner rather than as
/// desktop children. Enumerating the process's top-level HWNDs and resolving each via
/// <c>ElementFromHandle</c> finds the dialog wherever UIA decides to place it in the tree.
/// </para>
/// <para>
/// <b>Scope:</b> only windows of the session's own process are considered, so a parallel Notepad or
/// another test's application is never touched.
/// </para>
/// </remarks>
internal sealed class DialogWatcher
{
    // The classic Win32 dialog class name (MessageBox and most custom dialogs).
    private const string DialogClassName = "#32770";

    /// <summary>Labels accepted as the default "OK" answer, across the common locales.</summary>
    private static readonly string[] DefaultOkLabels = ["OK", "Okay", "Ok"];

    private readonly AppSession _session;
    private readonly AutomationBase _automation;
    private readonly DialogRegistry _registry;
    private readonly DialogAction _action;
    private readonly TraceLog _trace;
    private readonly TimeSpan _pollInterval;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly HashSet<IntPtr> _seen = new();

    internal DialogWatcher(
        AppSession session,
        AutomationBase automation,
        DialogRegistry registry,
        DialogAction action,
        TraceLog trace,
        TimeSpan pollInterval)
    {
        _session = session;
        _automation = automation;
        _registry = registry;
        _action = action;
        _trace = trace;
        _pollInterval = pollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : pollInterval;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DeskDx.DialogWatcher",
        };
    }

    /// <summary>Starts the watcher thread.</summary>
    internal void Start() => _thread.Start();

    /// <summary>
    /// Requests the watcher to stop and joins it (bounded). Called before the process is killed:
    /// probing UIA on a dying process only produces COM noise.
    /// </summary>
    /// <returns>True when the thread ended within the join timeout.</returns>
    internal bool Stop()
    {
        _stopRequested.Set();

        try
        {
            return _thread.Join(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // A join failure must never mask the test result.
            return false;
        }
    }

    private void Run()
    {
        while (!_stopRequested.IsSet)
        {
            try
            {
                if (!_session.IsAlive)
                {
                    // The application is gone: stop quietly instead of flooding the trace.
                    return;
                }

                PollOnce();
            }
            catch (Exception ex)
            {
                // Process died, window vanished, COM noise — log (bounded) and keep polling.
                try
                {
                    _trace.InternalLog($"dialog watcher poll failed: {ex.Message}");
                }
                catch
                {
                    // The trace may already be closed; never let that kill the watcher thread.
                }
            }

            _stopRequested.Wait(_pollInterval);
        }
    }

    private void PollOnce()
    {
        foreach (var window in ProcessWindowEnumerator.GetVisibleTopLevelWindows(_session.App.ProcessId))
        {
            try
            {
                if (!_seen.Add(window.Handle))
                {
                    // Already handled in a previous poll.
                    continue;
                }

                if (!IsDialog(window))
                {
                    continue;
                }

                HandleDialog(window);
            }
            catch (Exception)
            {
                // Window vanished mid-inspection; the outer loop keeps going.
            }
        }
    }

    /// <summary>
    /// Only modal dialogs count — a modeless second form of the app is not touched. The classic
    /// <c>#32770</c> window class identifies a MessageBox / standard dialog; the UIA WindowPattern's
    /// IsModal covers custom dialogs.
    /// </summary>
    private bool IsDialog(ProcessWindowEnumerator.WindowHandle window)
    {
        if (string.Equals(window.ClassName, DialogClassName, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var element = _automation.FromHandle(window.Handle);
            return element is not null
                   && element.Patterns.Window.IsSupported
                   && element.Patterns.Window.Pattern.IsModal.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void HandleDialog(ProcessWindowEnumerator.WindowHandle window)
    {
        var title = window.Title;

        AutomationElement? dialog = null;
        try
        {
            dialog = _automation.FromHandle(window.Handle);
        }
        catch (Exception)
        {
            // The dialog closed between enumeration and lookup; the trace still records it.
        }

        var text = dialog is null ? string.Empty : ExtractDialogText(dialog);

        // Evidence always, action according to policy.
        _trace.InternalPhase(
            $"dialog appeared: \"{title}\" — text: \"{text}\"",
            Array.Empty<string>());

        if (_action == DialogAction.LogOnly)
        {
            _trace.InternalDetail("dialog answered: \"\" (logOnly)");
            return;
        }

        if (dialog is null)
        {
            _trace.InternalDetail("dialog answered: \"\" (dialog already gone)");
            return;
        }

        var registered = _registry.FindAnswer(text);
        var button = registered is null ? FindDefaultButton(dialog) : FindButton(dialog, registered);

        if (button is null)
        {
            _trace.InternalDetail($"dialog answered: \"{registered ?? "<none>"}\" (no button found)");
            return;
        }

        var buttonName = ElementDescription.SafeName(button);
        var source = registered is null ? "default" : "registered";

        ClickButton(button);

        // Wait until the dialog is really gone: the application thread stays blocked until then,
        // so the next interaction of the test would otherwise hang.
        WaitUntilClosed(window.Handle);

        _trace.InternalDetail($"dialog answered: \"{buttonName}\" ({source})");
    }

    /// <summary>Waits (briefly) until the dialog window is gone, so the app thread is free again.</summary>
    private void WaitUntilClosed(IntPtr handle)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (!IsWindow(handle))
            {
                return;
            }

            Thread.Sleep(25);
        }
    }

    /// <summary>Title is the window name; text is the first Text/Edit control in the dialog subtree.</summary>
    private static string ExtractDialogText(AutomationElement dialog)
    {
        try
        {
            var firstText = dialog.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.Text).Or(cf.ByControlType(ControlType.Edit)));
            return firstText is null ? string.Empty : ElementDescription.SafeName(firstText);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static AutomationElement? FindDefaultButton(AutomationElement dialog)
    {
        var buttons = FindButtons(dialog);
        foreach (var label in DefaultOkLabels)
        {
            var match = buttons.FirstOrDefault(
                button => string.Equals(ElementDescription.SafeName(button), label, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // Fallback: the first button (MessageBox orders OK/Yes first for our scenarios).
        return buttons.FirstOrDefault();
    }

    private static AutomationElement? FindButton(AutomationElement dialog, string label)
        => FindButtons(dialog).FirstOrDefault(
            button => string.Equals(ElementDescription.SafeName(button), label, StringComparison.OrdinalIgnoreCase));

    private static List<AutomationElement> FindButtons(AutomationElement dialog)
    {
        try
        {
            return dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void ClickButton(AutomationElement button)
    {
        // InvokePattern first (no gate, no mouse): the dialog is modal, so pointer moves are risky.
        try
        {
            if (button.Patterns.Invoke.IsSupported)
            {
                button.Patterns.Invoke.Pattern.Invoke();
                return;
            }
        }
        catch (Exception)
        {
            // Fall through to the native click.
        }

        try
        {
            button.Click();
        }
        catch (Exception)
        {
            // The dialog may have closed between the lookup and the click.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
}
