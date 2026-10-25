using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core.Definitions;
using DeskDx;

// DeskDx dogfood run — MVP exit criterion 2: a real characterization run of a VB6 application
// through the wrapper. Diagnostics only: this program must not add DeskDx APIs. Anything missing
// becomes a backlog entry in the report.
//
// Usage:
//   dotnet run --project tools/DeskDx.Dogfood -- <exePath> <outputDirectory> [clickControlName]

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "usage: dotnet run --project tools/DeskDx.Dogfood -- <exePath> <outputDirectory> [clickControlName]");
    return 1;
}

var exePath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var explicitClickTarget = args.Length > 2 ? args[2] : null;

const string applicationName = "PhotoDemon";
const string baselineName = "photodemon-main";
const string updateEnvironmentVariable = "DESKDX_SNAPSHOT_UPDATE";

if (!File.Exists(exePath))
{
    Console.Error.WriteLine($"executable not found: {exePath}");
    return 1;
}

Directory.CreateDirectory(outputDirectory);
var baselinePath = Path.Combine(Environment.CurrentDirectory, "snapshots", baselineName + ".uia.txt");
var report = new RunReport(exePath, outputDirectory, baselinePath);

Console.WriteLine("=== DeskDx dogfood: PhotoDemon ===");
Console.WriteLine($"exe:      {exePath}");
Console.WriteLine($"output:   {outputDirectory}");
Console.WriteLine($"baseline: {baselinePath}");

// A second instance of a single-instance app would just be activated, which would invalidate the
// whole run — report it instead of silently producing a baseline for the wrong window tree.
var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
if (running.Length > 0)
{
    foreach (var process in running)
    {
        process.Dispose();
    }

    report.Warnings.Add(
        $"{running.Length} instance(s) of {applicationName} were already running before the launch; "
        + "a single-instance app may have been activated instead of started.");
    Console.WriteLine($"WARNING: {running.Length} {applicationName} instance(s) already running");
}

// The baseline is never created silently: only a run that finds the baseline missing sets the flag.
var baselineExisted = File.Exists(baselinePath);
if (!baselineExisted)
{
    Environment.SetEnvironmentVariable(updateEnvironmentVariable, "1");
    report.BaselineCreated = true;
    Console.WriteLine($"baseline missing -> creating it ({updateEnvironmentVariable}=1)");
}
else
{
    Environment.SetEnvironmentVariable(updateEnvironmentVariable, null);
    Console.WriteLine("baseline present -> matching against it");
}

var traceRoot = Path.Combine(Path.GetTempPath(), "DeskDx.Dogfood", Guid.NewGuid().ToString("N"));
var session = AppSession.Launch(
    exePath,
    startupTimeout: TimeSpan.FromSeconds(30),
    options: new AppSessionOptions
    {
        TraceDirectory = traceRoot,
        // Diagnostics wants the dialog evidence even when it is answered automatically.
        DialogPollInterval = TimeSpan.FromMilliseconds(250),
    });

report.TraceDirectory = session.Trace.SessionDirectory;
Console.WriteLine($"launched (pid {session.App.ProcessId}), trace: {report.TraceDirectory}");

try
{
    // ── 2. Stabilization: the splash screen closes itself, but it is the first window the session
    //       may have captured. Wait for a titled window that stays put for 3 s.
    Console.WriteLine();
    Console.WriteLine("--- stabilization (waiting for a stable main window) ---");
    var stableWindow = WaitForStableMainWindow(session, applicationName, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3), report);
    if (stableWindow is null)
    {
        report.Backlog.Add(
            "No window titled *PhotoDemon* stayed stable for 3 s within 20 s. Check the splash screen "
            + "handling and the startup time on a cold first run.");
        Console.WriteLine("FAILED: no stable main window found");
        return Finish(session, report, outputDirectory, baselinePath, exitCode: 2);
    }

    report.StableWindowTitle = stableWindow.Value.Title;
    report.StableWindowHandle = stableWindow.Value.Handle;
    report.StableWindowClass = stableWindow.Value.ClassName;
    Console.WriteLine($"stable window: \"{stableWindow.Value.Title}\" class={stableWindow.Value.ClassName} (hwnd {stableWindow.Value.Handle})");

    // Every visible titled window of the process, with the element count UIA sees below it. This is
    // the evidence for the central problem of this run: several windows belong to the process, and
    // the session's main-window resolution picks one that does not carry the real control tree.
    Console.WriteLine();
    Console.WriteLine("--- window inventory ---");
    foreach (var window in GetVisibleWindowTitles(session.App.ProcessId).OrderBy(w => w.Title))
    {
        string info;
        try
        {
            var element = session.Automation.FromHandle(window.Handle);
            var descendants = element.FindAllDescendants();
            var named = descendants.Count(d =>
            {
                try { return !string.IsNullOrWhiteSpace(d.Name); } catch { return false; }
            });
            info = $"descendants={descendants.Length}, named={named}, root={element.ControlType} \"{element.Name}\"";
        }
        catch (Exception ex)
        {
            info = $"uia failed: {ex.GetType().Name}";
        }

        Console.WriteLine($"  \"{window.Title}\" class={window.ClassName} hwnd={window.Handle} -> {info}");
    }

    // The session caches its main window on first access; touching it only now means it resolves to
    // the window that is actually up after the splash is gone.
    var mainWindow = session.MainWindow;
    report.MainWindowTitle = mainWindow?.Name ?? "<none>";
    var mainWindowMatches = mainWindow is not null
                            && string.Equals(mainWindow.Name, stableWindow.Value.Title, StringComparison.Ordinal);
    report.MainWindowMatchesStableWindow = mainWindowMatches;
    Console.WriteLine($"session.MainWindow: \"{report.MainWindowTitle}\" (matches stable: {mainWindowMatches})");
    if (!mainWindowMatches)
    {
        report.Backlog.Add(
            "session.MainWindow did not match the stabilized top-level window. This is the predicted "
            + "splash-screen hazard: the property is resolved from MainWindowHandle and can point at a "
            + "window that has since closed. A public way to scope a locator to a chosen window is missing.");
    }

    // ── 3. First baseline (or match against an existing one).
    Console.WriteLine();
    Console.WriteLine("--- baseline ---");
    var snapshotOptions = new UiaSnapshotOptions { Timeout = TimeSpan.FromSeconds(10) };
    string? baselineText = null;

    // A failed creation must not leave a baseline behind. A baseline is the artifact the whole
    // characterization rests on, so a wrong one is worse than none: it would be committed and later
    // compared against, hiding the fact that it was never a good reference. Creation succeeds only
    // when the snapshot has content.
    var baselineExistedBefore = File.Exists(baselinePath);
    try
    {
        // Locators resolve through session.MainWindow, so the baseline covers the window the session
        // considers main — for this application the real VB6 form.
        session.GetByName(stableWindow.Value.Title).ToMatchUiaSnapshot(baselineName, snapshotOptions);
        report.BaselineMatched = true;
        Console.WriteLine(report.BaselineCreated ? "baseline created" : "baseline matched");
    }
    catch (DeskDxExpectError error)
    {
        report.BaselineMatched = false;
        report.Errors.Add("baseline: " + FirstLines(error.Message, 6));
        Console.WriteLine("baseline FAILED:");
        Console.WriteLine(FirstLines(error.Message, 25));

        // Keep the diff lines themselves. The call log has the shape
        //   phase, "baseline: <path>", "actual: <path>", then the unified-diff lines.
        // Taking the first N lines of the message would truncate the diff, which is the part that
        // actually explains the failure.
        var callLog = error.CallLog.ToList();
        var actualIndex = callLog.FindIndex(line => line.Contains("actual:"));
        report.BaselineDiff = actualIndex >= 0 ? callLog.Skip(actualIndex + 1).ToList() : callLog;
        report.BaselineDiffPath = Path.Combine(
            Path.GetDirectoryName(baselinePath)!,
            "__mismatches__",
            baselineName + ".diff");

        Console.WriteLine($"--- differing lines ({report.BaselineDiff.Count}) ---");
        foreach (var line in report.BaselineDiff)
        {
            Console.WriteLine(line);
        }
    }
    catch (Exception ex)
    {
        // A UIA failure against a busy VB6 message pump must not abort the whole diagnostic run.
        report.Errors.Add($"baseline (uia): {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"baseline unavailable (UIA): {ex.GetType().Name}: {ex.Message}");
    }

    // Guard the create path: discard a baseline that was just written but carries no content.
    if (!baselineExistedBefore && File.Exists(baselinePath))
    {
        var written = File.ReadAllText(baselinePath);
        var writtenLines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        if (writtenLines == 0)
        {
            File.Delete(baselinePath);
            report.BaselineMatched = false;
            report.BaselineDiscarded = true;
            report.Errors.Add("baseline discarded: the newly written baseline was empty");
            Console.WriteLine("baseline discarded: the newly written baseline was empty");
        }
    }

    // ── 3b. Structural capture via the library.
    // The renderer is public, so the dogfood no longer re-implements the snapshot format. This call
    // also covers a case a locator cannot: it snapshots the window the stability filter selected, by
    // handle, regardless of which window the session treats as its main window.
    Console.WriteLine();
    Console.WriteLine("--- library snapshot renderer (element addressed) ---");
    string? escapedSnapshot = null;
    try
    {
        escapedSnapshot = session.ToUiaSnapshot(stableWindow.Value.Handle, new UiaSnapshotOptions
        {
            ExcludeIds = ["lblStatus"],
        });
        SnapshotStats(escapedSnapshot, report);
        Console.WriteLine($"captured {report.BaselineLineCount} lines "
                          + $"({report.EmptyAutomationIdLines} without AutomationId)");
        foreach (var pair in report.BaselineControlTypes)
        {
            Console.WriteLine($"    {pair.Key,-12} {pair.Value}");
        }

        var librarySnapshot = File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : null;
        if (librarySnapshot is not null)
        {
            // The baseline carries volatile popups, so a byte comparison is not meaningful; compare
            // the tree shape instead and report the difference.
            var baselineTree = ParseSnapshot(librarySnapshot).Select(e => e.ControlType).ToList();
            var crossCheckTree = ParseSnapshot(escapedSnapshot).Select(e => e.ControlType).ToList();
            var sameShape = baselineTree.Count == crossCheckTree.Count
                            && baselineTree.SequenceEqual(crossCheckTree);
            report.EscapeHatchMatchesLibrary = sameShape;
            Console.WriteLine($"library baseline and renderer cross-check have the same tree shape: {sameShape}");
        }
    }
    catch (Exception ex)
    {
        report.Errors.Add($"renderer: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"renderer capture failed: {ex.GetType().Name}: {ex.Message}");
    }

    // Baseline analysis — the AutomationId question is the core finding.
    baselineText = File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : null;
    if (baselineText is not null)
    {
        SnapshotStats(baselineText, report);
        Console.WriteLine($"baseline lines: {report.BaselineLineCount}, "
                          + $"empty AutomationIds: {report.EmptyAutomationIdLines}/{report.BaselineLineCount}");
    }
    else
    {
        report.Note("No baseline file was written, so no snapshot statistics are available.");
    }

    // ── 4. VB6Locator on a real control: which fallback stage matches?
    Console.WriteLine();
    Console.WriteLine("--- VB6Locator fallback chain ---");
    // Prefer an explicit name, else a client-area control from the snapshot (never a caption button).
    var target = PickTarget(baselineText, explicitClickTarget, report);

    if (target is null)
    {
        report.Note(
            "No named control could be read from the captured tree, so the VB6Locator stage could not be "
            + "exercised. Pass a control name as the third argument.");
        Console.WriteLine("no target control found in the captured tree");
    }
    else
    {
        report.Vb6Target = target.Value.Name;
        Console.WriteLine($"target control: {target.Value.Type} \"{target.Value.Name}\" (source: {report.TargetSource})");
        try
        {
            var element = VB6Locator.Find(session, target.Value.Type, target.Value.Name, TimeSpan.FromSeconds(5));
            report.Vb6Resolved = true;
            report.Vb6ResolvedDescription = $"{element.ControlType} \"{element.Name}\"";
            Console.WriteLine($"resolved: {report.Vb6ResolvedDescription}");

            // ── 5. One gate click on a control that is not obviously destructive.
            if (!IsRisky(target.Value.Name))
            {
                Console.WriteLine("--- gate click ---");
                session.GetByName(target.Value.Name).Click(TimeSpan.FromSeconds(5));
                report.GateClickPerformed = true;
                Console.WriteLine("click returned");
            }
            else
            {
                report.Note(
                    $"The only candidate control (\"{target.Value.Name}\") looks destructive, so the gate "
                    + "click was skipped to keep the run repeatable. Pass a safe control name as the third argument.");
                Console.WriteLine($"gate click skipped: \"{target.Value.Name}\" looks destructive");
            }
        }
        catch (Exception ex) when (ex is DeskDxTimeoutError or DeskDxStrictError or DeskDxExpectError)
        {
            report.Errors.Add("vb6 locator: " + FirstLines(ex.Message, 6));
            Console.WriteLine("VB6Locator FAILED:");
            Console.WriteLine(FirstLines(ex.Message, 20));
        }
    }

    // ── 6. One expectation against the real application.
    Console.WriteLine();
    Console.WriteLine("--- expect ---");
    try
    {
        Expect.That(session.GetByName(stableWindow.Value.Title)).ToBeVisible(TimeSpan.FromSeconds(5));
        report.ExpectPassed = true;
        Console.WriteLine("ToBeVisible(main window): OK");
    }
    catch (DeskDxExpectError error)
    {
        report.Errors.Add("expect: " + FirstLines(error.Message, 6));
        Console.WriteLine("ToBeVisible FAILED:");
        Console.WriteLine(FirstLines(error.Message, 20));
    }

    // Live state is worth recording: it is what the session saw at the end of the run.
    report.FinalWindowTitles = GetWindowTitles(session.App.ProcessId).ToList();
    return Finish(session, report, outputDirectory, baselinePath, exitCode: 0);
}
catch (Exception ex)
{
    report.Errors.Add($"unhandled: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine($"UNHANDLED: {ex}");
    return Finish(session, report, outputDirectory, baselinePath, exitCode: 3);
}

// Polls for the stable VB6 main window of the process.
//
// The splash filter cannot be "title contains PhotoDemon": this application shows a second visible
// window (class ThunderRT6Main, title "PhotoDemon") that is stable from the first 200 ms, while the
// real editor window is a ThunderRT6Form whose title starts as the vendor string and is only
// retitled to "PhotoDemon 2025.12" about a second in. Selecting on title alone picks the wrong
// window. The class name separates them: every VB6 form — splash, main window, palette — is a
// ThunderRT6Form, and ThunderRT6Main is not a form at all.
static (IntPtr Handle, string Title, string ClassName)? WaitForStableMainWindow(
    AppSession session,
    string applicationName,
    TimeSpan timeout,
    TimeSpan stableFor,
    RunReport report)
{
    var processId = session.App.ProcessId;
    var stopwatch = Stopwatch.StartNew();
    (IntPtr Handle, string Title, string ClassName)? candidate = null;
    var candidateSince = TimeSpan.Zero;

    while (stopwatch.Elapsed < timeout)
    {
        if (!session.IsAlive)
        {
            report.Backlog.Add("The application exited during stabilization.");
            return null;
        }

        var windows = GetVisibleWindowTitles(processId);

        // Primary signal: a VB6 form whose title names the application.
        var match = windows.FirstOrDefault(w =>
            w.ClassName.StartsWith("ThunderRT6Form", StringComparison.Ordinal)
            && w.Title.Contains(applicationName, StringComparison.OrdinalIgnoreCase));
        if (match.Title is null)
        {
            // Fallback for non-VB6 targets: any visible window whose title names the application.
            match = windows.FirstOrDefault(w =>
                w.Title.Contains(applicationName, StringComparison.OrdinalIgnoreCase));
        }

        if (match.Title is null)
        {
            candidate = null;
            candidateSince = TimeSpan.Zero;
        }
        else if (candidate is { } current && current.Handle == match.Handle && current.Title == match.Title)
        {
            // Identity requires BOTH handle and title to be unchanged. The splash-to-main transition
            // reuses the same HWND and only changes the title, so a handle-only check would accept
            // the splash as stable.
            if (stopwatch.Elapsed - candidateSince >= stableFor)
            {
                return candidate;
            }
        }
        else
        {
            candidate = match;
            candidateSince = stopwatch.Elapsed;
            var description = $"\"{match.Title}\" ({match.ClassName})";
            if (!report.AllSeenWindows.Contains(description))
            {
                report.AllSeenWindows.Add(description);
            }

            Console.WriteLine(
                $"  candidate: \"{match.Title}\" class={match.ClassName} hwnd={match.Handle} at {stopwatch.ElapsedMilliseconds} ms");
        }

        Thread.Sleep(250);
    }

    report.Backlog.Add(
        $"Stabilization timed out after {timeout.TotalSeconds:0}s. Windows seen: "
        + string.Join(", ", report.AllSeenWindows.Distinct()));
    return null;
}

static (IntPtr Handle, string Title, string ClassName)[] GetVisibleWindowTitles(int processId)
{
    var found = new List<(IntPtr, string, string)>();
    try
    {
        NativeMethods.EnumWindows((handle, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(handle))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(handle, out var owner);
                if (owner != (uint)processId)
                {
                    return true;
                }

                var titleBuffer = new StringBuilder(512);
                NativeMethods.GetWindowText(handle, titleBuffer, titleBuffer.Capacity);
                var classBuffer = new StringBuilder(256);
                NativeMethods.GetClassName(handle, classBuffer, classBuffer.Capacity);
                if (titleBuffer.Length > 0)
                {
                    found.Add((handle, titleBuffer.ToString(), classBuffer.ToString()));
                }
            }
            catch (Exception)
            {
                // A window that vanished mid-enumeration is skipped.
            }

            return true;
        }, IntPtr.Zero);
    }
    catch (Exception)
    {
        // Report whatever was collected.
    }

    return found.ToArray();
}

static IEnumerable<string> GetWindowTitles(int processId)
    => GetVisibleWindowTitles(processId).Select(w => w.Title);

// Reads a usable control out of the snapshot so the run needs no hard-coded names.
//
// The automatic pick deliberately ignores everything inside a TitleBar subtree: the only "button"
// names a VB6 window exposes there are the Win32 caption buttons (Minimieren/Maximieren/Schließen),
// which are not VB6 controls and clicking them would minimise or close the application. A client-area
// Button is preferred; failing that a MenuItem, which is also genuine VB6 UI. The control *type*
// travels with the name, because VB6Locator matches on name plus type and asking for the wrong type
// can never succeed. A name passed on the command line always wins.
static (ControlType Type, string Name)? PickTarget(string? baselineText, string? explicitTarget, RunReport report)
{
    if (!string.IsNullOrWhiteSpace(explicitTarget))
    {
        report.TargetSource = "command line";
        return (ControlType.Button, explicitTarget);
    }

    if (baselineText is null)
    {
        report.TargetSource = "none (no snapshot)";
        return null;
    }

    var entries = ParseSnapshot(baselineText);
    var eligible = entries
        .Where(entry => !entry.InTitleBar)
        .Where(entry => entry.ControlType is "Button" or "MenuItem")
        .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
        .ToList();

    report.BaselineButtonNames = eligible.Select(entry => $"{entry.ControlType} \"{entry.Name}\"").ToList();
    report.TitleBarCandidates = entries
        .Where(entry => entry.InTitleBar && !string.IsNullOrWhiteSpace(entry.Name))
        .Select(entry => entry.Name)
        .ToList();

    // Prefer a client-area Button (closest to the original intent), then any client-area control.
    var safe = eligible.FirstOrDefault(entry => !IsRisky(entry.Name) && entry.ControlType == "Button");
    if (safe.Name is null)
    {
        safe = eligible.FirstOrDefault(entry => !IsRisky(entry.Name));
    }

    if (safe.Name is null)
    {
        // No safe client-area control: use the first eligible one but let the caller skip the click.
        safe = eligible.FirstOrDefault();
    }

    if (safe.Name is null)
    {
        report.TargetSource = "none (no client-area control in the snapshot)";
        report.Note(
            "The snapshot contained no named client-area Button or MenuItem outside the title bar. "
            + "VB6 windows expose only Win32 caption buttons by name, so a target must be supplied as the "
            + "third argument to exercise VB6Locator.");
        return null;
    }

    var type = safe.ControlType == "MenuItem" ? ControlType.MenuItem : ControlType.Button;
    report.TargetSource = $"automatic (client-area {safe.ControlType.ToLowerInvariant()})";
    return (type, safe.Name);
}

// A conservative guard: the dogfood run must stay repeatable, so obviously destructive commands are
// never clicked. This is a diagnostic heuristic, not a policy engine.
static bool IsRisky(string name)
{
    string[] risky =
    [
        "exit", "quit", "close", "schließen", "schliessen", "abort", "cancel", "abbrechen",
        "delete", "löschen", "loeschen", "remove", "save", "speichern", "new", "neu",
        "open", "öffnen", "oeffnen", "shutdown", "reset", "undo", "overwrite",
    ];

    return risky.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
}

static List<SnapshotEntry> ParseSnapshot(string baselineText)
{
    var result = new List<SnapshotEntry>();
    var pattern = new Regex("^(?<indent> *)(?<type>[^\\s]+) \"(?<name>.*?)\" \\[AutomationId=(?<id>.*?)\\]$");
    var titleBarDepth = -1;
    var stack = new List<string>();

    foreach (var line in baselineText.Replace("\r\n", "\n").Split('\n'))
    {
        var match = pattern.Match(line.TrimEnd());
        if (!match.Success)
        {
            continue;
        }

        var depth = match.Groups["indent"].Value.Length / 2;
        var controlType = match.Groups["type"].Value;

        // Track ancestor types so a title-bar subtree can be recognised without a full parser.
        stack.RemoveRange(Math.Min(depth, stack.Count), Math.Max(0, stack.Count - depth));
        var inTitleBar = titleBarDepth >= 0 && depth > titleBarDepth;
        if (controlType == "TitleBar")
        {
            titleBarDepth = depth;
            inTitleBar = true;
        }
        else if (titleBarDepth >= 0 && depth <= titleBarDepth)
        {
            titleBarDepth = -1;
        }

        while (stack.Count <= depth)
        {
            stack.Add(string.Empty);
        }

        stack[depth] = controlType;

        result.Add(new SnapshotEntry(
            controlType,
            match.Groups["name"].Value,
            match.Groups["id"].Value,
            inTitleBar));
    }

    return result;
}

static void SnapshotStats(string baselineText, RunReport report)
{
    var parsed = ParseSnapshot(baselineText);
    report.BaselineLineCount = parsed.Count;
    report.EmptyAutomationIdLines = parsed.Count(line => line.AutomationId.Length == 0);
    report.BaselineControlTypes = parsed
        .GroupBy(line => line.ControlType)
        .OrderByDescending(group => group.Count())
        .ToDictionary(group => group.Key, group => group.Count());
}

static string FirstLines(string text, int count)
    => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Take(count));

// Stops the session, copies the artifacts and writes the summary. Always runs, also on failure.
static int Finish(AppSession session, RunReport report, string outputDirectory, string baselinePath, int exitCode)
{
    Console.WriteLine();
    Console.WriteLine("--- teardown ---");
    try
    {
        session.Dispose();
        Console.WriteLine("session disposed");
    }
    catch (Exception ex)
    {
        report.Errors.Add($"dispose: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"dispose threw: {ex.Message}");
    }

    // The trace is the primary artifact: the call log is the readable evidence document.
    if (report.TraceDirectory is not null && Directory.Exists(report.TraceDirectory))
    {
        var target = Path.Combine(outputDirectory, "trace", Path.GetFileName(report.TraceDirectory));
        CopyDirectory(report.TraceDirectory, target);
        report.TraceCopy = target;
        Console.WriteLine($"trace copied to: {target}");
    }

    if (File.Exists(baselinePath))
    {
        var target = Path.Combine(outputDirectory, Path.GetFileName(baselinePath));
        File.Copy(baselinePath, target, overwrite: true);
        report.BaselineCopy = target;
    }

    // Mine the trace for the two things the report is actually about.
    var traceLines = report.TraceDirectory is not null && File.Exists(Path.Combine(report.TraceDirectory, "trace.jsonl"))
        ? File.ReadAllLines(Path.Combine(report.TraceDirectory, "trace.jsonl"))
        : [];
    report.Vb6Stages = traceLines
        .Select(line => Regex.Match(line, "vb6 locator stage (?<stage>\\d+) matched"))
        .Where(match => match.Success)
        .Select(match => int.Parse(match.Groups["stage"].Value))
        .ToList();
    report.DialogLines = traceLines
        .Where(line => line.Contains("dialog appeared") || line.Contains("dialog answered"))
        .Select(line => Regex.Match(line, "\"message\":\"(?<message>.*?)\""))
        .Where(match => match.Success)
        .Select(match => Unescape(match.Groups["message"].Value))
        .ToList();

    var summaryPath = Path.Combine(outputDirectory, "dogfood-report.md");
    File.WriteAllText(summaryPath, report.ToMarkdown());
    report.SummaryPath = summaryPath;

    Console.WriteLine();
    Console.WriteLine("=== summary ===");
    Console.WriteLine($"stable window:        {report.StableWindowTitle ?? "<none>"} ({report.StableWindowClass ?? "?"})");
    Console.WriteLine($"session.MainWindow:   {report.MainWindowTitle ?? "<none>"} (matches: {report.MainWindowMatchesStableWindow})");
    Console.WriteLine($"baseline created:     {report.BaselineCreated}");
    Console.WriteLine($"baseline matched:     {report.BaselineMatched}");
    if (report.BaselineDiscarded)
    {
        Console.WriteLine("baseline discarded:   the newly written baseline was empty");
    }
    Console.WriteLine($"baseline lines:       {report.BaselineLineCount}");
    Console.WriteLine($"empty AutomationIds:  {report.EmptyAutomationIdLines}");
    Console.WriteLine($"VB6 target:           {report.Vb6Target ?? "<none>"} ({report.TargetSource ?? "-"})");
    Console.WriteLine($"VB6 resolved:         {report.Vb6Resolved} ({report.Vb6ResolvedDescription ?? "-"})");
    Console.WriteLine($"VB6 stages matched:   {(report.Vb6Stages.Count == 0 ? "<none>" : string.Join(", ", report.Vb6Stages))}");
    Console.WriteLine($"gate click:           {report.GateClickPerformed}");
    Console.WriteLine($"expect passed:        {report.ExpectPassed}");
    Console.WriteLine($"dialogs:              {report.DialogLines.Count}");
    foreach (var dialog in report.DialogLines)
    {
        Console.WriteLine($"  {dialog}");
    }

    Console.WriteLine($"windows at end:       {string.Join(", ", report.FinalWindowTitles)}");
    if (report.Errors.Count > 0)
    {
        Console.WriteLine("errors:");
        foreach (var error in report.Errors)
        {
            Console.WriteLine($"  {FirstLines(error, 1)}");
        }
    }

    if (report.Backlog.Count > 0)
    {
        Console.WriteLine("backlog:");
        foreach (var item in report.Backlog)
        {
            Console.WriteLine($"  {FirstLines(item, 1)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"report: {summaryPath}");
    Console.WriteLine(exitCode == 0
        ? "=== dogfood run completed ==="
        : $"=== dogfood run ended with exit code {exitCode} ===");
    return exitCode;
}

static string Unescape(string value)
    => value.Replace("\\u0022", "\"").Replace("\\\"", "\"").Replace("\\u2014", "—").Replace("\\n", " ");

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.GetDirectories(source))
    {
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    foreach (var file in Directory.GetFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }
}

/// <summary>A named element of a snapshot, with the flag the target picker needs.</summary>
internal readonly record struct SnapshotEntry(
    string ControlType,
    string Name,
    string AutomationId,
    bool InTitleBar);

internal sealed class RunReport(string exePath, string outputDirectory, string baselinePath)
{
    internal string ExePath => exePath;

    internal string OutputDirectory => outputDirectory;

    internal string BaselinePath => baselinePath;

    internal string? TraceDirectory { get; set; }

    internal string? TraceCopy { get; set; }

    internal string? BaselineCopy { get; set; }

    internal string? EscapeHatchSnapshotPath { get; set; }

    internal string? BaselineWrittenBy { get; set; }

    internal int ReachableLineCount { get; set; }

    internal bool EscapeHatchMatchesLibrary { get; set; }

    internal string? SummaryPath { get; set; }

    internal bool BaselineCreated { get; set; }

    internal bool BaselineMatched { get; set; }

    internal bool BaselineDiscarded { get; set; }

    internal List<string> BaselineDiff { get; set; } = [];

    internal string? BaselineDiffPath { get; set; }

    internal string? StableWindowTitle { get; set; }

    internal IntPtr StableWindowHandle { get; set; }

    internal string? StableWindowClass { get; set; }

    internal string? MainWindowTitle { get; set; }

    internal bool MainWindowMatchesStableWindow { get; set; }

    internal string? Vb6Target { get; set; }

    internal bool Vb6Resolved { get; set; }

    internal string? Vb6ResolvedDescription { get; set; }

    internal bool GateClickPerformed { get; set; }

    internal bool ExpectPassed { get; set; }

    internal int BaselineLineCount { get; set; }

    internal int EmptyAutomationIdLines { get; set; }

    internal Dictionary<string, int> BaselineControlTypes { get; set; } = [];

    internal List<string> BaselineButtonNames { get; set; } = [];

    internal string? TargetSource { get; set; }

    internal List<string> TitleBarCandidates { get; set; } = [];

    internal List<int> Vb6Stages { get; set; } = [];

    internal List<string> DialogLines { get; set; } = [];

    internal List<string> AllSeenWindows { get; } = [];

    internal List<string> FinalWindowTitles { get; set; } = [];

    internal List<string> Warnings { get; } = [];

    internal List<string> Errors { get; } = [];

    internal List<string> Backlog { get; } = [];

    /// <summary>Adds a backlog entry at most once (repeated observations would spam the report).</summary>
    internal void Note(string item)
    {
        if (!Backlog.Contains(item))
        {
            Backlog.Add(item);
        }
    }

    internal string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DeskDx Dogfood run — PhotoDemon");
        builder.AppendLine();
        builder.AppendLine("> Generated by `tools/DeskDx.Dogfood`. Raw evidence, not the curated report —"
                           + " the analysis lives in `docs/dogfood-report.md`.");
        builder.AppendLine();
        builder.AppendLine($"- exe: `{ExePath}`");
        builder.AppendLine($"- output: `{OutputDirectory}`");
        builder.AppendLine($"- baseline: `{BaselinePath}`");
        builder.AppendLine();

        builder.AppendLine("## Result");
        builder.AppendLine();
        builder.AppendLine("| Item | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| stable main window | {(StableWindowTitle is null ? "<none>" : $"\"{StableWindowTitle}\" ({StableWindowClass})")} |");
        builder.AppendLine($"| `session.MainWindow` | {(MainWindowTitle is null ? "<none>" : $"\"{MainWindowTitle}\"")} |");
        builder.AppendLine($"| MainWindow matched stable window | {MainWindowMatchesStableWindow} |");
        builder.AppendLine($"| baseline created | {BaselineCreated} |");
        builder.AppendLine($"| baseline matched | {BaselineMatched} |");
        builder.AppendLine($"| baseline discarded (empty) | {BaselineDiscarded} |");
        builder.AppendLine($"| baseline lines | {BaselineLineCount} |");
        builder.AppendLine($"| lines with empty AutomationId | {EmptyAutomationIdLines} |");
        builder.AppendLine($"| renderer cross-check matches baseline shape | {EscapeHatchMatchesLibrary} |");
        builder.AppendLine($"| VB6 target | {(Vb6Target is null ? "<none>" : $"\"{Vb6Target}\"")} |");
        builder.AppendLine($"| VB6 target source | {TargetSource ?? "<none>"} |");
        builder.AppendLine($"| VB6 resolved | {Vb6Resolved} |");
        builder.AppendLine($"| VB6 fallback stages matched | {(Vb6Stages.Count == 0 ? "<none>" : string.Join(", ", Vb6Stages))} |");
        builder.AppendLine($"| gate click performed | {GateClickPerformed} |");
        builder.AppendLine($"| expect passed | {ExpectPassed} |");
        builder.AppendLine();

        builder.AppendLine("## Control types in the baseline");
        builder.AppendLine();
        if (BaselineControlTypes.Count > 0)
        {
            builder.AppendLine("| ControlType | Count |");
            builder.AppendLine("|---|---|");
            foreach (var pair in BaselineControlTypes)
            {
                builder.AppendLine($"| {pair.Key} | {pair.Value} |");
            }
        }
        else
        {
            builder.AppendLine("_no baseline available_");
        }

        builder.AppendLine();
        builder.AppendLine("## Baseline diff");
        builder.AppendLine();
        if (BaselineDiff.Count == 0)
        {
            builder.AppendLine("_no diff (baseline matched)_");
        }
        else
        {
            builder.AppendLine($"diff file: `{BaselineDiffPath}`");
            builder.AppendLine();
            builder.AppendLine("```diff");
            foreach (var line in BaselineDiff)
            {
                builder.AppendLine(line);
            }

            builder.AppendLine("```");
        }

        builder.AppendLine();
        builder.AppendLine("## Dialog log");
        builder.AppendLine();
        if (DialogLines.Count > 0)
        {
            foreach (var line in DialogLines)
            {
                builder.AppendLine($"- {line}");
            }
        }
        else
        {
            builder.AppendLine("_no dialogs_");
        }

        builder.AppendLine();
        builder.AppendLine("## Windows observed");
        builder.AppendLine();
        foreach (var window in AllSeenWindows.Distinct())
        {
            builder.AppendLine($"- \"{window}\"");
        }

        builder.AppendLine();
        builder.AppendLine("## Warnings");
        builder.AppendLine();
        AppendList(builder, Warnings);

        builder.AppendLine();
        builder.AppendLine("## Errors");
        builder.AppendLine();
        AppendList(builder, Errors);

        builder.AppendLine();
        builder.AppendLine("## Backlog candidates");
        builder.AppendLine();
        AppendList(builder, Backlog);

        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, List<string> items)
    {
        if (items.Count == 0)
        {
            builder.AppendLine("_none_");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- {item.Replace("\n", " ")}");
        }
    }
}

internal static partial class NativeMethods
{
    internal delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr handle);
}





