using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;
using FlaUI.UIA3;

namespace DeskDx;

/// <summary>
/// Lifecycle owner for an application under automation: launch/attach, cleanup guarantee
/// and the <see cref="TraceLog"/> of the session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership rule:</b> whoever started the process decides about killing it. A session created
/// by <see cref="Launch"/> owns its process and terminates it in <see cref="Dispose"/>;
/// a session created by <see cref="Attach"/> never kills the foreign process.
/// </para>
/// <para>
/// <b>Crash detection:</b> <see cref="IsAlive"/> is a polling property and never throws.
/// The next operation calls <see cref="EnsureAlive"/> and throws <see cref="DeskDxCrashError"/>
/// with the exit code and the trace path in the call log.
/// </para>
/// </remarks>
public sealed class AppSession : IDisposable
{
    /// <summary>Default timeout to wait for the main window after launching.</summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly AppSessionOptions _options;
    private readonly AutomationBase _automation;
    private readonly TraceLog _trace;
    private readonly bool _ownsProcess;

    private Application _app;
    private Window? _mainWindow;
    private DialogWatcher? _dialogWatcher;
    private bool _disposed;

    private AppSession(
        Application app,
        AutomationBase automation,
        TraceLog trace,
        AppSessionOptions options,
        bool ownsProcess)
    {
        _app = app;
        _automation = automation;
        _trace = trace;
        _options = options;
        _ownsProcess = ownsProcess;
        Dialogs = new DialogRegistry();
    }

    /// <summary>
    /// Starts <paramref name="exePath"/> and waits for its main window.
    /// </summary>
    /// <param name="exePath">The executable (or alias) to start, e.g. "notepad.exe".</param>
    /// <param name="args">Optional command line arguments.</param>
    /// <param name="startupTimeout">Timeout to wait for the main window. Defaults to 10 s.</param>
    /// <param name="options">Session options. Defaults to a fresh <see cref="AppSessionOptions"/>.</param>
    /// <exception cref="DeskDxTimeoutError">No main window appeared within the timeout.</exception>
    public static AppSession Launch(
        string exePath,
        string? args = null,
        TimeSpan? startupTimeout = null,
        AppSessionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        var effectiveOptions = options ?? new AppSessionOptions();
        var timeout = startupTimeout ?? DefaultStartupTimeout;
        var automation = CreateAutomation(effectiveOptions);
        var trace = TraceLog.CreateSession(effectiveOptions.TraceDirectory);

        // Processes with this image name that already exist. Used to recognize a process
        // that is forked by an app-execution alias (e.g. the Notepad store app stub).
        var processName = Path.GetFileNameWithoutExtension(exePath);
        var preexistingProcessIds = GetProcessIdsByName(processName);
        var stopwatch = Stopwatch.StartNew();

        Application app;
        try
        {
            var startInfo = string.IsNullOrEmpty(args)
                ? new ProcessStartInfo(exePath)
                : new ProcessStartInfo(exePath, args);
            app = Application.Launch(startInfo);
        }
        catch (Exception ex)
        {
            // A launch failure is not a timeout: rethrow the original exception so that the
            // cause (e.g. Win32Exception for a missing executable) stays visible. DeskDxTimeoutError
            // is reserved for real timeouts.
            trace.InternalPhase(
                $"launching {exePath} (timeout {timeout.TotalMilliseconds:0}ms)",
                new[] { $"launch failed: {ex.Message}" });
            trace.InternalPhase("session ended (exit n/a)", Array.Empty<string>());
            automation.Dispose();
            trace.Close();
            throw;
        }

        trace.InternalLog($"launched {exePath} (pid {app.ProcessId})");

        var window = WaitForMainWindow(app, automation, Remaining(stopwatch, timeout));

        if (window is null && app.HasExited)
        {
            // App-execution alias (e.g. the Notepad store app on Windows 11): the started process
            // is a stub that immediately hands off to the real application and exits. Adopt the new
            // process with the same image name once it owns a main window.
            var forkedOwner = WaitForNewWindowedProcess(processName, preexistingProcessIds, Remaining(stopwatch, timeout));
            if (forkedOwner is not null)
            {
                trace.InternalLog($"launched process handed off to {processName} (pid {forkedOwner.Id})");
                app.Dispose();
                app = Application.Attach(forkedOwner);
                window = WaitForMainWindow(app, automation, Remaining(stopwatch, timeout));
            }
        }

        if (window is null)
        {
            trace.InternalPhase(
                $"waiting for main window of {exePath} (timeout {timeout.TotalMilliseconds:0}ms)",
                new[] { "no main window found" });
            trace.InternalPhase("session ended (exit n/a)", Array.Empty<string>());
            try
            {
                automation.Dispose();
            }
            finally
            {
                if (!app.HasExited)
                {
                    app.Kill();
                }

                app.Dispose();
                trace.Close();
            }

            throw new DeskDxTimeoutError($"launch {exePath}", timeout, trace.ToCallLog());
        }

        trace.InternalPhase(
            $"waiting for main window of {exePath} (timeout {timeout.TotalMilliseconds:0}ms)",
            new[] { $"main window found: \"{window.Name}\"" });
        var session = new AppSession(app, automation, trace, effectiveOptions, ownsProcess: true)
        {
            _mainWindow = window,
        };

        // The window above came from MainWindowHandle. For applications whose process owns several
        // top-level windows that heuristic can pick a container without any content, which would scope
        // every locator to an empty tree — so the session re-evaluates once the UI is up.
        session.EnsureRichestMainWindow(Remaining(stopwatch, timeout));
        session.StartDialogWatcher();
        return session;
    }

    /// <summary>
    /// Attaches to a running process with the given image name.
    /// </summary>
    /// <param name="processName">The process name or executable, e.g. "notepad" or "notepad.exe".</param>
    /// <param name="attachTimeout">Timeout to find the process. Defaults to <see cref="AppSessionOptions.DefaultTimeout"/>.</param>
    /// <param name="options">Session options. Defaults to a fresh <see cref="AppSessionOptions"/>.</param>
    /// <exception cref="DeskDxTimeoutError">No matching process was found within the timeout.</exception>
    public static AppSession Attach(
        string processName,
        TimeSpan? attachTimeout = null,
        AppSessionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        var effectiveOptions = options ?? new AppSessionOptions();
        var timeout = attachTimeout ?? effectiveOptions.DefaultTimeout;
        var automation = CreateAutomation(effectiveOptions);
        var trace = TraceLog.CreateSession(effectiveOptions.TraceDirectory);

        var name = Path.GetFileNameWithoutExtension(processName);
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        while (process is null && stopwatch.Elapsed < timeout)
        {
            process = FindProcess(name);
            if (process is null)
            {
                Thread.Sleep(PollInterval);
            }
        }

        if (process is null)
        {
            trace.InternalPhase(
                $"attaching to process \"{name}\" (timeout {timeout.TotalMilliseconds:0}ms)",
                new[] { "no matching process found" });
            trace.InternalPhase("session ended (exit n/a)", Array.Empty<string>());
            automation.Dispose();
            trace.Close();
            throw new DeskDxTimeoutError($"attach to process \"{name}\"", timeout, trace.ToCallLog());
        }

        trace.InternalLog($"attached {name} (pid {process.Id})");
        var app = Application.Attach(process);
        var window = WaitForMainWindow(app, automation, Remaining(stopwatch, timeout));
        trace.InternalPhase(
            $"waiting for main window of {name} (timeout {timeout.TotalMilliseconds:0}ms)",
            new[] { window is null ? "no main window found" : $"main window found: \"{window.Name}\"" });
        var session = new AppSession(app, automation, trace, effectiveOptions, ownsProcess: false)
        {
            _mainWindow = window,
        };
        session.EnsureRichestMainWindow(Remaining(stopwatch, timeout));
        session.StartDialogWatcher();
        return session;
    }

    /// <summary>Raw access to the underlying application (escape hatch).</summary>
    public Application App => _app;

    /// <summary>UIA3 by default, UIA2 when <see cref="AppSessionOptions.UseUia2"/> is set.</summary>
    public AutomationBase Automation => _automation;

    /// <summary>The call log of this session.</summary>
    public TraceLog Trace => _trace;

    /// <summary>Registered answers for the session's dialog watcher.</summary>
    public DialogRegistry Dialogs { get; }

    /// <summary>The timeout used for operations of this session.</summary>
    public TimeSpan DefaultTimeout => _options.DefaultTimeout;

    /// <summary>
    /// The main window of the session's process, or null when no main window can be resolved.
    /// Locators created from this session search inside this window.
    /// </summary>
    /// <remarks>
    /// The window is resolved once and then cached. Re-resolving it on every access would be
    /// fragile: after a combo box is opened, <see cref="Application.MainWindowHandle"/> can point at
    /// the drop-down popup window instead of the real main window.
    /// </remarks>
    public Window? MainWindow => _mainWindow ??= GetMainWindow(_app, _automation);

    /// <summary>Starts a locator that searches the main window of this session.</summary>
    public Locator GetByAutomationId(string automationId)
        => new Locator(this).GetByAutomationId(automationId);

    /// <summary>Starts a locator that searches the main window of this session.</summary>
    public Locator GetByName(string name)
        => new Locator(this).GetByName(name);

    /// <summary>Starts a locator that searches the main window of this session.</summary>
    public Locator GetByControlType(ControlType type)
        => new Locator(this).GetByControlType(type);

    /// <summary>
    /// Renders the subtree of the given top-level window of this session's process as canonical
    /// snapshot text.
    /// </summary>
    /// <remarks>
    /// This is the element-addressed counterpart of <see cref="Locator.ToUiaSnapshot"/>: it lets a
    /// caller snapshot a window that the session would not choose as its main window, and it avoids
    /// re-implementing the format outside the library. Locators — and therefore
    /// <see cref="Locator.ToMatchUiaSnapshot"/> — always operate on <see cref="MainWindow"/>, so a
    /// baseline for a different window must be produced here.
    /// </remarks>
    /// <param name="windowHandle">Handle of a top-level window that belongs to this session's process.</param>
    /// <param name="options">Snapshot options; <see cref="UiaSnapshotOptions.Timeout"/> is not used.</param>
    public string ToUiaSnapshot(IntPtr windowHandle, UiaSnapshotOptions? options = null)
    {
        EnsureAlive();

        var effectiveOptions = options ?? new UiaSnapshotOptions();
        AutomationElement element;
        try
        {
            element = _automation.FromHandle(windowHandle);
        }
        catch (Exception ex)
        {
            throw new DeskDxTimeoutError(
                $"window handle {windowHandle} of pid {_app.ProcessId}",
                effectiveOptions.Timeout ?? _options.DefaultTimeout,
                _trace.ToCallLog(),
                ex);
        }

        if (!effectiveOptions.Settle)
        {
            return UiaSnapshotRenderer.Render(element, effectiveOptions);
        }

        // Same settle rule as the locator path: two identical captures in a row, because a VB6
        // MSAA-to-UIA bridge can still be building its tree.
        var settleTimeout = effectiveOptions.SettleTimeout ?? effectiveOptions.Timeout ?? _options.DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        var previous = UiaSnapshotRenderer.Render(element, effectiveOptions);
        var attempts = 1;

        while (stopwatch.Elapsed < settleTimeout)
        {
            Thread.Sleep(effectiveOptions.SettleDelay);
            var current = UiaSnapshotRenderer.Render(element, effectiveOptions);
            attempts++;

            if (string.Equals(previous, current, StringComparison.Ordinal))
            {
                if (attempts > 2)
                {
                    _trace.InternalLog($"snapshot settled after {attempts} captures");
                }

                return current;
            }

            previous = current;
        }

        _trace.InternalLog(
            $"snapshot did not settle within {settleTimeout.TotalMilliseconds:0}ms ({attempts} captures); using the last capture");
        return previous;
    }

    /// <summary>Is the process still alive? Poll property, never throws.</summary>
    public bool IsAlive
    {
        get
        {
            try
            {
                return !_app.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Verifies that the process is still alive and throws <see cref="DeskDxCrashError"/>
    /// (with exit code, trace path and call log) otherwise. Operations call this before
    /// touching the automation tree.
    /// </summary>
    public void EnsureAlive()
    {
        if (IsAlive)
        {
            return;
        }

        var exitCode = GetExitCode();
        _trace.InternalPhase(
            $"process crashed (exit code {exitCode})",
            new[] { $"trace directory: {_trace.SessionDirectory}" });
        throw new DeskDxCrashError("session crashed", exitCode, _trace.SessionDirectory, _trace.ToCallLog());
    }

    /// <summary>
    /// Hard-stops and cleans up the application. Only kills processes this session started;
    /// for an attached foreign process it is a no-op.
    /// </summary>
    public void Kill()
    {
        if (!_ownsProcess)
        {
            _trace.InternalLog("skipping kill: process is not owned by this session (attached)");
            return;
        }

        try
        {
            // Graceful close first, hard kill as fallback.
            _app.Close(killIfCloseFails: false);
            if (!_app.HasExited)
            {
                _app.Kill();
            }
        }
        catch (Exception ex)
        {
            _trace.InternalLog($"kill failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleanup guarantee: the trace is always finalized (even after a test exception) and a
    /// self-started process is always terminated.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Stop the watcher BEFORE killing the process: probing UIA on a dying process only
            // produces COM noise.
            _dialogWatcher?.Stop();

            Kill();
            var exitCode = GetExitCode();
            _trace.InternalPhase($"session ended (exit {exitCode})", Array.Empty<string>());
        }
        catch (Exception ex)
        {
            try
            {
                _trace.InternalLog($"session teardown failed: {ex.Message}");
            }
            catch
            {
                // Never let cleanup masking a test result.
            }
        }
        finally
        {
            try
            {
                _automation.Dispose();
            }
            finally
            {
                try
                {
                    _app.Dispose();
                }
                finally
                {
                    _trace.Close();
                }
            }
        }
    }

    private static AutomationBase CreateAutomation(AppSessionOptions options)
        => options.UseUia2 ? new UIA2Automation() : new UIA3Automation();

    private void StartDialogWatcher()
    {
        _dialogWatcher = new DialogWatcher(
            this,
            _automation,
            Dialogs,
            _options.DialogAction,
            _trace,
            _options.DialogPollInterval);
        _dialogWatcher.Start();
    }

    private static TimeSpan Remaining(Stopwatch stopwatch, TimeSpan timeout)
    {
        var remaining = timeout - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Finds a process by image name, preferring one that already owns a main window.
    /// The caller owns the returned process.
    /// </summary>
    private static Process? FindProcess(string processName)
    {
        Process? windowed = null;
        foreach (var candidate in Process.GetProcessesByName(processName))
        {
            if (windowed is null && HasMainWindow(candidate))
            {
                windowed = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        if (windowed is not null)
        {
            return windowed;
        }

        // No process with a window (yet): fall back to the first match.
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            return null;
        }

        var first = processes[0];
        for (var i = 1; i < processes.Length; i++)
        {
            processes[i].Dispose();
        }

        return first;
    }

    /// <summary>
    /// Waits for a new process of the given image name that owns a main window and is not part of
    /// <paramref name="knownProcessIds"/>. The caller owns the returned process.
    /// </summary>
    private static Process? WaitForNewWindowedProcess(string processName, int[] knownProcessIds, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Process? match = null;
            foreach (var candidate in Process.GetProcessesByName(processName))
            {
                if (match is null
                    && HasMainWindow(candidate)
                    && Array.IndexOf(knownProcessIds, candidate.Id) < 0)
                {
                    match = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            if (match is not null)
            {
                return match;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                return null;
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static int[] GetProcessIdsByName(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(process => process.Id).ToArray();
        }
        finally
        {
            Array.ForEach(processes, process => process.Dispose());
        }
    }

    private static bool HasMainWindow(Process process)
    {
        try
        {
            return !process.HasExited && process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            // The process exited between enumeration and probing.
            return false;
        }
    }

    private static Window? WaitForMainWindow(Application app, AutomationBase automation, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (app.HasExited)
            {
                return null;
            }

            var window = TryGetMainWindow(app, automation);
            if (window is not null)
            {
                return window;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                return null;
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>
    /// Resolves the main window, treating a UIA failure as "not ready yet".
    /// </summary>
    /// <remarks>
    /// <c>ElementFromHandle</c> is a cross-process call and throws a COM timeout while the target's
    /// UI thread is still initialising (observed with a VB6 application whose splash screen and
    /// plug-in scan occupy the message pump). Without this guard that exception escapes
    /// <see cref="Launch"/> entirely instead of being retried, and the caller never gets a session.
    /// </remarks>
    private static Window? TryGetMainWindow(Application app, AutomationBase automation)
    {
        try
        {
            return GetMainWindow(app, automation);
        }
        catch (Exception)
        {
            // The window is not reachable yet; the caller's poll loop retries.
            return null;
        }
    }

    private static Window? GetMainWindow(Application app, AutomationBase automation)
    {
        // Refresh the cached process so that a newly created main window handle is picked up.
        app.WaitWhileMainHandleIsMissing(TimeSpan.Zero);
        var handle = app.MainWindowHandle;
        return handle == IntPtr.Zero ? null : automation.FromHandle(handle)?.AsWindow();
    }

    /// <summary>
    /// Switches <see cref="MainWindow"/> to a richer window when the process owns one and the current
    /// main window carries no content. Best effort: the session keeps the window it already has when
    /// the ranking finds nothing better, so single-window applications are unaffected.
    /// </summary>
    private void EnsureRichestMainWindow(TimeSpan timeout)
    {
        var current = _mainWindow;
        var currentCount = current is null ? 0 : CountDescendants(current);

        // A main window with content is already the right answer; do not go looking for another one.
        if (currentCount > 0 && string.IsNullOrWhiteSpace(_options.MainWindowTitle))
        {
            return;
        }

        try
        {
            UseMainWindow(timeout);

            var chosenCount = _mainWindow is null ? 0 : CountDescendants(_mainWindow);

            // Only actually switch when the alternative is genuinely richer; otherwise restore the
            // window Win32 reported so single-window behaviour is byte-for-byte what it was.
            if (current is not null && chosenCount <= currentCount)
            {
                _trace.InternalLog(
                    $"keeping main window \"{current.Name}\" ({currentCount} descendants); "
                    + $"richest candidate had {chosenCount}");
                _mainWindow = current;
            }
        }
        catch (DeskDxTimeoutError)
        {
            // No better window appeared; the previously resolved window stays in place.
            _mainWindow = current ?? _mainWindow;
        }
    }

    /// <summary>
    /// Returns the top-level window of this session's process that most likely carries the UI: the
    /// one whose subtree has the most descendants, with the process's own main window breaking ties.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Win32's <c>MainWindowHandle</c> is a heuristic and gets VB6 MDI applications wrong: it resolves
    /// to the control-less <c>ThunderRT6Main</c> container, while the form that actually carries the
    /// controls is a sibling <c>ThunderRT6Form</c>. Every locator is scoped to <see cref="MainWindow"/>,
    /// so that mistake makes the whole locator layer search an empty tree.
    /// </para>
    /// <para>
    /// Single-window applications are unaffected by construction: with one candidate the ranking has
    /// nothing to choose between, so the process's own main window is returned exactly as before.
    /// </para>
    /// </remarks>
    /// <exception cref="DeskDxTimeoutError">No top-level window with content appeared within the timeout.</exception>
    public Window UseMainWindow(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? _options.DefaultTimeout;
        _trace.Phase(
            $"waiting for main window of pid {_app.ProcessId} (timeout {effectiveTimeout.TotalMilliseconds:0}ms)");

        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var best = SelectRichestWindow();
                if (best is { } candidate)
                {
                    var window = candidate.Element.AsWindow();
                    _mainWindow = window;
                    _trace.InternalLog(
                        $"main window set to \"{window.Name}\" [{ElementDescription.Describe(window)}] "
                        + $"({candidate.Count} descendants)");
                    return window;
                }

                if (stopwatch.Elapsed >= effectiveTimeout)
                {
                    break;
                }

                Thread.Sleep(PollInterval);
            }

            throw new DeskDxTimeoutError(
                $"main window of pid {_app.ProcessId}",
                effectiveTimeout,
                _trace.ToCallLog());
        }
        finally
        {
            _trace.EndPhase();
        }
    }

    private WindowCandidate? SelectRichestWindow()
    {
        var processId = _app.ProcessId;
        var processMainWindow = TryGetMainWindow(_app, _automation);

        // With an expected title the caller decides; the ranking is only used when guessing.
        if (!string.IsNullOrWhiteSpace(_options.MainWindowTitle))
        {
            foreach (var window in ProcessWindowEnumerator.GetVisibleTopLevelWindows(processId))
            {
                if (window.Title.Contains(_options.MainWindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    var element = Resolve(window.Handle);
                    return element is null ? null : new WindowCandidate(element, CountDescendants(element));
                }
            }

            // The expected title has not appeared yet; keep polling rather than falling back to a guess.
            return null;
        }

        WindowCandidate? best = null;
        foreach (var window in ProcessWindowEnumerator.GetVisibleTopLevelWindows(processId))
        {
            var element = Resolve(window.Handle);
            if (element is null)
            {
                continue;
            }

            // Only real windows are candidates; a tooltip or IME surface is not a main window.
            ControlType controlType;
            try
            {
                controlType = element.ControlType;
            }
            catch (Exception)
            {
                continue;
            }

            if (controlType != ControlType.Window && controlType != ControlType.Pane)
            {
                continue;
            }

            var count = CountDescendants(element);
            var isProcessMain = processMainWindow is not null && SafeEquals(element, processMainWindow);

            if (best is not { } current || count > current.Count || (count == current.Count && isProcessMain))
            {
                best = new WindowCandidate(element, count);
            }
        }

        return best;
    }

    private AutomationElement? Resolve(IntPtr handle)
    {
        try
        {
            return _automation.FromHandle(handle);
        }
        catch (Exception)
        {
            // Not reachable (yet); treated as a non-candidate.
            return null;
        }
    }

    /// <summary>
    /// Counts known descendants below <paramref name="element"/>. UIA cannot report a descendant
    /// count directly, so the child tree is walked with a budget: enough to separate a populated
    /// window from an empty one, small enough to stay fast.
    /// </summary>
    private static int CountDescendants(AutomationElement element, int budget = 200)
    {
        var count = 0;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(element);

        while (queue.Count > 0 && count < budget)
        {
            AutomationElement current;
            try
            {
                current = queue.Dequeue();
            }
            catch (Exception)
            {
                continue;
            }

            AutomationElement[] children;
            try
            {
                children = current.FindAllChildren();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                count++;
                if (count >= budget)
                {
                    break;
                }

                queue.Enqueue(child);
            }
        }

        return count;
    }

    private static bool SafeEquals(AutomationElement left, AutomationElement right)
    {
        try
        {
            return left.Equals(right);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private readonly record struct WindowCandidate(AutomationElement Element, int Count);

    /// <summary>
    /// Reads the exit code of the process. Returns -1 when the code is not available (e.g. the
    /// process was killed by another party, or the process handle is already gone).
    /// </summary>
    private int GetExitCode()
    {
        try
        {
            return _app.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
