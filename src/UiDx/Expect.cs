using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;

namespace UiDx;

/// <summary>
/// Entry point for assertions, mirroring Playwright's <c>expect</c>.
/// <c>using static UiDx.Expect;</c> makes <see cref="That"/> directly available.
/// </summary>
/// <example>
/// <code>
/// Expect.That(session.GetByName("Speichern")).ToBeEnabled();
/// </code>
/// </example>
public static class Expect
{
    /// <summary>Starts an assertion for the given locator.</summary>
    public static LocatorAssertions That(Locator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return new LocatorAssertions(locator);
    }
}

/// <summary>
/// Web-first assertions for a <see cref="Locator"/>: every assertion polls until the timeout and is
/// green as soon as the condition holds.
/// </summary>
/// <remarks>
/// <para>
/// The predicates are the <see cref="ActionabilityGate"/> checks, shared rather than copied, so gate
/// and assertions can never disagree about what "visible" or "editable" means.
/// </para>
/// <para>
/// <b>A successful assertion writes nothing.</b> Everything is recorded in a suppression scope and
/// only committed to the trace file when the assertion fails — the log shows the problem, not the
/// functioning (Playwright rule). On failure the caller's call log remains untouched.
/// </para>
/// </remarks>
public sealed class LocatorAssertions
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Locator _locator;

    internal LocatorAssertions(Locator locator) => _locator = locator;

    /// <summary>The locator under assertion.</summary>
    public Locator Locator => _locator;

    /// <summary>Polls until the element resolves and is visible (non-empty rect, not offscreen).</summary>
    public void ToBeVisible(TimeSpan? timeout = null)
        => AssertElementState(
            "ToBeVisible",
            "to be visible",
            ActionabilityGate.IsVisible,
            "element is not visible",
            timeout);

    /// <summary>Polls until the element is gone or invisible. Green immediately when it never existed.</summary>
    public void ToBeHidden(TimeSpan? timeout = null)
        => AssertElementState(
            "ToBeHidden",
            "to be hidden",
            // Only evaluated when an element actually exists; a missing element is handled above.
            element => !ActionabilityGate.IsVisible(element),
            "element is still visible",
            timeout);

    /// <summary>Polls until the element resolves and is enabled (with LegacyIAccessible fallback).</summary>
    public void ToBeEnabled(TimeSpan? timeout = null)
        => AssertElementState(
            "ToBeEnabled",
            "to be enabled",
            ActionabilityGate.IsEnabled,
            "element is not enabled",
            timeout);

    /// <summary>Polls until the element is enabled and editable.</summary>
    public void ToBeEditable(TimeSpan? timeout = null)
        => AssertElementState(
            "ToBeEditable",
            "to be editable",
            ActionabilityGate.IsEditable,
            "element is not editable",
            timeout);

    /// <summary>
    /// Polls until the ValuePattern value matches the normalized expected text.
    /// An element without a ValuePattern fails immediately — that is a programming error, not a state.
    /// </summary>
    public void ToHaveValue(string expected, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        AssertText("ToHaveValue", "to have value", expected, requireValuePattern: true, timeout);
    }

    /// <summary>
    /// Polls until the element's readable text matches: the ValuePattern value when supported,
    /// otherwise the UIA Name (labels, buttons).
    /// </summary>
    public void ToHaveText(string expected, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        AssertText("ToHaveText", "to have text", expected, requireValuePattern: false, timeout);
    }

    // ── The two polling primitives ───────────────────────────────────────────

    /// <summary>
    /// Polls until <paramref name="predicate"/> holds for a resolved element (or, for
    /// <c>ToBeHidden</c>, for a missing element).
    /// </summary>
    private void AssertElementState(
        string assertionName,
        string phaseText,
        Func<AutomationElement, bool> predicate,
        string failureMessage,
        TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? _locator.Session.DefaultTimeout;
        using var suppression = _locator.Session.Trace.SuppressPersistence();
        var mark = _locator.Session.Trace.EventCount;

        _locator.Session.Trace.Phase(
            $"waiting for expect: {Describe()} {phaseText} (timeout {effectiveTimeout.TotalMilliseconds:0}ms)");

        AutomationElement? element = null;
        var stopwatch = Stopwatch.StartNew();
        var succeeded = false;
        while (!succeeded)
        {
            element = TryResolveElement(effectiveTimeout);
            // An element that does not exist cannot fail the visibility predicates, so ToBeHidden is
            // already satisfied; every other predicate keeps polling instead.
            if (element is null)
            {
                if (assertionName == nameof(ToBeHidden))
                {
                    succeeded = true;
                    break;
                }
            }
            else if (predicate(element))
            {
                succeeded = true;
                break;
            }

            if (stopwatch.Elapsed >= effectiveTimeout)
            {
                break;
            }

            Thread.Sleep(PollInterval);
        }

        if (!succeeded)
        {
            // Context first, then the failing check — the log must explain what it hung on.
            if (element is not null)
            {
                _locator.Session.Trace.InternalLog($"locator resolved to {DescribeElement(element)}");
                _locator.Session.Trace.InternalDetail(failureMessage);
                _locator.Session.Trace.InternalDetail(ElementDescription.DescribeState(element));
            }
            else
            {
                // Nothing resolved: the scope children are the context, the check line is the verdict.
                _locator.LogScopeChildrenDump();
                _locator.Session.Trace.InternalDetail(failureMessage);
            }
        }

        _locator.Session.Trace.EndPhase();
        Commit(assertionName, effectiveTimeout, succeeded, mark);
    }

    /// <summary>Text comparison variant: polls until the normalized actual text equals the expectation.</summary>
    private void AssertText(
        string assertionName,
        string phaseText,
        string expected,
        bool requireValuePattern,
        TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? _locator.Session.DefaultTimeout;
        using var suppression = _locator.Session.Trace.SuppressPersistence();
        var mark = _locator.Session.Trace.EventCount;

        _locator.Session.Trace.Phase(
            $"waiting for expect: {Describe()} {phaseText} \"{expected}\" (timeout {effectiveTimeout.TotalMilliseconds:0}ms)");

        AutomationElement? element = TryResolveElement(effectiveTimeout);

        // A missing ValuePattern is a programming error: no polling, immediate failure signal.
        if (element is not null && requireValuePattern && !SupportsValue(element))
        {
            _locator.Session.Trace.InternalLog($"locator resolved to {DescribeElement(element)}");
            _locator.Session.Trace.InternalDetail("element does not support ValuePattern");
            _locator.Session.Trace.EndPhase();
            Commit(assertionName, effectiveTimeout, succeeded: false, mark);
            return;
        }

        string? actual = null;
        var stopwatch = Stopwatch.StartNew();
        var succeeded = false;
        while (!succeeded)
        {
            element = TryResolveElement(effectiveTimeout);
            if (element is not null)
            {
                actual = ReadText(element, requireValuePattern);
                if (actual is not null && Normalize(actual) == Normalize(expected))
                {
                    succeeded = true;
                    break;
                }
            }

            if (stopwatch.Elapsed >= effectiveTimeout)
            {
                break;
            }

            Thread.Sleep(PollInterval);
        }

        if (!succeeded)
        {
            if (element is not null)
            {
                _locator.Session.Trace.InternalLog($"locator resolved to {DescribeElement(element)}");
                // Normalization drives the comparison, transparency drives the evidence.
                _locator.Session.Trace.InternalDetail(
                    $"actual: \"{Escape(actual)}\", expected: \"{Escape(expected)}\"");
                _locator.Session.Trace.InternalDetail(ElementDescription.DescribeState(element));
            }
            else
            {
                _locator.Session.Trace.InternalDetail("element was not found");
                _locator.LogScopeChildrenDump();
            }
        }

        _locator.Session.Trace.EndPhase();
        Commit(assertionName, effectiveTimeout, succeeded, mark);
    }

    /// <summary>
    /// Commits or discards the buffered evidence. On failure the failure lines are copied out of the
    /// buffer and the buffer is dropped, so the caller's call log stays untouched and the thrown
    /// error carries the evidence instead.
    /// </summary>
    private void Commit(string assertionName, TimeSpan effectiveTimeout, bool succeeded, int mark)
    {
        if (succeeded)
        {
            // Nothing at all reaches the trace file or the call log.
            _locator.Session.Trace.TruncateTo(mark);
            return;
        }

        var failureLog = _locator.Session.Trace.ToCallLog().Skip(mark).ToArray();
        _locator.Session.Trace.TruncateTo(mark);
        throw new UiDxExpectError(Describe(), assertionName, effectiveTimeout, failureLog);
    }

    /// <summary>
    /// Resolves silently (no call log lines) and returns null when the element is missing or
    /// ambiguous. The assertions own the logging so the log stays free of resolve noise.
    /// </summary>
    private AutomationElement? TryResolveElement(TimeSpan timeout)
    {
        try
        {
            return _locator.ResolveSilently(timeout);
        }
        catch (UiDxTimeoutError)
        {
            // Missing element: ToBeHidden is green, every other predicate keeps polling.
            return null;
        }
    }

    private static string? ReadText(AutomationElement element, bool requireValuePattern)
        => requireValuePattern || SupportsValue(element)
            ? ReadValue(element)
            : ElementDescription.SafeName(element);

    private static bool SupportsValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ReadValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.Pattern.Value.ValueOrDefault;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribeElement(AutomationElement element) => ElementDescription.Describe(element);

    /// <summary>Trim + collapse internal whitespace + normalize CRLF/CR to LF (Playwright rule).</summary>
    private static string Normalize(string value)
        => Regex.Replace(
            value.Replace("\r\n", "\n").Replace('\r', '\n').Trim(),
            "[ \t\n]+",
            " ");

    /// <summary>Renders control characters visible in the failure message.</summary>
    private static string Escape(string? value)
        => value is null ? string.Empty : value.Replace("\r", "\\r").Replace("\n", "\\n");

    private string Describe() => $"locator({_locator})";
}
