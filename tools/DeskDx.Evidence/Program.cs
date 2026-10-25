using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskDx;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/DeskDx.Evidence -- <outputDirectory> [TestTargetPath]");
    return 1;
}

var outputDirectory = Path.GetFullPath(args[0]);
return args.Length > 1
    ? RunLocatorDemo(outputDirectory, args[1])
    : RunNotepadCrashDemo(outputDirectory);

// ── Locator / gate / expect / dialog demo against TestTarget ──────────────────
static int RunLocatorDemo(string outputDirectory, string testTargetPath)
{
    KillStrayProcesses("TestTarget");

    var traceRoot = Path.Combine(Path.GetTempPath(), "DeskDx.Evidence", Guid.NewGuid().ToString("N"));
    var session = AppSession.Launch(
        Path.GetFullPath(testTargetPath),
        options: new AppSessionOptions { TraceDirectory = traceRoot });
    var sessionDirectory = session.Trace.SessionDirectory;
    Console.WriteLine($"session trace directory: {sessionDirectory}");

    try
    {
        // T1: unique match by name.
        var save = session.GetByName("Speichern").Resolve();
        Console.WriteLine($"T1 resolved: {save.ControlType} \"{save.Name}\" [AutomationId={save.AutomationId}]");

        // T3: strict mode violation — two elements share the caption "Duplikat".
        try
        {
            session.GetByName("Duplikat").Resolve();
            Console.WriteLine("T3 unexpected: strict mode did not trigger");
        }
        catch (DeskDxStrictError error)
        {
            Console.WriteLine();
            Console.WriteLine("--- T3 DeskDxStrictError.Message ---");
            Console.WriteLine(error.Message);
        }

        // T5: chaining scopes the search to the subtree of the scope element.
        var inGroupA = session.GetByName("Gruppe A").GetByName("GruppenKnopf").Resolve();
        Console.WriteLine($"T5 scoped resolve: [AutomationId={inGroupA.AutomationId}]");

        // Step 4: expect assertions. E3 shows the visible waiting phase (btnDelayed is still
        // disabled here), E4 the failure evidence, E1 the silence on success.
        Console.WriteLine();
        Console.WriteLine("--- E3 expect: waiting for the enable timer ---");
        var expectStopwatch = Stopwatch.StartNew();
        Expect.That(session.GetByAutomationId("btnDelayed")).ToBeEnabled(TimeSpan.FromSeconds(6));
        expectStopwatch.Stop();
        Console.WriteLine($"E3 ToBeEnabled() waited {expectStopwatch.ElapsedMilliseconds} ms and passed");

        Console.WriteLine();
        Console.WriteLine("--- E4 DeskDxExpectError.Message ---");
        try
        {
            Expect.That(session.GetByAutomationId("txtDisabled")).ToBeEnabled(TimeSpan.FromMilliseconds(400));
        }
        catch (DeskDxExpectError error)
        {
            Console.WriteLine(error.Message);
        }

        Console.WriteLine();
        Console.WriteLine("--- E1 expect: a successful assertion writes nothing ---");
        var callLogBefore = session.Trace.ToCallLog().Count;
        Expect.That(session.GetByAutomationId("btnSave")).ToBeVisible();
        Console.WriteLine($"call log lines before={callLogBefore} after={session.Trace.ToCallLog().Count}");

        // Step 3: the actionability gate (G2 short form: click waits for the enable timer).
        var stopwatch = Stopwatch.StartNew();
        session.GetByAutomationId("btnDelayed").Click(TimeSpan.FromSeconds(6));
        stopwatch.Stop();
        Console.WriteLine($"G2 gate waited {stopwatch.ElapsedMilliseconds} ms for the enable timer");

        // Step 3: gate timeout with the failed check lines and the UIA state dump (G3).
        Console.WriteLine();
        Console.WriteLine("--- G3 DeskDxTimeoutError.Message (gate) ---");
        try
        {
            session.GetByAutomationId("txtDisabled").Fill("geht nicht", TimeSpan.FromMilliseconds(400));
            Console.WriteLine("G3 unexpected: disabled text box accepted the fill");
        }
        catch (DeskDxTimeoutError error)
        {
            Console.WriteLine(error.Message);
        }

        // Step 3: ReceivesEvents detects the covering panel (G8).
        Console.WriteLine();
        Console.WriteLine("--- G8 DeskDxTimeoutError.Message (covered) ---");
        session.GetByAutomationId("btnCover").Click();
        try
        {
            session.GetByAutomationId("btnSave").Click(TimeSpan.FromMilliseconds(400));
            Console.WriteLine("G8 unexpected: covered button was clickable");
        }
        catch (DeskDxTimeoutError error)
        {
            Console.WriteLine(error.Message);
        }

        // Step 5: the dialog watcher. D1 (default AutoDismiss) and an excerpt of D2 (registered answer).
        Console.WriteLine();
        Console.WriteLine("--- D1 dialog watcher: AutoDismiss ---");
        TriggerDialogWithoutBlocking(session, "btnMsgBox");
        WaitForDialogLine(session, "dialog answered:", TimeSpan.FromSeconds(5));
        Console.WriteLine("D1 dialog appeared and was answered by the default policy");

        Console.WriteLine();
        Console.WriteLine("--- D2 dialog watcher: registered answer ---");
        session.Dialogs.On("Wirklich löschen", "Ja");
        TriggerDialogWithoutBlocking(session, "btnMsgBoxYesNo");
        WaitForDialogLine(session, "(registered)", TimeSpan.FromSeconds(5));
        Console.WriteLine($"D2 status after the registered answer: \"{session.GetByAutomationId("lblStatus").Resolve().Name}\"");

        Console.WriteLine();
        Console.WriteLine("--- dialog trace excerpt ---");
        foreach (var line in session.Trace.ToCallLog().Where(line => line.Contains("dialog ")))
        {
            Console.WriteLine(line);
        }

        // Step 6: the UIA snapshot diff (S4 short form: the cover panel appears and the diff fails).
        Console.WriteLine();
        Console.WriteLine("--- S4 uia snapshot diff: structural change ---");
        var snapshotOptions = new UiaSnapshotOptions
        {
            Timeout = TimeSpan.FromMilliseconds(500),
            ExcludeIds = ["lblStatus", "txtDisabled"],
        };
        var baselineName = "evidence-" + Guid.NewGuid().ToString("N");
        BaselineStore(session, baselineName, snapshotOptions);
        TriggerDialogWithoutBlocking(session, "btnCover");
        Thread.Sleep(300);
        try
        {
            session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName, snapshotOptions);
            Console.WriteLine("S4 unexpected: the snapshot matched despite the new panel");
        }
        catch (DeskDxExpectError error)
        {
            Console.WriteLine(error.Message);
        }
        finally
        {
            CleanupBaseline(baselineName);
        }

        return Finish(sessionDirectory, outputDirectory);
    }
    finally
    {
        session.Dispose();
    }
}

// ── Step 1 demo: launch, screenshot, crash detection ──
// Clean slate first: a running Notepad instance would be activated instead of started by the alias.
static int RunNotepadCrashDemo(string outputDirectory)
{
    KillStrayProcesses("notepad");

    var traceRoot = Path.Combine(Path.GetTempPath(), "DeskDx.Evidence", Guid.NewGuid().ToString("N"));
    var session = AppSession.Launch("notepad.exe", options: new AppSessionOptions { TraceDirectory = traceRoot });
    var sessionDirectory = session.Trace.SessionDirectory;
    Console.WriteLine($"session trace directory: {sessionDirectory}");

    var window = session.App.GetMainWindow(session.Automation, TimeSpan.FromSeconds(5));
    session.Trace.Phase("evidence: capturing main window");
    session.Trace.Log($"main window: {window?.Name ?? "<none>"}");

    session.Trace.Phase("waiting for locator(#TextField) (timeout 5000ms)");
    session.Trace.Screenshot(window is null ? null : (FlaUI.Core.AutomationElements.AutomationElement)window, "main-window");
    session.Trace.Log("locator resolved to the Notepad edit field");

    var processId = session.App.ProcessId;
    var process = Process.GetProcessById(processId);
    process.Kill();
    process.WaitForExit(5000);
    process.Dispose();

    var stopwatch = Stopwatch.StartNew();
    while (session.IsAlive && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
    {
        Thread.Sleep(50);
    }

    session.Trace.Phase("evidence: crash detection");
    try
    {
        session.EnsureAlive();
        session.Trace.Log("unexpected: EnsureAlive did not throw");
    }
    catch (DeskDxCrashError error)
    {
        session.Trace.Log($"caught {nameof(DeskDxCrashError)}");
        Console.WriteLine();
        Console.WriteLine("--- DeskDxCrashError.Message ---");
        Console.WriteLine(error.Message);
    }

    session.Dispose();
    return Finish(sessionDirectory, outputDirectory);
}

// Writes a snapshot baseline by hand: the evidence tool lives outside the test assembly, so it
// cannot reach the internal SnapshotStore. Same layout: <cwd>/snapshots/<name>.uia.txt.
static void BaselineStore(AppSession session, string baselineName, UiaSnapshotOptions options)
{
    var directory = Path.Combine(Environment.CurrentDirectory, "snapshots");
    Directory.CreateDirectory(directory);
    File.WriteAllText(
        Path.Combine(directory, baselineName + ".uia.txt"),
        session.GetByName("DeskDx TestTarget").ToUiaSnapshot(options));
}

static void CleanupBaseline(string baselineName)
{
    var directory = Path.Combine(Environment.CurrentDirectory, "snapshots");
    File.Delete(Path.Combine(directory, baselineName + ".uia.txt"));
    File.Delete(Path.Combine(directory, "__mismatches__", baselineName + ".actual.txt"));
    File.Delete(Path.Combine(directory, "__mismatches__", baselineName + ".diff"));
}

// Posts BM_CLICK so the call returns immediately: InvokePattern.Invoke blocks while the modal
// dialog is open, and a mouse click needs the application to own the foreground window.
static void TriggerDialogWithoutBlocking(AppSession session, string automationId)
{
    var handle = session.GetByAutomationId(automationId).Resolve().Properties.NativeWindowHandle.ValueOrDefault;
    NativeMethods.PostMessage(handle, NativeMethods.ButtonClickMessage, IntPtr.Zero, IntPtr.Zero);
}

static void WaitForDialogLine(AppSession session, string marker, TimeSpan timeout)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (session.Trace.ToCallLog().Any(line => line.Contains(marker)))
        {
            return;
        }

        Thread.Sleep(100);
    }

    Console.WriteLine($"warning: no trace line containing \"{marker}\" within {timeout.TotalSeconds}s");
}

static void KillStrayProcesses(string processName)
{
    foreach (var stray in Process.GetProcessesByName(processName))
    {
        try
        {
            if (!stray.HasExited)
            {
                stray.Kill();
                stray.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
        finally
        {
            stray.Dispose();
        }
    }
}

static int Finish(string sessionDirectory, string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    var target = Path.Combine(outputDirectory, Path.GetFileName(sessionDirectory));
    if (Directory.Exists(target))
    {
        Directory.Delete(target, recursive: true);
    }

    CopyDirectory(sessionDirectory, target);
    Console.WriteLine($"trace copied to: {target}");
    Console.WriteLine($"trace.jsonl lines: {File.ReadAllLines(Path.Combine(target, "trace.jsonl")).Length}");

    var shotsDirectory = Path.Combine(target, "shots");
    if (Directory.Exists(shotsDirectory))
    {
        foreach (var file in Directory.GetFiles(shotsDirectory))
        {
            Console.WriteLine($"shot: {Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)");
        }
    }

    return 0;
}

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

static class NativeMethods
{
    internal const uint ButtonClickMessage = 0x00F5; // BM_CLICK

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
