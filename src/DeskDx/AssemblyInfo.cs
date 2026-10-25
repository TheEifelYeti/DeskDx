using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DeskDx.Tests")]

// The MCP server is a thin adapter over the engine, like the tests: it delimits its own call-log
// output per tool call (TraceLog.EventCount) so a tool response carries only the evidence of that
// call. That is a read-only counter — the engine's behaviour and public API stay unchanged.
[assembly: InternalsVisibleTo("DeskDx.Mcp")]

// The MCP acceptance tests point SnapshotStore.BaseDirectory at the committed baselines, exactly as
// DeskDx.Tests does — the baseline lives next to the engine's tests, not in a build output folder.
[assembly: InternalsVisibleTo("DeskDx.Mcp.Tests")]
