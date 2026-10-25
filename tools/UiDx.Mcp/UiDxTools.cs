using UiDx;

namespace UiDx.Mcp;

/// <summary>
/// The UiDx MCP tools as plain, in-proc callable methods.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole server behaviour. The stdio host is a thin adapter over these methods, and the
/// acceptance tests M1–M7 call them directly — no process pairing, no protocol round trip.
/// </para>
/// <para>
/// <b>All handlers return, none throws.</b> An engine failure is reported as
/// <see cref="ToolOutcome.IsError"/> with the UiDx error text (call log included), which is what the
/// agent needs to diagnose instead of blindly retrying.
/// </para>
/// <para>
/// <b>Locators, not refs.</b> Every element-addressed tool takes the locator string
/// <see cref="LocatorString"/> parses. <c>ui_click</c> always goes through the actionability gate;
/// <c>ui_invoke</c> is the deliberately gate-free escape hatch.
/// </para>
/// </remarks>
public sealed class UiDxTools
{
    /// <summary>Environment variable that switches snapshot matching into baseline-update mode.</summary>
    public const string UpdateEnvironmentVariable = "UIDX_SNAPSHOT_UPDATE";

    /// <summary>Environment variable that sets where baselines are read from and written.</summary>
    public const string SnapshotDirectoryEnvironmentVariable = "UIDX_MCP_SNAPSHOT_DIR";

    /// <summary>
    /// Points baseline matching at the directory <em>containing</em> <c>snapshots/</c>, i.e. where
    /// <c>&lt;directory&gt;/snapshots/&lt;name&gt;.uia.txt</c> lives.
    /// </summary>
    /// <remarks>
    /// Baselines are meant to be committed next to the application's tests, so the server must be told
    /// where that is: a process started by a host does not run in the project directory. Call this once
    /// at start-up. The default is <c>UIDX_MCP_SNAPSHOT_DIR</c>, else the process's current directory.
    /// </remarks>
    /// <param name="directory">The directory the <c>snapshots/</c> folder is resolved against.</param>
    public static void UseSnapshotDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        SnapshotStore.BaseDirectory = Path.GetFullPath(directory);
    }

    /// <summary>The directory baselines are matched against.</summary>
    public static string SnapshotDirectory => SnapshotStore.SnapshotDirectory;

    private readonly SessionRegistry _registry;
    private readonly TraceRoot _traceRoot;
    private readonly LaunchDelegate _launch;
    private readonly AttachDelegate _attach;

    /// <summary>Creates the tools over a session registry.</summary>
    /// <param name="registry">Owns the sessions.</param>
    /// <param name="traceRoot">Trace root for new sessions. Defaults to the process-wide root.</param>
    public UiDxTools(SessionRegistry registry, TraceRoot? traceRoot = null)
        : this(registry, traceRoot ?? TraceRoot.Process, AppSession.Launch, AppSession.Attach)
    {
    }

    private UiDxTools(SessionRegistry registry, TraceRoot traceRoot, LaunchDelegate launch, AttachDelegate attach)
    {
        _registry = registry;
        _traceRoot = traceRoot;
        _launch = launch;
        _attach = attach;
    }

    /// <summary>Starts an application and registers the resulting session.</summary>
    /// <param name="exePath">Executable (or alias) to start.</param>
    /// <param name="args">Optional command line arguments.</param>
    /// <param name="startupTimeoutMs">Timeout to wait for the main window. Defaults to the engine's 10 s.</param>
    /// <param name="expectWindowTitle">
    /// Expected main-window title (substring, case-insensitive). Needed for applications whose
    /// process owns several top-level windows — the VB6/MDI case.
    /// </param>
    public ToolOutcome Launch(
        string exePath,
        string? args = null,
        int? startupTimeoutMs = null,
        string? expectWindowTitle = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return ToolOutcome.Error("exePath is required, e.g. C:\\legacy\\MyApp.exe");
        }

        try
        {
            var session = _launch(
                exePath,
                args,
                startupTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                NewOptions(expectWindowTitle));

            return ToolOutcome.Ok(DescribeSession(session, _registry.Register(session)));
        }
        catch (UiDxTimeoutError error)
        {
            return ToolOutcome.Error(error.Message);
        }
        catch (UiDxCrashError error)
        {
            return ToolOutcome.Error(error.Message);
        }
        catch (Exception error)
        {
            return ToolOutcome.Error($"launch {exePath} failed: {error.Message}");
        }
    }

    /// <summary>Attaches to an already running process instead of starting one.</summary>
    /// <param name="processName">Image name or executable, e.g. <c>MyApp</c> or <c>MyApp.exe</c>.</param>
    public ToolOutcome Attach(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return ToolOutcome.Error("processName is required, e.g. MyApp or MyApp.exe");
        }

        try
        {
            var session = _attach(processName, null, NewOptions(expectWindowTitle: null));
            return ToolOutcome.Ok(DescribeSession(session, _registry.Register(session)));
        }
        catch (UiDxTimeoutError error)
        {
            return ToolOutcome.Error(error.Message);
        }
        catch (Exception error)
        {
            return ToolOutcome.Error($"attach to \"{processName}\" failed: {error.Message}");
        }
    }

    /// <summary>Ends a session: stops an owned process and closes its trace.</summary>
    public ToolOutcome Dispose(string sessionId)
    {
        if (!_registry.TryGet(sessionId, out _))
        {
            return UnknownSession(sessionId);
        }

        _registry.DisposeSession(sessionId);
        return ToolOutcome.Ok($"ok: session {sessionId} disposed");
    }

    // ── Observation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the canonical UIA tree as text — the whole main window, or a locator's subtree.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">Optional scope; the main window when omitted.</param>
    /// <param name="includeState">Add the <c>(enabled|disabled)</c> state to every line.</param>
    /// <param name="excludeIds">AutomationIds/Names whose subtree is left out (volatile controls).</param>
    public ToolOutcome Snapshot(
        string sessionId,
        string? locator = null,
        bool? includeState = null,
        string[]? excludeIds = null)
        => Run(sessionId, session =>
        {
            var options = new UiaSnapshotOptions
            {
                IncludeState = includeState ?? false,
                ExcludeIds = excludeIds ?? [],
            };

            string text;
            if (string.IsNullOrWhiteSpace(locator))
            {
                // Element-addressed: no name round trip, so an unnamed or duplicated window title
                // cannot break the snapshot.
                var windowHandle = session.MainWindow?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
                if (windowHandle == IntPtr.Zero)
                {
                    return ToolOutcome.Error("session has no main window to snapshot");
                }

                text = session.ToUiaSnapshot(windowHandle, options);
            }
            else
            {
                text = LocatorString.Build(session, locator).ToUiaSnapshot(options);
            }

            return ToolOutcome.Ok(text.Length == 0 ? "(empty)" : text.TrimEnd('\n'));
        });

    /// <summary>
    /// Compares the snapshot against the baseline <c>snapshots/&lt;baselineName&gt;.uia.txt</c> and
    /// returns the unified diff on mismatch.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">Optional scope; the main window when omitted.</param>
    /// <param name="baselineName">Baseline name without the <c>.uia.txt</c> suffix.</param>
    /// <param name="update">Write the baseline instead of comparing (explicit, never silent).</param>
    /// <param name="excludeIds">
    /// AutomationIds/Names left out of the comparison. A baseline over a form with timer-driven or
    /// otherwise volatile captions is only stable with these exclusions, so the structural contract
    /// keeps meaning the structure.
    /// </param>
    public ToolOutcome MatchSnapshot(
        string sessionId,
        string? locator,
        string baselineName,
        bool? update = null,
        string[]? excludeIds = null)
    {
        if (string.IsNullOrWhiteSpace(baselineName))
        {
            return ToolOutcome.Error("baselineName is required");
        }

        return Run(
            sessionId,
            session =>
            {
                var target = string.IsNullOrWhiteSpace(locator)
                    ? session.GetByName(RequireMainWindowName(session))
                    : LocatorString.Build(session, locator);

                using var updateScope = update is true
                    ? new EnvironmentOverride(UpdateEnvironmentVariable, "1")
                    : null;

                target.ToMatchUiaSnapshot(baselineName, new UiaSnapshotOptions
                {
                    Timeout = session.DefaultTimeout,
                    ExcludeIds = excludeIds ?? [],
                });

                return ToolOutcome.Ok(
                    $"matched=true: {baselineName}" + (update is true ? " (baseline written)" : string.Empty));
            },
            onExpectError: error => ToolOutcome.Error($"matched=false: {baselineName}\n{error.Message}"));
    }

    /// <summary>Returns the tail of the session's call log, dialog history included.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="lastLines">How many trailing lines to return. Default 50.</param>
    public ToolOutcome GetTrace(string sessionId, int? lastLines = null)
    {
        var effectiveLastLines = lastLines is > 0 ? lastLines.Value : 50;

        return Run(sessionId, session =>
        {
            var lines = session.Trace.ToCallLog();
            if (lines.Count == 0)
            {
                return ToolOutcome.Ok("(trace is empty)");
            }

            var tail = lines.Count <= effectiveLastLines
                ? lines
                : lines.Skip(lines.Count - effectiveLastLines);

            return ToolOutcome.Ok(string.Join('\n', tail).TrimEnd());
        });
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Gate-checked mouse click (Visible + Stable + ReceivesEvents + Enabled).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">The target locator.</param>
    /// <param name="force">Skip Visible/Stable/ReceivesEvents. Enabled is still checked.</param>
    /// <param name="timeoutMs">Gate timeout. Defaults to the session's default timeout.</param>
    public ToolOutcome Click(string sessionId, string locator, bool? force = null, int? timeoutMs = null)
    {
        var timeout = ToTimeout(timeoutMs);
        return Action(sessionId, locator, "click", target => target.Click(timeout, force ?? false));
    }

    /// <summary>Gate-checked value fill (Visible + Editable + Enabled).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">The target locator.</param>
    /// <param name="text">The value to set.</param>
    /// <param name="timeoutMs">Gate timeout. Defaults to the session's default timeout.</param>
    public ToolOutcome Fill(string sessionId, string locator, string text, int? timeoutMs = null)
    {
        var timeout = ToTimeout(timeoutMs);
        return Action(sessionId, locator, "fill", target => target.Fill(text ?? string.Empty, timeout));
    }

    /// <summary>Gate-checked combo selection by item label (Visible + Enabled).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">The target locator.</param>
    /// <param name="item">The item label to select.</param>
    /// <param name="timeoutMs">Gate timeout. Defaults to the session's default timeout.</param>
    public ToolOutcome Select(string sessionId, string locator, string item, int? timeoutMs = null)
    {
        var timeout = ToTimeout(timeoutMs);
        return string.IsNullOrWhiteSpace(item)
            ? ToolOutcome.Error("item is required for ui_select")
            : Action(sessionId, locator, "select", target => target.Select(item, timeout));
    }

    /// <summary>
    /// Gate-free activation of a control: deterministic, for targets that must not wait — a
    /// deliberately disabled control, or a button that opens a modal dialog.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">The target locator.</param>
    /// <param name="timeoutMs">Resolve timeout. Defaults to the session's default timeout.</param>
    /// <remarks>
    /// <para>
    /// <b>Asynchronous by design.</b> <c>InvokePattern.Invoke</c> on a control that opens a modal
    /// dialog does not return cleanly: the provider's UI thread sits inside <c>MessageBox.Show</c>,
    /// so the cross-process call either blocks or fails with a UIA timeout — and a blocking call on
    /// the caller's thread would hang the agent. It is also unsafe to run that call on a second
    /// thread, because UIA client objects are not thread-safe and would race the session's dialog
    /// watcher (observed: the watcher then finds the dialog but can no longer read it).
    /// </para>
    /// <para>
    /// So the control is activated the way the engine's own dialog tests do it: a posted
    /// <c>BM_CLICK</c> to the element's window handle. That is asynchronous, needs no foreground
    /// window and touches no UIA state, which leaves the watcher free to answer the dialog — after
    /// which the trace shows both the dialog and its answer. Controls without a window handle fall
    /// back to <c>InvokePattern</c>.
    /// </para>
    /// </remarks>
    public ToolOutcome Invoke(string sessionId, string locator, int? timeoutMs = null)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return ToolOutcome.Error("locator is required for ui_invoke");
        }

        if (!_registry.TryGet(sessionId, out var handle))
        {
            return UnknownSession(sessionId);
        }

        var resolveTimeout = ToTimeout(timeoutMs) ?? handle.Session.DefaultTimeout;

        lock (handle.Gate)
        {
            var trace = handle.Session.Trace;
            var mark = trace.EventCount;

            try
            {
                // Resolve on THIS thread only: no second thread may touch the session's automation.
                var element = LocatorString.Build(handle.Session, locator).Resolve(resolveTimeout);
                var windowHandle = element.Properties.NativeWindowHandle.ValueOrDefault;

                if (windowHandle != IntPtr.Zero)
                {
                    AsyncTrigger.PostButtonClick(windowHandle);
                    trace.InternalLog("invoke executed: activation posted (gate skipped)");
                }
                else
                {
                    var pattern = element.Patterns.Invoke.PatternOrDefault
                                  ?? throw new UiDxTimeoutError(
                                      $"invoke locator({locator})",
                                      resolveTimeout,
                                      trace.ToCallLog());

                    pattern.Invoke();
                    trace.InternalLog("invoke executed (gate skipped)");
                }

                return ToolOutcome.Ok(CallLogSince(trace, mark));
            }
            catch (Exception error) when (IsEngineError(error))
            {
                return ToolOutcome.Error(error.Message);
            }
        }
    }

    /// <summary>Polls a web-first assertion and returns the failing evidence on failure.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="locator">The target locator.</param>
    /// <param name="assertion">
    /// One of <c>visible</c>, <c>hidden</c>, <c>enabled</c>, <c>editable</c>, <c>hasValue</c>, <c>hasText</c>.
    /// </param>
    /// <param name="expected">Required for <c>hasValue</c>/<c>hasText</c>.</param>
    /// <param name="timeoutMs">Timeout. Defaults to the session's default timeout.</param>
    public ToolOutcome Expect(
        string sessionId,
        string locator,
        string assertion,
        string? expected = null,
        int? timeoutMs = null)
    {
        if (string.IsNullOrWhiteSpace(assertion))
        {
            return ToolOutcome.Error(
                "assertion is required: visible, hidden, enabled, editable, hasValue or hasText");
        }

        var timeout = timeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;

        return Run(sessionId, session =>
        {
            var assertions = UiDx.Expect.That(LocatorString.Build(session, locator));

            switch (assertion.Trim().ToLowerInvariant())
            {
                case "visible":
                    assertions.ToBeVisible(timeout);
                    break;
                case "hidden":
                    assertions.ToBeHidden(timeout);
                    break;
                case "enabled":
                    assertions.ToBeEnabled(timeout);
                    break;
                case "editable":
                    assertions.ToBeEditable(timeout);
                    break;
                case "hasvalue":
                    assertions.ToHaveValue(RequireExpected(assertion, expected), timeout);
                    break;
                case "hastext":
                    assertions.ToHaveText(RequireExpected(assertion, expected), timeout);
                    break;
                default:
                    return ToolOutcome.Error(
                        $"unknown assertion \"{assertion}\"; "
                        + "expected visible, hidden, enabled, editable, hasValue or hasText");
            }

            return ToolOutcome.Ok(
                expected is null ? $"expect {assertion}: ok" : $"expect {assertion} \"{expected}\": ok");
        });
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private AppSessionOptions NewOptions(string? expectWindowTitle)
        => new()
        {
            TraceDirectory = _traceRoot.Directory,
            MainWindowTitle = string.IsNullOrWhiteSpace(expectWindowTitle) ? null : expectWindowTitle,

            // The agent must not die on a MessageBox: the watcher answers it and writes the dialog to
            // the trace, where ui_get_trace can read it.
            DialogAction = DialogAction.AutoDismiss,
        };

    private static string RequireExpected(string assertion, string? expected)
        => expected ?? throw new FormatException($"assertion \"{assertion}\" requires the expected value");

    private static TimeSpan? ToTimeout(int? timeoutMs)
        => timeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static string RequireMainWindowName(AppSession session)
        => session.MainWindow?.Name is { Length: > 0 } name
            ? name
            : throw new FormatException(
                "the session's main window has no name; pass an explicit locator to compare a snapshot");

    private ToolOutcome UnknownSession(string sessionId)
        => ToolOutcome.Error(
            $"unknown sessionId \"{sessionId}\"; live sessions: {_registry.DescribeKnownSessions()}");

    /// <summary>
    /// Runs <paramref name="body"/> against a session: an unknown id, an engine failure and a
    /// malformed locator all become error outcomes, and concurrent operations on one session are
    /// serialized by the session's gate.
    /// </summary>
    private ToolOutcome Run(
        string sessionId,
        Func<AppSession, ToolOutcome> body,
        Func<UiDxExpectError, ToolOutcome>? onExpectError = null)
    {
        if (!_registry.TryGet(sessionId, out var handle))
        {
            return UnknownSession(sessionId);
        }

        lock (handle.Gate)
        {
            try
            {
                return body(handle.Session);
            }
            catch (UiDxExpectError error)
            {
                return onExpectError?.Invoke(error) ?? ToolOutcome.Error(error.Message);
            }
            catch (Exception error) when (IsEngineError(error))
            {
                return ToolOutcome.Error(error.Message);
            }
        }
    }

    /// <summary>
    /// Runs a gate-checked action and returns the call-log lines it produced: the executed line on
    /// success, the failing gate checks plus the state dump on failure.
    /// </summary>
    private ToolOutcome Action(string sessionId, string locator, string toolName, Action<Locator> action)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return ToolOutcome.Error($"locator is required for ui_{toolName}");
        }

        if (!_registry.TryGet(sessionId, out var handle))
        {
            return UnknownSession(sessionId);
        }

        lock (handle.Gate)
        {
            var trace = handle.Session.Trace;
            var mark = trace.EventCount;

            try
            {
                action(LocatorString.Build(handle.Session, locator));
            }
            catch (Exception error) when (IsEngineError(error))
            {
                // The engine already wrote the failing checks and the state dump to the call log, and
                // both travel with the error: the agent can read why the action did not happen.
                return ToolOutcome.Error(error.Message);
            }

            return ToolOutcome.Ok(CallLogSince(trace, mark));
        }
    }

    /// <summary>The call log lines recorded after <paramref name="mark"/>.</summary>
    private static string CallLogSince(TraceLog trace, int mark)
    {
        var lines = trace.ToCallLog();
        var tail = lines.Count <= mark ? [] : lines.Skip(mark).ToList();
        return tail.Count == 0 ? "(no call log output)" : string.Join('\n', tail).TrimEnd();
    }

    /// <summary>
    /// Engine failures that are normal exploration outcomes: a gate timeout, a strict-mode violation,
    /// a crashed application, or a malformed locator. They carry the evidence in their message.
    /// </summary>
    private static bool IsEngineError(Exception error)
        => error is UiDxTimeoutError
            or UiDxStrictError
            or UiDxCrashError
            or UiDxExpectError
            or FormatException
            or ArgumentException
            or InvalidOperationException;

    private static string DescribeSession(AppSession session, string sessionId)
    {
        var window = session.MainWindow;
        var descendants = 0;
        try
        {
            if (window is not null)
            {
                descendants = window.FindAllDescendants().Length;
            }
        }
        catch (Exception)
        {
            // A VB6 MSAA bridge can refuse the count right at launch; the number is informational.
        }

        return string.Join(
            '\n',
            $"sessionId: {sessionId}",
            $"mainWindow: \"{window?.Name ?? "(none)"}\"",
            $"windowClass: {SafeClassName(window)}",
            $"descendants: {descendants}",
            $"tracePath: {session.Trace.SessionDirectory}");
    }

    private static string SafeClassName(FlaUI.Core.AutomationElements.AutomationElement? element)
    {
        try
        {
            return element?.ClassName ?? "n/a";
        }
        catch (Exception)
        {
            // ClassName throws on some elements (title bars, VB6 controls): always guard.
            return "n/a";
        }
    }

    private delegate AppSession LaunchDelegate(
        string exePath,
        string? args,
        TimeSpan? startupTimeout,
        AppSessionOptions? options);

    private delegate AppSession AttachDelegate(
        string processName,
        TimeSpan? attachTimeout,
        AppSessionOptions? options);

    /// <summary>Restores an environment variable when the scope ends.</summary>
    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentOverride(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

/// <summary>
/// The trace root that every session of one server instance writes into.
/// </summary>
/// <remarks>
/// One root per process keeps a conversation's traces together: every tool response points at it, and
/// an evidence run can archive it as a whole. <c>UIDX_MCP_TRACE_DIR</c> overrides the default
/// location below the temp folder.
/// </remarks>
public sealed class TraceRoot
{
    /// <summary>The root used by a server process that does not configure one.</summary>
    public static TraceRoot Process { get; } = new();

    /// <summary>Creates a root below the temp folder, or at <c>UIDX_MCP_TRACE_DIR</c> when set.</summary>
    public TraceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("UIDX_MCP_TRACE_DIR");
        Directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Path.GetTempPath(),
                "UiDx.Mcp",
                DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture))
            : Path.GetFullPath(configured);
    }

    /// <summary>Creates a root at an explicit directory (used by the tests for isolation).</summary>
    /// <param name="directory">Directory session traces are created below.</param>
    public TraceRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>The directory that session traces are created below.</summary>
    public string Directory { get; }
}
