using UiDx.Mcp;

// Evidence run for step 7: the agent story, executed against the TestTarget through the real MCP
// tool handlers. The transcript this prints is the README fragment — six tool calls that verify a
// form, written by an agent that never sees the screen.
//
// Usage:
//   dotnet run --project tools/UiDx.Mcp.Demo -- <outputDirectory> <TestTarget.exe>

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/UiDx.Mcp.Demo -- <outputDirectory> <TestTarget.exe>");
    return 1;
}

var outputDirectory = Path.GetFullPath(args[0]);
var testTargetPath = Path.GetFullPath(args[1]);

if (!File.Exists(testTargetPath))
{
    Console.Error.WriteLine($"TestTarget not found: {testTargetPath}");
    return 1;
}

Directory.CreateDirectory(outputDirectory);

// The baseline the story verifies against: a reviewed capture, written explicitly (never silently).
var baselineName = "demo-main-form";
var baselineDirectory = Path.Combine(outputDirectory, "snapshots");
Directory.CreateDirectory(baselineDirectory);

// UseSnapshotDirectory takes the directory that the snapshots/ folder is resolved against.
UiDxTools.UseSnapshotDirectory(outputDirectory);

var traceRoot = Path.Combine(outputDirectory, "traces");
var registry = new SessionRegistry();
var tools = new UiDxTools(registry, new TraceRoot(traceRoot));

var transcript = new List<string>();
var failures = 0;

void Call(string tool, string arguments, ToolOutcome outcome)
{
    transcript.Add($"── {tool} {arguments}");
    foreach (var line in outcome.Text.Split('\n'))
    {
        transcript.Add("   " + line);
    }

    transcript.Add(string.Empty);
    if (outcome.IsError)
    {
        failures++;
    }
}

var launch = tools.Launch(testTargetPath, startupTimeoutMs: 15000, expectWindowTitle: "UiDx TestTarget");
Call("ui_launch", $"exePath=\"{testTargetPath}\" expectWindowTitle=\"UiDx TestTarget\"", launch);

var sessionId = launch.Text.Split('\n')
    .FirstOrDefault(line => line.StartsWith("sessionId:", StringComparison.Ordinal))?["sessionId:".Length..]
    .Trim();

if (string.IsNullOrEmpty(sessionId))
{
    Console.Error.WriteLine("launch did not return a sessionId; aborting");
    File.WriteAllLines(Path.Combine(outputDirectory, "agent-story.md"), transcript);
    return 1;
}

try
{
    // ── 1. Discover what to address ───────────────────────────────────────────
    var snapshot = tools.Snapshot(sessionId, locator: "name=\"UiDx TestTarget\"", excludeIds: ["lblStatus", "txtDisabled"]);
    Call("ui_snapshot", "sessionId=\"...\" locator=\"name=\\\"UiDx TestTarget\\\"\"", snapshot);

    // ── 2. Establish the baseline the story verifies (the reviewed first capture) ─
    var baseline = tools.MatchSnapshot(
        sessionId,
        locator: null,
        baselineName,
        update: true,
        excludeIds: ["lblStatus", "txtDisabled"]);
    Call("ui_match_snapshot", $"baselineName=\"{baselineName}\" update=true", baseline);

    // ── 3. Drive the form ────────────────────────────────────────────────────
    Call(
        "ui_fill",
        "locator=\"automationId=\\\"txtKunde\\\"\" text=\"Müller GmbH\"",
        tools.Fill(sessionId, "automationId=\"txtKunde\"", "Müller GmbH"));

    Call(
        "ui_click",
        "locator=\"name=\\\"Speichern\\\"\"",
        tools.Click(sessionId, "name=\"Speichern\""));

    // ── 4. Assert the outcome ────────────────────────────────────────────────
    Call(
        "ui_expect",
        "locator=\"automationId=\\\"btnSave\\\"\" assertion=\"visible\"",
        tools.Expect(sessionId, "automationId=\"btnSave\"", "visible"));

    // ── 5. Verify the structure did not change ───────────────────────────────
    Call(
        "ui_match_snapshot",
        $"baselineName=\"{baselineName}\"",
        tools.MatchSnapshot(sessionId, locator: null, baselineName, excludeIds: ["lblStatus", "txtDisabled"]));

    // ── 6. A migration regression shows up as a diff, not a paragraph ────────
    tools.Invoke(sessionId, "automationId=\"btnCover\"");
    Call(
        "ui_match_snapshot",
        $"baselineName=\"{baselineName}\" (after a simulated migration regression)",
        tools.MatchSnapshot(sessionId, locator: null, baselineName, excludeIds: ["lblStatus", "txtDisabled"]));

    // ── 7. The dialog watcher keeps the agent alive; the trace shows why ─────
    Call("ui_invoke", "locator=\"automationId=\\\"btnMsgBox\\\"\"", tools.Invoke(sessionId, "automationId=\"btnMsgBox\""));
    Thread.Sleep(1500);
    Call("ui_get_trace", "sessionId=\"...\" lastLines=15", tools.GetTrace(sessionId, lastLines: 15));
}
finally
{
    Call("ui_dispose", "sessionId=\"...\"", tools.Dispose(sessionId));
    registry.Dispose();
}

var story = Path.Combine(outputDirectory, "agent-story.md");
File.WriteAllLines(
    story,
    [
        "# UiDx MCP server — agent story evidence",
        string.Empty,
        "Six-plus tool calls that verify a form after a migration module, with no human and no screen.",
        $"TestTarget: `{testTargetPath}`",
        $"Trace root:  `{traceRoot}`",
        $"Baseline:    `{Path.Combine(baselineDirectory, baselineName + ".uia.txt")}`",
        string.Empty,
        "```",
        .. transcript,
        "```",
    ]);

Console.WriteLine($"transcript: {story}");
Console.WriteLine($"traces:     {traceRoot}");
Console.WriteLine($"baseline:   {Path.Combine(baselineDirectory, baselineName + ".uia.txt")}");
Console.WriteLine(failures == 0
    ? "all tool calls succeeded"
    : $"{failures} tool call(s) returned an error (see the transcript)");

return 0;
