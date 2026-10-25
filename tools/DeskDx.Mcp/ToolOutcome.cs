namespace DeskDx.Mcp;

/// <summary>
/// The result of a DeskDx MCP tool: either a success text the agent reads, or an error text that
/// becomes a structured <c>isError: true</c> tool result.
/// </summary>
/// <remarks>
/// The handlers never throw. An engine failure (gate timeout, strict violation, crash) is a normal
/// outcome of exploring an application — turning it into a value instead of an exception keeps the
/// handlers directly callable from tests and keeps the MCP adapter a pure translation layer.
/// </remarks>
public sealed record ToolOutcome(string Text, bool IsError = false)
{
    /// <summary>A successful result carrying <paramref name="text"/>.</summary>
    public static ToolOutcome Ok(string text) => new(text);

    /// <summary>A failed result; the MCP layer reports it with <c>isError: true</c>.</summary>
    public static ToolOutcome Error(string text) => new(text, IsError: true);
}
