using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using DeskDx;

namespace DeskDx.Mcp.Tests;

/// <summary>
/// M8: the stdio round trip. Starts the real <c>DeskDx.Mcp.exe</c> as a child process, connects an MCP
/// client over its standard input/output and drives a real launch through the protocol.
/// </summary>
/// <remarks>
/// M1–M7 prove the tool behaviour; this one proves the transport wiring — that the server
/// initializes, advertises its tools and executes one against a live application. It is deliberately
/// the only test that pays for process pairing.
/// </remarks>
[Collection("TestTarget")]
public sealed class StdioTransportTests
{
    private readonly TestTargetFixture _fixture;

    public StdioTransportTests(TestTargetFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task M8_StdioServer_InitializesListsToolsAndLaunches()
    {
        var serverPath = ResolveServerPath();

        // A dedicated trace root keeps the smoke test's session out of the default location.
        var traceRoot = Path.Combine(Path.GetTempPath(), "DeskDx.Mcp.Tests.stdio", Guid.NewGuid().ToString("N"));

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "deskdx",
            Command = serverPath,
            EnvironmentVariables = new Dictionary<string, string?> { ["DESKDX_MCP_TRACE_DIR"] = traceRoot },
        });

        await using var client = await McpClient.CreateAsync(transport);

        // ── initialize ─────────────────────────────────────────────────────────
        Assert.NotNull(client.ServerInfo);
        Assert.Equal("deskdx", client.ServerInfo!.Name);

        // ── tools/list ────────────────────────────────────────────────────────
        var tools = await client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
                 {
                     "ui_launch", "ui_attach", "ui_dispose", "ui_snapshot", "ui_match_snapshot",
                     "ui_get_trace", "ui_click", "ui_fill", "ui_select", "ui_invoke", "ui_expect",
                 })
        {
            Assert.Contains(expected, names);
        }

        // ── one real ui_launch over the wire ──────────────────────────────────
        var launch = await client.CallToolAsync(
            "ui_launch",
            new Dictionary<string, object?>
            {
                ["exePath"] = _fixture.ExecutablePath,
                ["startupTimeoutMs"] = 15000,
            });

        Assert.True(launch.IsError is not true, Text(launch));

        var launchText = Text(launch);
        var sessionId = launchText.Split('\n')
            .FirstOrDefault(line => line.StartsWith("sessionId:", StringComparison.Ordinal))?
            ["sessionId:".Length..]
            .Trim();

        Assert.False(string.IsNullOrEmpty(sessionId), $"no sessionId in:\n{launchText}");

        try
        {
            // The trace path from the response must be the configured root — proof the environment
            // variable travelled through the transport.
            Assert.Contains(traceRoot, launchText, StringComparison.Ordinal);

            // And a follow-up call works over the same connection.
            var snapshot = await client.CallToolAsync(
                "ui_snapshot",
                new Dictionary<string, object?> { ["sessionId"] = sessionId });

            Assert.True(snapshot.IsError is not true, Text(snapshot));
            Assert.Contains("btnSave", Text(snapshot), StringComparison.Ordinal);
        }
        finally
        {
            await client.CallToolAsync(
                "ui_dispose",
                new Dictionary<string, object?> { ["sessionId"] = sessionId });
        }
    }

    private static string Text(CallToolResult result)
        => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static string ResolveServerPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var path = Path.Combine(
            TestTargetFixture.RepositoryRoot,
            "tools", "DeskDx.Mcp", "bin", configuration, "net8.0-windows", "DeskDx.Mcp.exe");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"DeskDx.Mcp.exe not found at '{path}'. Build the solution first (dotnet build DeskDx.slnx).",
                path);
        }

        return path;
    }
}
