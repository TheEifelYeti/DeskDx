using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskDx;

namespace DeskDx.Mcp.Tests;

/// <summary>
/// Step 7 acceptance tests M1–M7: the MCP tool handlers, called in-proc.
/// </summary>
/// <remarks>
/// Each test builds its own tool set and session so registry entries never leak between tests. The
/// locator strings are the ones an agent would derive from a <c>ui_snapshot</c> line.
/// </remarks>
[Collection("TestTarget")]
public sealed class McpToolTests
{
    private const string MainFormBaseline = "main-form";

    /// <summary>Controls whose UIA Name is volatile (the enable timer) — the engine's step-6 rule.</summary>
    private static readonly string[] VolatileControls = ["lblStatus", "txtDisabled"];

    private readonly TestTargetFixture _fixture;

    public McpToolTests(TestTargetFixture fixture) => _fixture = fixture;

    /// <summary>Launches TestTarget through the tool and returns the tools plus the session id.</summary>
    private (DeskDxTools Tools, string SessionId) Launch()
    {
        var tools = TestTargetFixture.NewTools();
        var launch = tools.Launch(_fixture.ExecutablePath, startupTimeoutMs: 15000);
        Assert.False(launch.IsError, launch.Text);

        var sessionId = LineValue(launch.Text, "sessionId:");
        Assert.False(string.IsNullOrEmpty(sessionId), $"no sessionId in launch result:\n{launch.Text}");
        return (tools, sessionId!);
    }

    private static string? LineValue(string text, string prefix)
        => text.Split('\n')
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..]
            .Trim();

    // ── M1: launch ────────────────────────────────────────────────────────────
    [Fact]
    public void M1_Launch_ReturnsSessionWindowAndTrace()
    {
        var tools = TestTargetFixture.NewTools();
        var launch = tools.Launch(_fixture.ExecutablePath, startupTimeoutMs: 15000);

        try
        {
            Assert.False(launch.IsError, launch.Text);

            var sessionId = LineValue(launch.Text, "sessionId:");
            Assert.False(string.IsNullOrEmpty(sessionId), $"no sessionId in launch result:\n{launch.Text}");
            Assert.StartsWith("s", sessionId!, StringComparison.Ordinal);

            // The response names the real form, not a container without content.
            Assert.Contains("mainWindow: \"DeskDx TestTarget\"", launch.Text, StringComparison.Ordinal);

            // The trace path is reported and exists on disk.
            var tracePath = LineValue(launch.Text, "tracePath:");
            Assert.False(string.IsNullOrWhiteSpace(tracePath), $"no tracePath in:\n{launch.Text}");
            Assert.True(File.Exists(Path.Combine(tracePath!, "trace.jsonl")), $"no trace.jsonl in {tracePath}");

            // The main window really is the form with the control tree (a snapshot proves it).
            var snapshot = tools.Snapshot(sessionId!);
            Assert.False(snapshot.IsError, snapshot.Text);
            Assert.StartsWith(
                "Window \"DeskDx TestTarget\" [AutomationId=MainForm]",
                snapshot.Text,
                StringComparison.Ordinal);

            tools.Dispose(sessionId!);
        }
        finally
        {
            var sessionId = LineValue(launch.Text, "sessionId:");
            if (!string.IsNullOrEmpty(sessionId))
            {
                tools.Dispose(sessionId);
            }
        }
    }

    // ── M2: gate-checked click ────────────────────────────────────────────────
    [Fact]
    public void M2_Click_OnSaveButton_Executes()
    {
        var (tools, sessionId) = Launch();
        try
        {
            var result = tools.Click(sessionId, "automationId=\"btnSave\"");

            Assert.False(result.IsError, result.Text);
            Assert.Contains("click executed", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── M3: a disabled target fails with the gate's evidence ──────────────────
    [Fact]
    public void M3_Click_OnDisabledButton_FailsWithEnabledCheck()
    {
        var (tools, sessionId) = Launch();
        try
        {
            // btnDelayed is disabled until the enable timer fires (2 s); a short timeout catches it.
            var result = tools.Click(sessionId, "automationId=\"btnDelayed\"", timeoutMs: 500);

            Assert.True(result.IsError, $"expected a gate failure, got:\n{result.Text}");
            Assert.Contains("element is not enabled", result.Text, StringComparison.Ordinal);

            // The state dump is the evidence that separates "disabled" from "not there yet".
            Assert.Contains("state:", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── M4: assertions, pass and fail ─────────────────────────────────────────
    [Fact]
    public void M4_Expect_VisibleAndText()
    {
        var (tools, sessionId) = Launch();
        try
        {
            var visible = tools.Expect(sessionId, "automationId=\"btnSave\"", "visible");
            Assert.False(visible.IsError, visible.Text);

            var text = tools.Expect(sessionId, "automationId=\"lblStatus\"", "hasText", "bereit");
            Assert.False(text.IsError, text.Text);

            // A failing assertion is a structured result, not an exception.
            var failing = tools.Expect(sessionId, "automationId=\"lblStatus\"", "hasText", "nicht-da");
            Assert.True(failing.IsError);
            Assert.Contains("failed after", failing.Text, StringComparison.Ordinal);
            Assert.Contains("call log", failing.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── M5: snapshot contract, match and the cover diff ───────────────────────
    [Fact]
    public void M5_MatchSnapshot_MatchesBaseline_AndCoverShowsDiff()
    {
        var (tools, sessionId) = Launch();
        try
        {
            var matched = tools.MatchSnapshot(sessionId, locator: null, MainFormBaseline, excludeIds: VolatileControls);
            Assert.False(matched.IsError, matched.Text);
            Assert.Contains("matched=true", matched.Text, StringComparison.Ordinal);

            // Cover btnSave: the panel appears in the tree, so the structural contract must break.
            var cover = tools.Invoke(sessionId, "automationId=\"btnCover\"");
            Assert.False(cover.IsError, cover.Text);

            var mismatch = tools.MatchSnapshot(sessionId, locator: null, MainFormBaseline, excludeIds: VolatileControls);
            Assert.True(mismatch.IsError, $"expected a mismatch, got:\n{mismatch.Text}");
            Assert.Contains("matched=false", mismatch.Text, StringComparison.Ordinal);
            Assert.Contains("pnlCover", mismatch.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── M6: the dialog watcher keeps the agent alive ──────────────────────────
    [Fact]
    public void M6_Invoke_OnMessageBoxButton_IsAnsweredByTheWatcher()
    {
        var (tools, sessionId) = Launch();
        try
        {
            // Gate-free trigger: a modal MessageBox would block a gated click's InvokePattern.
            var invoke = tools.Invoke(sessionId, "automationId=\"btnMsgBox\"");
            Assert.False(invoke.IsError, invoke.Text);

            // The watcher answers the dialog; without it the next UIA call would hang on the blocked
            // application thread.
            Assert.True(
                WaitForTrace(tools, sessionId, line => line.Contains("dialog appeared:", StringComparison.Ordinal)),
                "the dialog was never recorded");
            Assert.True(
                WaitForTrace(tools, sessionId, line => line.Contains("dialog answered:", StringComparison.Ordinal)),
                "the dialog was never answered");

            var trace = tools.GetTrace(sessionId);
            Assert.False(trace.IsError, trace.Text);
            Assert.Contains("dialog appeared: \"Meldung\"", trace.Text, StringComparison.Ordinal);
            Assert.Contains("dialog answered:", trace.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── M7: the trace is readable as evidence ─────────────────────────────────
    [Fact]
    public void M7_GetTrace_ReturnsTheDialogEvidenceAsText()
    {
        var (tools, sessionId) = Launch();
        try
        {
            tools.Invoke(sessionId, "automationId=\"btnMsgBox\"");
            Assert.True(
                WaitForTrace(tools, sessionId, line => line.Contains("dialog answered:", StringComparison.Ordinal)),
                "the dialog was never answered");

            var trace = tools.GetTrace(sessionId, lastLines: 20);

            Assert.False(trace.IsError, trace.Text);
            // The tail is what the agent reads: the dialog history must be in it.
            Assert.Contains("dialog appeared", trace.Text, StringComparison.Ordinal);
            Assert.Contains("dialog answered", trace.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── Extra: the locator grammar is the agent's real interface ──────────────
    [Fact]
    public void Extra_ChainedLocator_ScopesTheSearch()
    {
        var (tools, sessionId) = Launch();
        try
        {
            // "GruppenKnopf" exists in both groups; only the chain makes the target unique.
            var chained = tools.Expect(sessionId, "name=\"Gruppe A\" >> name=\"GruppenKnopf\"", "visible");
            Assert.False(chained.IsError, chained.Text);

            // Unchained, the same name is a strict-mode violation — a structured error, not a guess.
            var ambiguous = tools.Expect(sessionId, "name=\"GruppenKnopf\"", "visible", timeoutMs: 500);
            Assert.True(ambiguous.IsError);
            Assert.Contains("strict mode violation", ambiguous.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    // ── Extra: a malformed locator is reported, never guessed ─────────────────
    [Fact]
    public void Extra_MalformedLocator_IsRejectedWithGuidance()
    {
        var (tools, sessionId) = Launch();
        try
        {
            var result = tools.Click(sessionId, "name=\"Speichern\" nonsense");

            Assert.True(result.IsError);
            Assert.Contains("key=value", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            tools.Dispose(sessionId);
        }
    }

    private static bool WaitForTrace(DeskDxTools tools, string sessionId, Func<string, bool> predicate)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
        {
            var trace = tools.GetTrace(sessionId, lastLines: 200);
            if (!trace.IsError && trace.Text.Split('\n').Any(predicate))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
