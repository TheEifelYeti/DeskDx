using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UiDx.Tests")]

// The MCP server is a thin adapter over the engine, like the tests: it delimits its own call-log
// output per tool call (TraceLog.EventCount) so a tool response carries only the evidence of that
// call. That is a read-only counter — the engine's behaviour and public API stay unchanged.
[assembly: InternalsVisibleTo("UiDx.Mcp")]

// The MCP acceptance tests point SnapshotStore.BaseDirectory at the committed baselines, exactly as
// UiDx.Tests does — the baseline lives next to the engine's tests, not in a build output folder.
[assembly: InternalsVisibleTo("UiDx.Mcp.Tests")]
