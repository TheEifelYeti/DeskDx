using System.Diagnostics;
using FlaUI.Core.Definitions;

namespace UiDx.Tests;

/// <summary>
/// Step 2 acceptance tests T1–T8 against the TestTarget app. Also keeps the flat-phase guarantee
/// (T8) that replaced the accumulating Step 1 indentation.
/// </summary>
[Collection("TestTarget")]
public sealed class LocatorTests
{
    private readonly TestTargetFixture _fixture;

    public LocatorTests(TestTargetFixture fixture) => _fixture = fixture;

    private static string CreateTraceRoot()
        => Path.Combine(Path.GetTempPath(), "UiDx.Tests", Guid.NewGuid().ToString("N"));

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

    // ── T1: unique match by name ─────────────────────────────────────────────
    [Fact]
    public void T1_GetByName_ResolvesUniqueElement_AndLogsResolution()
    {
        var (session, _) = Start();
        using (session)
        {
            var element = session.GetByName("Speichern").Resolve();

            Assert.Equal("Speichern", element.Name);
            var callLog = session.Trace.ToCallLog();
            Assert.Contains(callLog, line => line.Contains("waiting for locator(name=\"Speichern\")"));
            Assert.Contains(callLog, line => line.Contains("locator resolved to Button \"Speichern\""));
        }
    }

    // ── T2: timeout with scope children dump ─────────────────────────────────
    [Fact]
    public void T2_GetByName_NoMatch_ThrowsTimeoutErrorWithScopeChildrenDump()
    {
        var (session, _) = Start();
        using (session)
        {
            var error = Assert.Throws<UiDxTimeoutError>(
                () => session.GetByName("GibtEsNicht").Resolve(TimeSpan.FromSeconds(1)));

            Assert.Contains("Timeout", error.Message);
            // The dump lists what is actually there (the classic "wrong window / wrong text" case).
            Assert.Contains(error.CallLog, line => line.Contains("scope children:"));
            Assert.Contains(error.CallLog, line => line.Contains("Button \"Speichern\""));
            Assert.Contains(error.CallLog, line => line.Contains("Duplikat"));
            // Truncated to 20 lines plus the "(+n more)" marker.
            Assert.Contains(error.CallLog, line => line.Contains("more)"));
            var childLines = error.CallLog.Count(line => line.Contains("[AutomationId="));
            Assert.True(childLines <= 22, $"dump listed {childLines} element lines, expected <= 22");
        }
    }

    // ── T3: strict mode violation ────────────────────────────────────────────
    [Fact]
    public void T3_GetByName_Duplicate_ThrowsStrictError()
    {
        var (session, _) = Start();
        using (session)
        {
            var error = Assert.Throws<UiDxStrictError>(() => session.GetByName("Duplikat").Resolve());

            Assert.StartsWith("strict mode violation:", error.Message, StringComparison.Ordinal);
            Assert.Contains("name=\"Duplikat\"", error.Message, StringComparison.Ordinal);
            Assert.Contains("resolved to 2 elements:", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, error.Matches.Count);
            Assert.All(error.Matches, match => Assert.Contains("Button \"Duplikat\"", match));
            Assert.Contains("Call log:", error.Message, StringComparison.Ordinal);
        }
    }

    // ── T4: Nth disables strict mode ─────────────────────────────────────────
    [Fact]
    public void T4_Nth_TakesExplicitMatch_AndLogsStrictModeOff()
    {
        var (session, _) = Start();
        using (session)
        {
            var first = session.GetByName("Duplikat").Nth(0).Resolve();
            Assert.Equal("btnDup1", first.AutomationId);

            var second = session.GetByName("Duplikat").Nth(1).Resolve();
            Assert.Equal("btnDup2", second.AutomationId);

            Assert.Contains(
                session.Trace.ToCallLog(),
                line => line.Contains("strict mode off: taking match"));
        }
    }

    // ── T5: chaining scopes the search to the parent subtree ────────────────
    [Fact]
    public void T5_Chaining_SearchesOnlyTheSubtreeOfTheScope()
    {
        var (session, _) = Start();
        using (session)
        {
            // "GruppenKnopf" exists in "Gruppe A" and "Gruppe B"; each scoped locator finds exactly one.
            var inA = session.GetByName("Gruppe A").GetByName("GruppenKnopf").Resolve();
            var inB = session.GetByName("Gruppe B").GetByName("GruppenKnopf").Resolve();

            Assert.Equal("btnA1", inA.AutomationId);
            Assert.Equal("btnB1", inB.AutomationId);

            // Scope resolution is part of the call log.
            Assert.Contains(
                session.Trace.ToCallLog(),
                line => line.Contains("scope resolved to Group \"Gruppe A\""));
        }
    }

    // ── T6: VB6 fallback chain, stage 2 ──────────────────────────────────────
    [Fact]
    public void T6_VB6Locator_FallsBackToStage2_AndLogsTheStage()
    {
        var (session, _) = Start();
        using (session)
        {
            var element = VB6Locator.Find(session, ControlType.Button, "Speichern");

            Assert.Equal("Speichern", element.Name);
            Assert.Contains(
                session.Trace.ToCallLog(),
                line => line.Contains("vb6 locator stage 2 matched: Button \"Speichern\""));
        }
    }

    // ── T7: per-call timeout override ────────────────────────────────────────
    [Fact]
    public void T7_TimeoutOverride_TimesOutAfterTheGivenDuration()
    {
        var (session, _) = Start();
        using (session)
        {
            var stopwatch = Stopwatch.StartNew();
            var error = Assert.Throws<UiDxTimeoutError>(
                () => session.GetByName("GibtEsNicht").Resolve(TimeSpan.FromMilliseconds(200)));
            stopwatch.Stop();

            Assert.InRange(stopwatch.ElapsedMilliseconds, 150, 3000);
            Assert.Contains("Timeout 200ms exceeded", error.Message, StringComparison.Ordinal);
            Assert.Contains("waiting for locator(name=\"GibtEsNicht\") (timeout 200ms)", error.Message, StringComparison.Ordinal);
        }
    }

    // ── T8: two phases are both flat at base level ───────────────────────────
    [Fact]
    public void T8_TwoPhases_BothStartAtBaseLevel()
    {
        var (session, _) = Start();
        using (session)
        {
            session.Trace.Phase("phase one");
            session.Trace.Log("child of one");
            session.Trace.Phase("phase two");
            session.Trace.Log("child of two");

            var callLog = session.Trace.ToCallLog();
            var phaseOne = callLog.Single(line => line.EndsWith("phase one", StringComparison.Ordinal));
            var phaseTwo = callLog.Single(line => line.EndsWith("phase two", StringComparison.Ordinal));
            var childOne = callLog.Single(line => line.EndsWith("child of one", StringComparison.Ordinal));
            var childTwo = callLog.Single(line => line.EndsWith("child of two", StringComparison.Ordinal));

            Assert.StartsWith("  - ", phaseOne, StringComparison.Ordinal);
            Assert.StartsWith("  - ", phaseTwo, StringComparison.Ordinal);
            Assert.StartsWith("    - ", childOne, StringComparison.Ordinal);
            Assert.StartsWith("    - ", childTwo, StringComparison.Ordinal);
        }
    }

    // ── Extra: AutomationId lookup and AccessibleName priority ───────────────
    [Fact]
    public void Extra_GetByAutomationId_UsesDesignerName_AndAccessibleNameWins()
    {
        var (session, _) = Start();
        using (session)
        {
            var byId = session.GetByAutomationId("btnSave").Resolve();
            Assert.Equal("Speichern", byId.Name);

            // btnId has AccessibleName="aid-btnId", which has priority over Text and becomes UIA Name.
            var accessible = session.GetByAutomationId("btnId").Resolve();
            Assert.Equal("aid-btnId", accessible.Name);

            // Name lookup by the accessible name also works.
            Assert.Equal("btnId", session.GetByName("aid-btnId").Resolve().AutomationId);
        }
    }

    // ── T9: AutomationId semantics stay strict (no silent OR-name fallback) ───
    [Fact]
    public void T9_GetByAutomationId_DoesNotSilentlyMatchByName()
    {
        var (session, _) = Start();
        using (session)
        {
            // "Speichern" is the UIA NAME of btnSave, not its AutomationId.
            // A strict AutomationId locator must therefore NOT find it —
            // if this test turns red, the client-side OR-name fallback in
            // Locator.IsMatch is live and blurs the locator semantics
            // (spec review 15.10.: remove it, VB6Locator is the explicit way).
            Assert.Throws<UiDxTimeoutError>(
                () => session.GetByAutomationId("Speichern").Resolve(TimeSpan.FromMilliseconds(500)));
        }
    }

    // ── Extra: TryResolve does not throw ──────────────────────────────────────
    [Fact]
    public void Extra_TryResolve_ReturnsFalseWithoutThrowing()
    {
        var (session, _) = Start();
        using (session)
        {
            Assert.False(session.GetByName("GibtEsNicht").TryResolve(out var missing, TimeSpan.FromMilliseconds(200)));
            Assert.Null(missing);
            Assert.True(session.GetByName("Speichern").TryResolve(out var found));
            Assert.NotNull(found);
        }
    }

    // ── Extra: the resolved element supports typed access ────────────────────
    [Fact]
    public void Extra_ResolveGeneric_ReturnsTypedElement()
    {
        var (session, _) = Start();
        using (session)
        {
            var button = session.GetByAutomationId("btnId").Resolve<FlaUI.Core.AutomationElements.Button>();
            Assert.Equal("aid-btnId", button.Name);
        }
    }
}
