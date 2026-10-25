using System.Diagnostics;

namespace UiDx.Tests;

/// <summary>
/// Schritt 1 acceptance tests: N1–N3 against Notepad plus the trace/error format checks the spec
/// demands. Runs in the "Notepad" collection so stray instances are cleaned up around the run.
/// </summary>
[Collection("Notepad")]
public sealed class AppSessionNotepadTests
{
    private const string ProcessName = "notepad";

    private static string CreateTraceRoot()
        => Path.Combine(Path.GetTempPath(), "UiDx.Tests", Guid.NewGuid().ToString("N"));

    private static AppSessionOptions Options(string traceRoot)
        => new() { TraceDirectory = traceRoot };

    /// <summary>Process ids of the Notepad image name that are currently running.</summary>
    private static int[] NotepadProcessIds()
        => Process.GetProcessesByName(ProcessName).Select(process =>
        {
            var id = process.Id;
            process.Dispose();
            return id;
        }).ToArray();

    /// <summary>Process ids of the Notepad image name that currently own a main window.</summary>
    private static int[] WindowedNotepadProcessIds()
        => Process.GetProcessesByName(ProcessName).Select(process =>
        {
            try
            {
                return (!process.HasExited && process.MainWindowHandle != IntPtr.Zero) ? process.Id : 0;
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                process.Dispose();
            }
        }).Where(id => id != 0).ToArray();

    private static bool IsRunning(int processId) => NotepadProcessIds().Contains(processId);

    private static void KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Reads the trace while the session may still hold the writer open.</summary>
    private static string[] GetTraceLines(string traceDirectory)
    {
        var path = Path.Combine(traceDirectory, "trace.jsonl");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    // ── N1: Launch → Dispose terminates the process and finalizes the trace ──
    [Fact]
    public void N1_Launch_StartsProcess_AndDisposeTerminatesIt()
    {
        var traceRoot = CreateTraceRoot();
        var before = NotepadProcessIds();
        int processId;
        string traceDirectory;

        using (var session = AppSession.Launch("notepad.exe", options: Options(traceRoot)))
        {
            processId = session.App.ProcessId;
            traceDirectory = session.Trace.SessionDirectory;

            Assert.True(session.IsAlive);
            Assert.DoesNotContain(processId, before); // a process really started by Launch
            Assert.True(File.Exists(Path.Combine(traceDirectory, "trace.jsonl")));

            var linesWhileRunning = GetTraceLines(traceDirectory);
            Assert.Contains("launched notepad.exe (pid ", linesWhileRunning[0], StringComparison.Ordinal);
        }

        Assert.False(IsRunning(processId));

        var lines = GetTraceLines(traceDirectory);
        Assert.Contains(lines, line => line.Contains("launched notepad.exe (pid "));
        Assert.Contains(lines, line => line.Contains("\"message\":\"session ended (exit "));
    }

    // ── N2: Attach → Dispose must not kill a foreign process ────────────────
    [Fact]
    public void N2_Attach_DoesNotKillForeignProcessOnDispose()
    {
        var traceRoot = CreateTraceRoot();
        var before = NotepadProcessIds();
        using var notepad = Process.Start("notepad.exe")!;
        var launchedPid = notepad.Id;
        try
        {
            // The alias may hand off to a new process, so attach the process that appeared.
            var attachedPid = WaitForWindowedProcess(before);
            Assert.NotEqual(0, attachedPid);

            string traceDirectory;
            using (var session = AppSession.Attach(ProcessName, options: Options(traceRoot)))
            {
                traceDirectory = session.Trace.SessionDirectory;
                Assert.Equal(attachedPid, session.App.ProcessId);
                Assert.True(session.IsAlive);
                Assert.Contains(
                    GetTraceLines(traceDirectory),
                    line => line.Contains("\"attached " + ProcessName + " (pid " + attachedPid + ")\""));
            }

            // The whole point of N2: Dispose leaves the foreign process alive.
            Assert.True(IsRunning(attachedPid));

            var lines = GetTraceLines(traceDirectory);
            Assert.Contains(lines, line => line.Contains("\"message\":\"session ended (exit "));
            Assert.Contains(lines, line => line.Contains("skipping kill"));
        }
        finally
        {
            KillProcess(launchedPid);
            foreach (var pid in NotepadProcessIds())
            {
                KillProcess(pid);
            }
        }
    }

    // ── N3: crash is detected by the next operation ──────────────────────────
    [Fact]
    public void N3_CrashedProcess_ThrowsCrashErrorOnNextOperation_AndPersistsTrace()
    {
        var traceRoot = CreateTraceRoot();
        var session = AppSession.Launch("notepad.exe", options: Options(traceRoot));
        var traceDirectory = session.Trace.SessionDirectory;
        var processId = session.App.ProcessId;
        try
        {
            Assert.True(session.IsAlive);

            KillProcess(processId); // kill from the outside
            var stopwatch = Stopwatch.StartNew();
            while (session.IsAlive && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(50);
            }

            Assert.False(session.IsAlive); // polling property never throws

            var error = Assert.Throws<UiDxCrashError>(() => session.EnsureAlive());
            Assert.Contains("exit code", error.Message);
            Assert.Contains(traceDirectory, error.Message);
            Assert.Contains("Call log:", error.Message);
            Assert.NotEmpty(error.CallLog);
        }
        finally
        {
            session.Dispose();
        }

        // Trace is finalized even after the crash.
        var lines = GetTraceLines(traceDirectory);
        Assert.Contains(lines, line => line.Contains("\"message\":\"launched notepad.exe (pid "));
        Assert.Contains(lines, line => line.Contains("\"message\":\"session ended (exit "));
    }

    // ── Build gate: Screenshot() writes a non-empty file into shots/ ────────
    [Fact]
    public void Screenshot_WritesNonEmptyFileIntoShotsDirectory()
    {
        var traceRoot = CreateTraceRoot();
        string traceDirectory;
        using (var session = AppSession.Launch("notepad.exe", options: Options(traceRoot)))
        {
            traceDirectory = session.Trace.SessionDirectory;
            session.Trace.Screenshot(label: "desktop");

            var shots = Directory.GetFiles(Path.Combine(traceDirectory, "shots"), "*.png");
            Assert.Single(shots);
            Assert.True(new FileInfo(shots[0]).Length > 0);
        }

        Assert.Contains(
            GetTraceLines(traceDirectory),
            line => line.Contains("screenshot saved: shots/"));
    }

    // ── Call log layout: phases indent by two spaces, no timestamps ──────────
    [Fact]
    public void TraceLog_PhaseIndentsFollowingLines_AndJsonlKeepsTimestamps()
    {
        var traceRoot = CreateTraceRoot();
        string traceDirectory;
        using (var session = AppSession.Launch("notepad.exe", options: Options(traceRoot)))
        {
            traceDirectory = session.Trace.SessionDirectory;
            session.Trace.Log("before phase");
            session.Trace.Phase("waiting for locator(#x)");
            session.Trace.Log("locator resolved to #x");

            var callLog = session.Trace.ToCallLog();
            var before = callLog.Single(line => line.EndsWith("before phase", StringComparison.Ordinal));
            var phase = callLog.Single(line => line.EndsWith("waiting for locator(#x)", StringComparison.Ordinal));
            var inside = callLog.Single(line => line.EndsWith("locator resolved to #x", StringComparison.Ordinal));

            // The internal Launch phase is closed again, so a client line starts at the base indent.
            Assert.StartsWith("  - ", before, StringComparison.Ordinal);
            Assert.StartsWith("  - ", phase, StringComparison.Ordinal);
            Assert.StartsWith("    - ", inside, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-", phase, StringComparison.Ordinal);
        }

        // The JSONL keeps timestamps, level and the structural indent.
        var first = GetTraceLines(traceDirectory)[0];
        Assert.Contains("\"ts\":", first);
        Assert.Contains("\"level\":", first);
        Assert.Contains("\"indent\":", first);
    }

    // ── Launch failure surfaces the original exception, not a timeout ───────
    [Fact]
    public void Launch_MissingExecutable_ThrowsOriginalException_AndFinalizesTrace()
    {
        var traceRoot = CreateTraceRoot();

        // A missing executable is a launch failure, not a timed out wait for a main window.
        Assert.Throws<System.ComponentModel.Win32Exception>(
            () => AppSession.Launch("this-executable-does-not-exist.exe", options: Options(traceRoot)));

        // Exactly one session directory was created and its trace was finalized.
        var sessionDirectories = Directory.GetDirectories(traceRoot);
        var sessionDirectory = Assert.Single(sessionDirectories);
        var lines = GetTraceLines(sessionDirectory);
        Assert.Contains(lines, line => line.Contains("launch failed:"));
        Assert.Contains(lines, line => line.Contains("\"message\":\"session ended (exit n/a)\""));
    }

    // ── Launch timeout (no main window) surfaces a UiDxTimeoutError ─────────
    [Fact]
    public void Launch_NoMainWindowWithinTimeout_ThrowsTimeoutErrorWithCallLog()
    {
        var traceRoot = CreateTraceRoot();

        // cmd.exe stays alive but owns no window that FlaUI can resolve within the timeout.
        var error = Assert.Throws<UiDxTimeoutError>(
            () => AppSession.Launch("cmd.exe", "/c pause", TimeSpan.FromSeconds(2), Options(traceRoot)));

        Assert.Contains("Timeout", error.Message);
        Assert.Contains("Call log:", error.Message);
        Assert.NotEmpty(error.CallLog);
        // Base indent is exactly 2 spaces; ToCallLog renders it, the message must not add another 2.
        Assert.Contains("\n  - launched cmd.exe (pid ", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n    - launched", error.Message, StringComparison.Ordinal);
    }

    // ── Launch waits for and resolves the main window ───────────────────────
    [Fact]
    public void Launch_ResolvesMainWindow()
    {
        var traceRoot = CreateTraceRoot();
        using var session = AppSession.Launch("notepad.exe", options: Options(traceRoot));
        var window = session.App.GetMainWindow(session.Automation, TimeSpan.FromSeconds(5));
        Assert.NotNull(window);
        Assert.True(File.Exists(Path.Combine(session.Trace.SessionDirectory, "trace.jsonl")));
    }

    // ── Attach without a candidate process surfaces a timeout error ─────────
    [Fact]
    public void Attach_MissingProcess_ThrowsTimeoutError()
    {
        var traceRoot = CreateTraceRoot();
        var error = Assert.Throws<UiDxTimeoutError>(
            () => AppSession.Attach("no-such-process-42", TimeSpan.FromMilliseconds(500), Options(traceRoot)));

        Assert.Contains("Timeout", error.Message);
        Assert.Contains("Call log:", error.Message);
    }

    /// <summary>
    /// Waits for a Notepad process with a main window that was not running before the manual launch.
    /// </summary>
    private static int WaitForWindowedProcess(int[] before)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var match = WindowedNotepadProcessIds().FirstOrDefault(pid => !before.Contains(pid));
            if (match != 0)
            {
                return match;
            }

            Thread.Sleep(100);
        }

        return 0;
    }
}
