namespace UiDx;

/// <summary>
/// Error thrown when an operation does not complete within <see cref="AppSessionOptions.DefaultTimeout"/>.
/// <see cref="Exception.Message"/> ends with "Timeout &lt;ms&gt;ms exceeded." followed by the formatted call log.
/// </summary>
public sealed class UiDxTimeoutError : Exception
{
    public UiDxTimeoutError(string message, TimeSpan timeout, IReadOnlyList<string> callLog)
        : base(Format(message, timeout, callLog))
    {
        Timeout = timeout;
        CallLog = callLog;
    }

    public UiDxTimeoutError(string message, TimeSpan timeout, IReadOnlyList<string> callLog, Exception? innerException)
        : base(Format(message, timeout, callLog), innerException)
    {
        Timeout = timeout;
        CallLog = callLog;
    }

    /// <summary>The timeout that was exceeded.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The rendered call log that was attached to the error message.</summary>
    public IReadOnlyList<string> CallLog { get; }

    private static string Format(string message, TimeSpan timeout, IReadOnlyList<string> callLog)
        => $"{message}: Timeout {timeout.TotalMilliseconds:0}ms exceeded." + UiDxErrorFormat.CallLog(callLog);
}

/// <summary>
/// Error thrown when the session's process already exited and an operation is attempted.
/// <see cref="Exception.Message"/> contains the exit code and the trace path.
/// </summary>
public sealed class UiDxCrashError : Exception
{
    public UiDxCrashError(string message, int exitCode, string? tracePath, IReadOnlyList<string> callLog)
        : base(
            $"{message}: Process has exited (exit code {exitCode}). Trace: {tracePath}"
            + UiDxErrorFormat.CallLog(callLog))
    {
        ExitCode = exitCode;
        TracePath = tracePath;
        CallLog = callLog;
    }

    /// <summary>The exit code reported by the crashed process.</summary>
    public int ExitCode { get; }

    /// <summary>The trace directory of the crashed session.</summary>
    public string? TracePath { get; }

    /// <summary>The rendered call log that was attached to the error message.</summary>
    public IReadOnlyList<string> CallLog { get; }
}

/// <summary>
/// Error thrown when a locator resolves to more than one element and strict mode is on.
/// <see cref="Exception.Message"/> starts with "strict mode violation:" and lists every match.
/// </summary>
public sealed class UiDxStrictError : Exception
{
    public UiDxStrictError(string locatorDescription, IReadOnlyList<string> matches, IReadOnlyList<string> callLog)
        : base(Format(locatorDescription, matches) + UiDxErrorFormat.CallLog(callLog))
    {
        LocatorDescription = locatorDescription;
        Matches = matches;
        CallLog = callLog;
    }

    /// <summary>The description of the locator that matched more than one element.</summary>
    public string LocatorDescription { get; }

    /// <summary>The rendered description of every matching element.</summary>
    public IReadOnlyList<string> Matches { get; }

    /// <summary>The rendered call log that was attached to the error message.</summary>
    public IReadOnlyList<string> CallLog { get; }

    private static string Format(string locatorDescription, IReadOnlyList<string> matches)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("strict mode violation: ")
            .Append(locatorDescription)
            .Append(" resolved to ")
            .Append(matches.Count)
            .Append(" elements:");
        foreach (var match in matches)
        {
            builder.Append('\n').Append("  ").Append(match);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Error thrown when an assertion fails. This is a test result with evidence, deliberately not a
/// recycled <see cref="UiDxTimeoutError"/>: <see cref="Exception.Message"/> starts with
/// <c>expect(...).ToBeEnabled() failed after 5000ms</c> followed by the call log.
/// </summary>
public sealed class UiDxExpectError : Exception
{
    public UiDxExpectError(
        string expectedDescription,
        string assertionName,
        TimeSpan timeout,
        IReadOnlyList<string> callLog,
        string assertionArguments = "")
        : base(
            $"expect({expectedDescription}).{assertionName}({assertionArguments}) failed after {timeout.TotalMilliseconds:0}ms"
            + UiDxErrorFormat.CallLog(callLog))
    {
        ExpectedDescription = expectedDescription;
        AssertionName = assertionName;
        Timeout = timeout;
        CallLog = callLog;
    }

    /// <summary>The rendered locator description, e.g. <c>locator(name="btnDelayed")</c>.</summary>
    public string ExpectedDescription { get; }

    /// <summary>The assertion that failed, e.g. <c>ToBeEnabled</c>.</summary>
    public string AssertionName { get; }

    /// <summary>The timeout that elapsed before the assertion gave up.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The rendered call log that was attached to the error message.</summary>
    public IReadOnlyList<string> CallLog { get; }
}

/// <summary>Shared call log rendering for the UiDx error types.</summary>
internal static class UiDxErrorFormat
{
    /// <summary>Renders the Schritt 0 call log block that is appended to error messages.</summary>
    public static string CallLog(IReadOnlyList<string> callLog)
    {
        if (callLog is null || callLog.Count == 0)
        {
            return "\nCall log:\n  (empty)";
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("\nCall log:");
        foreach (var line in callLog)
        {
            // ToCallLog() already renders the full indentation (base indent = 2 spaces).
            builder.Append('\n').Append(line);
        }

        return builder.ToString();
    }
}
