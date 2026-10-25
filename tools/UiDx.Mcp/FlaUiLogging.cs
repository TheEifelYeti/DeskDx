using FlaUI.Core.Logging;

namespace UiDx.Mcp;

/// <summary>
/// Keeps FlaUI's logging off the stdio protocol channel.
/// </summary>
/// <remarks>
/// <para>
/// FlaUI ships a global <see cref="Logger.Default"/> that is a <c>ConsoleLogger</c>, and
/// <c>ConsoleLogger</c> writes <c>Info</c>/<c>Warn</c>/<c>Trace</c> to <b>stdout</b>. On a stdio MCP
/// transport stdout <em>is</em> the protocol stream, so a single FlaUI log line — for example
/// "Application failed to exit" from <c>Application.Close()</c> — would inject non-JSON text between
/// two protocol messages and can break the connection.
/// </para>
/// <para>
/// The library itself never writes to the console; this is the one place a dependency does, so it is
/// redirected here: diagnostics stay visible (on stderr, where the host log belongs) and stdout keeps
/// carrying protocol messages only.
/// </para>
/// </remarks>
internal sealed class StderrLogger : LoggerBase
{
    protected override void GatedTrace(string message) => Write(message);

    protected override void GatedDebug(string message) => Write(message);

    protected override void GatedInfo(string message) => Write(message);

    protected override void GatedWarn(string message) => Write(message);

    protected override void GatedError(string message) => Write(message);

    protected override void GatedFatal(string message) => Write(message);

    private static void Write(string message)
    {
        try
        {
            Console.Error.WriteLine("[FlaUI] " + message);
        }
        catch (Exception)
        {
            // A closed stderr must never take the server down.
        }
    }
}

/// <summary>Installs the stderr logger as FlaUI's global default.</summary>
internal static class FlaUiLogging
{
    /// <summary>Routes FlaUI's logging to stderr so stdout stays a clean JSON-RPC channel.</summary>
    internal static void RedirectToStandardError() => Logger.Default = new StderrLogger();
}
