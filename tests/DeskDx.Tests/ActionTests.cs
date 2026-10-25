using System.Diagnostics;
using FlaUI.Core.AutomationElements;

namespace DeskDx.Tests;

/// <summary>
/// Step 3 acceptance tests G1–G9: the actionability gate and the actions.
/// </summary>
[Collection("TestTarget")]
public sealed class ActionTests
{
    private readonly TestTargetFixture _fixture;

    public ActionTests(TestTargetFixture fixture) => _fixture = fixture;

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

    // ── G1: happy path — gate green on the first poll, no check lines ────────
    [Fact]
    public void G1_Click_GreenGate_LogsExecutedWithoutCheckLines()
    {
        var (session, _) = Start();
        using (session)
        {
            session.GetByName("Speichern").Click();

            var callLog = session.Trace.ToCallLog();
            Assert.Contains(callLog, line => line.Contains("click executed at"));
            // A check that is green on the first poll is not logged (no failure lines, no state dump).
            Assert.DoesNotContain(callLog, line => line.Contains("element is not "));
            Assert.DoesNotContain(callLog, line => line.Contains("element was covered"));
            Assert.DoesNotContain(callLog, line => line.Contains("state: Name="));
        }
    }

    // ── G2: click waits for a control that becomes enabled after 2 s ─────────
    [Fact]
    public void G2_Click_WaitsUntilDelayedButtonBecomesEnabled()
    {
        var (session, _) = Start();
        using (session)
        {
            var delayed = session.GetByAutomationId("btnDelayed");
            // The button is disabled on launch and enabled by a 2 s timer.
            Assert.False(session.GetByAutomationId("btnDelayed").Resolve().IsEnabled);

            var stopwatch = Stopwatch.StartNew();
            delayed.Click();
            stopwatch.Stop();

            // The gate must have waited for the enable timer (>= ~1.5 s, generous lower bound).
            Assert.InRange(stopwatch.ElapsedMilliseconds, 1500, 15000);

            var callLog = session.Trace.ToCallLog();
            // Repeated check lines while the gate waits, then the successful action.
            Assert.True(callLog.Count(line => line.Contains("element is not enabled")) >= 3);
            Assert.Contains(callLog, line => line.Contains("click executed at"));
            // The gate does not report a timeout on the happy path.
            Assert.DoesNotContain(callLog, line => line.Contains("state: Name="));
        }
    }

    // ── G3: gate timeout on a permanently disabled control + state dump ──────
    [Fact]
    public void G3_Click_DisabledControl_ThrowsTimeoutWithCheckLinesAndStateDump()
    {
        var (session, _) = Start();
        using (session)
        {
            var error = Assert.Throws<DeskDxTimeoutError>(
                () => session.GetByAutomationId("btnDelayed").Click(TimeSpan.FromMilliseconds(500)));

            // At least one failing check line, plus the full UIA state dump.
            Assert.Contains(error.CallLog, line => line.Contains("element is not enabled"));
            Assert.Contains(error.CallLog, line => line.Contains("state: Name=\"Langsam\" ControlType=Button"));
            Assert.Contains(error.CallLog, line => line.Contains("IsEnabled=False"));
            Assert.Contains(error.CallLog, line => line.Contains("Patterns=Invoke,LegacyIAccessible"));

            // One locator phase only: the gate re-checks within the same phase.
            Assert.Single(error.CallLog, line => line.Contains("waiting for locator("));
        }
    }

    // ── G4: Fill sets the value via ValuePattern ─────────────────────────────
    [Fact]
    public void G4_Fill_SetsValueOnTextBox()
    {
        var (session, _) = Start();
        using (session)
        {
            session.GetByAutomationId("txtKunde").Fill("Kunde: Müller");

            var value = session.GetByAutomationId("txtKunde").Resolve()
                .Patterns.Value.Pattern.Value.ValueOrDefault;
            Assert.Equal("Kunde: Müller", value);
            Assert.Contains(session.Trace.ToCallLog(), line => line.Contains("fill executed: value set to"));
        }
    }

    // ── G5: Fill on a disabled text box fails the Editable/Enabled check ─────
    [Fact]
    public void G5_Fill_DisabledTextBox_TimesOutWithEditableOrEnabledLine()
    {
        var (session, _) = Start();
        using (session)
        {
            var error = Assert.Throws<DeskDxTimeoutError>(
                () => session.GetByAutomationId("txtDisabled").Fill("geht nicht", TimeSpan.FromMilliseconds(500)));

            // Editable is checked before Enabled, so the specific Fill message is reported.
            Assert.Contains(error.CallLog, line => line.Contains("element is not editable"));
            Assert.Contains(error.CallLog, line => line.Contains("state: Name="));
        }
    }

    // ── G6: Invoke bypasses the gate entirely ────────────────────────────────
    [Fact]
    public void G6_Invoke_WorksOnDisabledControlWithoutGate()
    {
        var (session, _) = Start();
        using (session)
        {
            // Escape hatch: InvokePattern is called directly, so a disabled button is fine.
            // Deterministic, no waiting — this is deliberately not the default path.
            var stopwatch = Stopwatch.StartNew();
            session.GetByAutomationId("btnDelayed").Invoke();
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 3000, "Invoke must not wait for the gate");
            Assert.Contains(
                session.Trace.ToCallLog(),
                line => line.Contains("invoke executed (gate skipped)"));
            Assert.DoesNotContain(
                session.Trace.ToCallLog(),
                line => line.Contains("element is not enabled"));
        }
    }

    // ── G7: force still checks Enabled ───────────────────────────────────────
    [Fact]
    public void G7_ClickForce_StillFailsOnDisabledControl()
    {
        var (session, _) = Start();
        using (session)
        {
            // force only skips Visible/Stable/ReceivesEvents — Enabled stays checked (Schritt 0).
            var error = Assert.Throws<DeskDxTimeoutError>(
                () => session.GetByAutomationId("btnDelayed").Click(TimeSpan.FromMilliseconds(500), force: true));

            Assert.Contains(error.CallLog, line => line.Contains("element is not enabled"));
            // The skipped checks must not appear.
            Assert.DoesNotContain(error.CallLog, line => line.Contains("element is not visible"));
            Assert.DoesNotContain(error.CallLog, line => line.Contains("element was covered"));
        }
    }

    // ── G8: ReceivesEvents detects a covering panel ──────────────────────────
    [Fact]
    public void G8_Click_CoveredElement_TimesOutWithCoveredLine()
    {
        var (session, _) = Start();
        using (session)
        {
            // btnCover shows pnlCover exactly over btnSave, which then no longer receives events.
            session.GetByAutomationId("btnCover").Click();

            var error = Assert.Throws<DeskDxTimeoutError>(
                () => session.GetByAutomationId("btnSave").Click(TimeSpan.FromSeconds(2)));
            Assert.Contains(error.CallLog, line => line.Contains("element was covered by"));
        }
    }

    // ── G9: increasing backoff between the check lines ───────────────────────
    [Fact]
    public void G9_CheckLines_UseIncreasingBackoff()
    {
        var (session, _) = Start();
        using (session)
        {
            Assert.Throws<DeskDxTimeoutError>(
                () => session.GetByAutomationId("btnDelayed").Click(TimeSpan.FromMilliseconds(1200)));

            var traceDirectory = session.Trace.SessionDirectory;

            // The check lines are the ones that failed and got logged. Their timestamps must show
            // the increasing backoff (50 -> 100 -> 200 -> 400), not a flat 100 ms poll.
            var checkTimes = GetTraceLines(traceDirectory)
                .Where(line => line.Contains("element is not enabled"))
                .Select(ParseTimestamp)
                .ToList();

            Assert.True(checkTimes.Count >= 3, $"expected >= 3 check lines, got {checkTimes.Count}");

            var deltas = checkTimes.Zip(checkTimes.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();

            // A flat poll would sit at one interval; the backoff must have visibly grown past the
            // first step. Asserting an absolute threshold avoids depending on scheduler noise.
            Assert.True(
                deltas.Max() >= 150,
                $"expected the backoff to grow beyond the first step, got deltas: {string.Join(", ", deltas.Select(d => d.ToString("0")))}");
        }
    }

    /// <summary>Reads the <c>ts</c> field from a trace.jsonl line (JSON escapes the + of the offset).</summary>
    private static DateTimeOffset ParseTimestamp(string jsonLine)
    {
        const string marker = "\"ts\":\"";
        var start = jsonLine.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = jsonLine.IndexOf('"', start);
        var raw = jsonLine[start..end].Replace("\\u002B", "+", StringComparison.Ordinal);
        return DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Extra: Select on a combo box ─────────────────────────────────────────
    [Fact]
    public void Extra_Select_ChoosesItemByLabel()
    {
        var (session, _) = Start();
        using (session)
        {
            session.GetByAutomationId("cmbAuswahl").Select("Zweite");

            Assert.Contains(session.Trace.ToCallLog(), line => line.Contains("select executed: \"Zweite\""));

            // WinForms has no SelectionPattern; FlaUI derives SelectedItem from the item IsSelected state.
            var comboBox = session.GetByAutomationId("cmbAuswahl").Resolve().AsComboBox();
            Assert.Equal("Zweite", comboBox.SelectedItem?.Text);
        }
    }
}
