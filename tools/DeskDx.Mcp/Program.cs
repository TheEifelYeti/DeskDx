using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using DeskDx.Mcp;

// Entry point of the DeskDx MCP server — a thin stdio adapter over DeskDxTools.
//
// The tool behaviour lives in DeskDxTools (in-proc testable); this file only translates MCP requests
// into calls and results back. Nothing here may write to stdout — stdout is the protocol channel, so
// every diagnostic goes to stderr through the host's logger.
return await McpHost.RunAsync(args).ConfigureAwait(false);

internal static class McpHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        // Before anything else: FlaUI's global logger writes to stdout, which is the protocol channel.
        FlaUiLogging.RedirectToStandardError();

        // Baselines live next to the application's tests, which is not where the host starts the
        // server — so the location must be configured, never guessed.
        var snapshotDirectory = Environment.GetEnvironmentVariable(DeskDxTools.SnapshotDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            DeskDxTools.UseSnapshotDirectory(snapshotDirectory);
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Stdio transport: the protocol owns stdout, so log lines must go to stderr.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        var registry = new SessionRegistry();
        var tools = new DeskDxTools(registry);

        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(tools);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "deskdx", Version = "0.1.0" };
                options.ServerInstructions =
                    "DeskDx drives real Windows desktop applications (WinForms/VB6/MDI) for migration "
                    + "work. Locators are the identification: read a snapshot line like "
                    + "`Button \"Speichern\" [AutomationId=btnSave]` and build `name=\"Speichern\"` or "
                    + "`automationId=\"btnSave\"` from it; chain with `>>` to scope a search to a "
                    + "subtree. Actions go through an actionability gate, so a failure tells you why "
                    + "(not visible, covered, disabled) instead of silently doing nothing. Modal "
                    + "dialogs are answered automatically and recorded in the trace; use "
                    + "`ui_get_trace` to read them.";
            })
            .WithStdioServerTransport()
            .WithListToolsHandler((request, cancellationToken) => ValueTask.FromResult(
                new ListToolsResult { Tools = ToolCatalog.Define() }))
            .WithCallToolHandler((request, cancellationToken) => ValueTask.FromResult(
                CallTool(tools, request)));

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Dispatches a tool call to <see cref="DeskDxTools"/> by name.</summary>
    private static CallToolResult CallTool(DeskDxTools tools, RequestContext<CallToolRequestParams> request)
    {
        var name = request.Params?.Name ?? string.Empty;
        var arguments = request.Params?.Arguments;

        var outcome = name switch
        {
            // Lifecycle
            "ui_launch" => tools.Launch(
                RequiredString(arguments, "exePath"),
                OptionalString(arguments, "args"),
                OptionalInt(arguments, "startupTimeoutMs"),
                OptionalString(arguments, "expectWindowTitle")),
            "ui_attach" => tools.Attach(RequiredString(arguments, "processName")),
            "ui_dispose" => tools.Dispose(RequireSessionId(arguments)),

            // Observation
            "ui_snapshot" => tools.Snapshot(
                RequireSessionId(arguments),
                OptionalString(arguments, "locator"),
                OptionalBool(arguments, "includeState"),
                OptionalStringArray(arguments, "excludeIds")),
            "ui_match_snapshot" => tools.MatchSnapshot(
                RequireSessionId(arguments),
                OptionalString(arguments, "locator"),
                RequiredString(arguments, "baselineName"),
                OptionalBool(arguments, "update"),
                OptionalStringArray(arguments, "excludeIds")),
            "ui_get_trace" => tools.GetTrace(RequireSessionId(arguments), OptionalInt(arguments, "lastLines")),

            // Actions
            "ui_click" => tools.Click(
                RequireSessionId(arguments),
                RequiredString(arguments, "locator"),
                OptionalBool(arguments, "force"),
                OptionalInt(arguments, "timeoutMs")),
            "ui_fill" => tools.Fill(
                RequireSessionId(arguments),
                RequiredString(arguments, "locator"),
                RequiredString(arguments, "text"),
                OptionalInt(arguments, "timeoutMs")),
            "ui_select" => tools.Select(
                RequireSessionId(arguments),
                RequiredString(arguments, "locator"),
                RequiredString(arguments, "item"),
                OptionalInt(arguments, "timeoutMs")),
            "ui_invoke" => tools.Invoke(
                RequireSessionId(arguments),
                RequiredString(arguments, "locator"),
                OptionalInt(arguments, "timeoutMs")),
            "ui_expect" => tools.Expect(
                RequireSessionId(arguments),
                RequiredString(arguments, "locator"),
                RequiredString(arguments, "assertion"),
                OptionalString(arguments, "expected"),
                OptionalInt(arguments, "timeoutMs")),

            _ => ToolOutcome.Error(
                $"unknown tool \"{name}\"; available: ui_launch, ui_attach, ui_dispose, ui_snapshot, "
                + "ui_match_snapshot, ui_get_trace, ui_click, ui_fill, ui_select, ui_invoke, ui_expect"),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = outcome.Text }],
            IsError = outcome.IsError ? true : null,
        };
    }

    // ── Argument helpers ─────────────────────────────────────────────────────
    // A missing required argument is a malformed request, not a tool failure, so these raise
    // McpProtocolException (JSON-RPC InvalidParams) rather than producing an isError tool result.

    private static string RequireSessionId(IDictionary<string, JsonElement>? arguments)
        => OptionalString(arguments, "sessionId")
           ?? throw new McpProtocolException("missing required argument \"sessionId\"", McpErrorCode.InvalidParams);

    private static string RequiredString(IDictionary<string, JsonElement>? arguments, string key)
        => OptionalString(arguments, key)
           ?? throw new McpProtocolException($"missing required argument \"{key}\"", McpErrorCode.InvalidParams);

    private static string? OptionalString(IDictionary<string, JsonElement>? arguments, string key)
    {
        if (!TryRead(arguments, key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? OptionalInt(IDictionary<string, JsonElement>? arguments, string key)
        => TryRead(arguments, key, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? OptionalBool(IDictionary<string, JsonElement>? arguments, string key)
    {
        if (!TryRead(arguments, key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string[]? OptionalStringArray(IDictionary<string, JsonElement>? arguments, string key)
    {
        if (!TryRead(arguments, key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)];
    }

    private static bool TryRead(IDictionary<string, JsonElement>? arguments, string key, out JsonElement value)
    {
        value = default;
        return arguments is not null && arguments.TryGetValue(key, out value);
    }
}
