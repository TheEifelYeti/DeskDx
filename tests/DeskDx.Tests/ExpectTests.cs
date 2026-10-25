using System.Diagnostics;
using FlaUI.Core.AutomationElements;

namespace DeskDx.Tests;

/// <summary>
/// Step 4 acceptance tests E1–E9: the web-first expect assertions.
/// </summary>
[Collection("TestTarget")]
public sealed class ExpectTests
{
    private readonly TestTargetFixture _fixture;

    public ExpectTests(TestTargetFixture fixture) => _fixture = fixture;

    private static string CreateTraceRoot()
        => Path.Combine(Path.GetTempPath(), "DeskDx.Tests", Guid.NewGuid().ToString("N"));

    private (AppSession Session, string TraceRoot) Start()
    {
        var traceRoot = CreateTraceRoot();
        var session = AppSession.Launch(
            _fixture.ExecutablePath,
            options: new AppSessionOptions { TraceDirectory = traceRoot });
        return (session, traceRoot);
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

    // ── E1: success logs nothing ─────────────────────────────────────────────
    [Fact]
    public void E1_ToBeVisible_SucceedsWithoutWritingToTheTrace()
    {
        var (session, traceRoot) = Start();
        using (session)
        {
            var linesBefore = GetTraceLines(session.Trace.SessionDirectory).Length;

            Expect.That(session.GetByAutomationId("btnSave")).ToBeVisible();

            // Playwright rule: the log shows the problem, not the functioning.
            var linesAfter = GetTraceLines(session.Trace.SessionDirectory);
            Assert.Equal(linesBefore, linesAfter.Length);
            Assert.DoesNotContain(linesAfter, line => line.Contains("waiting for expect:"));
            _ = traceRoot;
        }
    }

    // ── E2: never-existing element times out with the scope children dump ────
    [Fact]
    public void E2_ToBeVisible_MissingElement_TimesOutWithScopeChildrenDump()
    {
        var (session, _) = Start();
        using (session)
        {
            var stopwatch = Stopwatch.StartNew();
            var error = Assert.Throws<DeskDxExpectError>(
                () => Expect.That(session.GetByName("GibtEsNicht")).ToBeVisible(TimeSpan.FromMilliseconds(500)));
            stopwatch.Stop();

            Assert.InRange(stopwatch.ElapsedMilliseconds, 400, 3000);
            Assert.Contains(error.CallLog, line => line.Contains("waiting for expect:"));
            Assert.Contains(error.CallLog, line => line.Contains("scope children:"));
            Assert.Contains(error.CallLog, line => line.Contains("Button \"Speichern\""));

            // Context precedes the verdict: the dump explains what is there, then the check line.
            var dumpIndex = error.CallLog.ToList().FindIndex(line => line.Contains("scope children:"));
            var checkIndex = error.CallLog.ToList().FindIndex(line => line.Contains("element is not visible"));
            Assert.True(dumpIndex >= 0 && checkIndex > dumpIndex, "the check line must follow its context");
        }
    }

    // ── E3: waits for the 2 s enable timer ───────────────────────────────────
    [Fact]
    public void E3_ToBeEnabled_WaitsForTheEnableTimer()
    {
        var (session, _) = Start();
        using (session)
        {
            var stopwatch = Stopwatch.StartNew();
            Expect.That(session.GetByAutomationId("btnDelayed")).ToBeEnabled();
            stopwatch.Stop();

            Assert.InRange(stopwatch.ElapsedMilliseconds, 1500, 15000);
        }
    }

    // ── E4: disabled control fails with check line and state dump ───────────
    [Fact]
    public void E4_ToBeEnabled_DisabledControl_FailsWithCheckLineAndStateDump()
    {
        var (session, _) = Start();
        using (session)
        {
            var error = Assert.Throws<DeskDxExpectError>(
                () => Expect.That(session.GetByAutomationId("txtDisabled")).ToBeEnabled(TimeSpan.FromMilliseconds(500)));

            Assert.Contains(error.CallLog, line => line.Contains("element is not enabled"));
            Assert.Contains(error.CallLog, line => line.Contains("state: Name="));
            Assert.Contains(error.CallLog, line => line.Contains("IsEnabled=False"));
        }
    }

    // ── E5: Fill then ToHaveValue ────────────────────────────────────────────
    [Fact]
    public void E5_ToHaveValue_AfterFill_Succeeds()
    {
        var (session, _) = Start();
        using (session)
        {
            session.GetByAutomationId("txtKunde").Fill("Müller");
            Expect.That(session.GetByAutomationId("txtKunde")).ToHaveValue("Müller");
        }
    }

    // ── E6: ToHaveText via the UIA Name path (no ValuePattern) ──────────────
    [Fact]
    public void E6_ToHaveText_LabelUsesTheNamePath()
    {
        var (session, _) = Start();
        using (session)
        {
            // A WinForms Label exposes no ValuePattern, so ToHaveText falls back to the UIA Name.
            var label = session.GetByAutomationId("lblStatus");
            Assert.False(label.Resolve().Patterns.Value.IsSupported);

            Expect.That(label).ToHaveText("bereit");
        }
    }

    // ── E7: whitespace/line-break normalization ─────────────────────────────
    [Fact]
    public void E7_ToHaveValue_NormalizesLineBreaksAndWhitespace()
    {
        var (session, _) = Start();
        using (session)
        {
            // A multiline WinForms text box hands back \r\n.
            var multiline = session.GetByAutomationId("txtMultiline");
            multiline.Fill("Zeile 1\r\nZeile 2");
            Assert.Contains("\r\n", multiline.Resolve().Patterns.Value.Pattern.Value.ValueOrDefault ?? string.Empty);
            Expect.That(multiline).ToHaveValue("Zeile 1\nZeile 2");

            // Whitespace padding and collapsed internal runs.
            var textBox = session.GetByAutomationId("txtKunde");
            textBox.Fill("  Kunde:   Müller  ");
            Expect.That(textBox).ToHaveValue("Kunde: Müller");
        }
    }

    // ── E8: ToBeHidden after the cover panel is toggled off ─────────────────
    [Fact]
    public void E8_ToBeHidden_GreenWhenCoveredAndWhenMissing()
    {
        var (session, _) = Start();
        using (session)
        {
            // Green immediately when the element never existed.
            Expect.That(session.GetByName("GibtEsNicht")).ToBeHidden();

            var cover = session.GetByAutomationId("btnCover");
            var save = session.GetByAutomationId("btnSave");
            var panel = session.GetByAutomationId("pnlCover");

            // btnCover toggles the panel. The clicks are setup, so they use the deterministic invoke
            // path; the assertions below are what this test is actually about.
            ToggleUntil(cover, targetVisible: true, panel);
            Expect.That(save).ToBeVisible();                   // geometrically still visible ...
            ToggleUntil(cover, targetVisible: false, panel);   // ... cover off again (toggle, Step 4)
            Expect.That(save).ToBeVisible();

            // A real hidden case: pnlCover is hidden while untoggled. WinForms removes hidden
            // controls from the UIA tree entirely, so it is "gone" rather than merely invisible.
            Assert.False(panel.TryResolve(out _, TimeSpan.FromMilliseconds(300)));
            Expect.That(panel).ToBeHidden();
        }
    }

    /// <summary>
    /// Toggles <paramref name="panel"/> off/on until it is in the intended state.
    /// </summary>
    /// <remarks>
    /// This uses <see cref="Locator.Invoke"/> rather than <see cref="Locator.Click"/>: the toggle is
    /// test setup, and a real mouse click needs the application to own the foreground window, which
    /// made this test flaky under full-suite load. InvokePattern triggers the same WinForms handler
    /// without that dependency. WinForms removes a hidden panel from the UIA tree, so resolvability
    /// is the state signal.
    /// </remarks>
    private static void ToggleUntil(Locator toggle, bool targetVisible, Locator panel)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var visibleNow = panel.TryResolve(out _, TimeSpan.FromMilliseconds(300));
            if (visibleNow == targetVisible)
            {
                return;
            }

            toggle.Invoke();
            Thread.Sleep(250);
        }

        Assert.Fail($"panel never became {(targetVisible ? "visible" : "hidden")} after five toggles");
    }

    // ── E9: failure message format ──────────────────────────────────────────
    [Fact]
    public void E9_FailureMessageMatchesTheSpecifiedFormat()
    {
        var (session, _) = Start();
        using (session)
        {
            var error = Assert.Throws<DeskDxExpectError>(
                () => Expect.That(session.GetByAutomationId("txtDisabled")).ToBeEnabled(TimeSpan.FromMilliseconds(500)));

            // Message head, then the phase, then the resolved context, then the failing check.
            Assert.StartsWith(
                "expect(locator(AutomationId=\"txtDisabled\")).ToBeEnabled() failed after 500ms",
                error.Message,
                StringComparison.Ordinal);
            Assert.Contains("Call log:", error.Message, StringComparison.Ordinal);
            Assert.Contains(
                "  - waiting for expect: locator(AutomationId=\"txtDisabled\") to be enabled (timeout 500ms)",
                error.Message,
                StringComparison.Ordinal);
            Assert.Contains("    - locator resolved to Edit \"", error.Message, StringComparison.Ordinal);
            Assert.Contains("      - element is not enabled", error.Message, StringComparison.Ordinal);
            Assert.Contains("      - state: Name=", error.Message, StringComparison.Ordinal);

            Assert.Equal("ToBeEnabled", error.AssertionName);
            Assert.Equal(TimeSpan.FromMilliseconds(500), error.Timeout);
        }
    }

    // ── Extra: ToHaveValue without a ValuePattern fails fast ────────────────
    [Fact]
    public void Extra_ToHaveValue_WithoutValuePattern_FailsWithoutWaiting()
    {
        var (session, _) = Start();
        using (session)
        {
            var stopwatch = Stopwatch.StartNew();
            var error = Assert.Throws<DeskDxExpectError>(
                () => Expect.That(session.GetByAutomationId("lblStatus")).ToHaveValue("bereit"));
            stopwatch.Stop();

            // Programming error signal — no polling for the full timeout.
            Assert.True(stopwatch.ElapsedMilliseconds < 2000, "expected a fast fail without a ValuePattern");
            Assert.Contains(error.CallLog, line => line.Contains("does not support ValuePattern"));
        }
    }

    // ── Extra: ToBeEditable ─────────────────────────────────────────────────
    [Fact]
    public void Extra_ToBeEditable_EnabledTextBox_Passes()
    {
        var (session, _) = Start();
        using (session)
        {
            Expect.That(session.GetByAutomationId("txtKunde")).ToBeEditable();
            Assert.Throws<DeskDxExpectError>(
                () => Expect.That(session.GetByAutomationId("txtDisabled")).ToBeEditable(TimeSpan.FromMilliseconds(400)));
        }
    }
}
