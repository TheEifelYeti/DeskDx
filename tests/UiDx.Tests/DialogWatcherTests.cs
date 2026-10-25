using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UiDx.Tests;

/// <summary>
/// Step 5 acceptance tests D1–D7: the dialog watcher.
/// </summary>
/// <remarks>
/// Each test builds its own session so registry entries and dialog policy never leak between tests.
/// </remarks>
[Collection("TestTarget")]
public sealed class DialogWatcherTests
{
    private readonly TestTargetFixture _fixture;

    public DialogWatcherTests(TestTargetFixture fixture) => _fixture = fixture;

    private static string CreateTraceRoot()
        => Path.Combine(Path.GetTempPath(), "UiDx.Tests", Guid.NewGuid().ToString("N"));

    private AppSession Start(AppSessionOptions? options = null)
        => AppSession.Launch(
            _fixture.ExecutablePath,
            options: options ?? new AppSessionOptions { TraceDirectory = CreateTraceRoot() });

    /// <summary>
    /// Triggers the dialog-opening control and waits until the watcher has logged the dialog.
    /// </summary>
    /// <remarks>
    /// This uses <c>PostMessage(BM_CLICK)</c> rather than <see cref="Locator.Click"/> or
    /// <c>InvokePattern</c>:
    /// <list type="bullet">
    ///   <item><c>InvokePattern.Invoke</c> blocks until the modal dialog is closed, so it cannot be
    ///     used as the trigger from the test thread.</item>
    ///   <item>A mouse click only works when the application owns the foreground window, which made
    ///     the tests flaky whenever the test host was busy.</item>
    ///   <item><c>PostMessage</c> is asynchronous and needs neither foreground nor a COM call from a
    ///     foreign thread.</item>
    /// </list>
    /// </remarks>
    private static void TriggerDialog(AppSession session, string automationId)
    {
        var element = session.GetByAutomationId(automationId).Resolve();
        var handle = element.Properties.NativeWindowHandle.ValueOrDefault;
        Assert.NotEqual(IntPtr.Zero, handle);

        PostMessage(handle, ButtonClickMessage, IntPtr.Zero, IntPtr.Zero);

        Assert.True(
            WaitForTrace(session, line => line.Contains("dialog appeared:"), TimeSpan.FromSeconds(5)),
            $"no dialog appeared after triggering {automationId}");
    }

    private const uint ButtonClickMessage = 0x00F5; // BM_CLICK

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Waits until the call log contains a line matching the predicate.</summary>
    private static bool WaitForTrace(AppSession session, Func<string, bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (session.Trace.ToCallLog().Any(predicate))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

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

    // ── D1: AutoDismiss default answers the OK dialog, no hang ───────────────
    [Fact]
    public void D1_AutoDismiss_AnswersOkDialog_AndNoHang()
    {
        using var session = Start();

        TriggerDialog(session, "btnMsgBox");

        // The watcher must dismiss the dialog, so the next operation cannot hang.
        Assert.True(
            WaitForTrace(session, line => line.Contains("dialog answered: \"OK\" (default)"), TimeSpan.FromSeconds(5)),
            "expected the OK button to be clicked by the default policy");

        // Proof that the application thread is free again.
        var status = session.GetByAutomationId("lblStatus").Resolve(TimeSpan.FromSeconds(3));
        Assert.NotNull(status);
    }

    // ── D2: registered Yes answers the Yes/No dialog ────────────────────────
    [Fact]
    public void D2_RegisteredYes_AnswersYesNoDialog()
    {
        using var session = Start();
        session.Dialogs.On("Wirklich löschen", "Ja");

        TriggerDialog(session, "btnMsgBoxYesNo");

        Assert.True(
            WaitForTrace(session, line => line.Contains("(registered)"), TimeSpan.FromSeconds(5)),
            "expected a registered answer");
        Expect.That(session.GetByAutomationId("lblStatus")).ToHaveText("Ja geklickt", TimeSpan.FromSeconds(5));
    }

    // ── D3: registered No, matched by a partial text ────────────────────────
    [Fact]
    public void D3_RegisteredNo_MatchesByContains()
    {
        using var session = Start();
        session.Dialogs.On("Wirklich", "Nein");

        TriggerDialog(session, "btnMsgBoxYesNo");

        Assert.True(
            WaitForTrace(session, line => line.Contains("dialog answered: \"Nein\""), TimeSpan.FromSeconds(5)),
            "expected the No button to be clicked");
        Expect.That(session.GetByAutomationId("lblStatus")).ToHaveText("Nein geklickt", TimeSpan.FromSeconds(5));
    }

    // ── D4: LogOnly leaves the dialog to the test ───────────────────────────
    [Fact]
    public void D4_LogOnly_LetsTheTestHandleTheDialog()
    {
        using var session = Start(new AppSessionOptions
        {
            TraceDirectory = CreateTraceRoot(),
            DialogAction = DialogAction.LogOnly,
        });

        TriggerDialog(session, "btnMsgBox");

        // The watcher logs but does not answer.
        Assert.True(
            WaitForTrace(session, line => line.Contains("dialog appeared:"), TimeSpan.FromSeconds(5)),
            "expected a 'dialog appeared' line");
        Assert.True(
            WaitForTrace(session, line => line.Contains("(logOnly)"), TimeSpan.FromSeconds(5)),
            "expected a logOnly answer line");

        // The test finds and dismisses the dialog itself. The dialog is an owned window, so it is
        // searched from the main window rather than from the desktop.
        var mainWindow = session.MainWindow!;
        var okButton = mainWindow.FindFirstDescendant(cf => cf.ByName("OK"));
        Assert.NotNull(okButton);
        okButton!.Patterns.Invoke.PatternOrDefault?.Invoke();

        var status = session.GetByAutomationId("lblStatus").Resolve(TimeSpan.FromSeconds(3));
        Assert.NotNull(status);
    }

    // ── D5: the non-modal second form is not a dialog ───────────────────────
    [Fact]
    public void D5_ModelessSecondForm_IsNotTouched()
    {
        using var session = Start();

        // btnNewForm opens a modeless window, so no dialog line is expected — Invoke it directly
        // (no dialog means no blocking call, so no background task is needed).
        session.GetByAutomationId("btnNewForm").Invoke();

        // Prove the invocation landed before asserting the absence of a dialog line.
        Expect.That(session.GetByAutomationId("lblStatus")).ToHaveText("Zweitfenster offen", TimeSpan.FromSeconds(5));

        // Give the watcher two poll intervals to (wrongly) react.
        Thread.Sleep(800);

        Assert.DoesNotContain(
            session.Trace.ToCallLog(),
            line => line.Contains("dialog appeared:"));
        Assert.DoesNotContain(
            session.Trace.ToCallLog(),
            line => line.Contains("dialog answered:"));

        // The second window is still alive. It is an OWNED window, so UIA reports it as a child of
        // its owner rather than as a desktop-level window — enumerate by HWND instead.
        var windowTitles = ProcessWindowEnumerator
            .GetVisibleTopLevelWindows(session.App.ProcessId)
            .Select(w => w.Title)
            .ToArray();
        Assert.Contains("Zweitfenster", windowTitles);
    }

    // ── D6: killing the app right after the dialog must not throw ───────────
    [Fact]
    public void D6_KillAfterDialog_ShutsTheWatcherDownCleanly()
    {
        var session = Start();
        try
        {
            TriggerDialog(session, "btnMsgBox");
            session.Kill();

            // Dispose must not throw even though the process died with a dialog open.
            session.Dispose();
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Assert.Fail($"Dispose threw after Kill: {ex}");
            }
        }
    }

    // ── D7: trace layout — flat phase + detail ─────────────────────────────
    [Fact]
    public void D7_DialogTrace_UsesFlatPhaseAndDetailIndent()
    {
        using var session = Start();
        TriggerDialog(session, "btnMsgBox");
        Assert.True(
            WaitForTrace(session, line => line.Contains("dialog answered:"), TimeSpan.FromSeconds(5)),
            "expected the dialog to be answered");

        var callLog = session.Trace.ToCallLog();
        var appeared = callLog.Single(line => line.Contains("dialog appeared:"));
        var answered = callLog.Single(line => line.Contains("dialog answered:"));

        Assert.StartsWith("  - dialog appeared:", appeared, StringComparison.Ordinal);
        Assert.StartsWith("    - dialog answered:", answered, StringComparison.Ordinal);

        // The JSONL agrees with the rendered call log. CreateSession appends a timestamp folder,
        // so the session's own directory is the one to read.
        var lines = GetTraceLines(session.Trace.SessionDirectory);
        Assert.Contains(lines, line => line.Contains("\"level\":\"phase\"") && line.Contains("dialog appeared:"));
        Assert.Contains(lines, line => line.Contains("\"indent\":4") && line.Contains("dialog answered:"));
    }
}

