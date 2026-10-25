using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace DeskDx;

/// <summary>
/// The actionability gate: before an action runs, the target must satisfy the checks required for
/// that action. Every check is polled with an increasing backoff; a check that fails is written to
/// the call log (a check that is already green on the first poll is not logged).
/// </summary>
/// <remarks>
/// <para>Check implementations follow the Schritt 0 decision record verbatim:</para>
/// <list type="bullet">
///   <item><b>Visible</b>: <c>BoundingRectangle</c> non-empty and <c>IsOffscreen == false</c>.</item>
///   <item><b>Stable</b>: <c>BoundingRectangle</c> identical over two consecutive poll cycles.</item>
///   <item><b>Enabled</b>: <c>IsEnabled</c>, with a <c>LegacyIAccessible.State</c> fallback that must
///     not contain <c>STATE_SYSTEM_UNAVAILABLE</c>.</item>
///   <item><b>Editable</b>: ValuePattern supported, not read-only, and enabled.</item>
///   <item><b>ReceivesEvents</b>: the element hit at the centre of the bounding rectangle must be the
///     target or inside the target's subtree (parent climb).</item>
/// </list>
/// </remarks>
internal sealed class ActionabilityGate
{
    // Schritt 0: "increasing amount of time before the next retry", capped at 500 ms.
    private static readonly int[] BackoffMilliseconds = [50, 100, 200, 400, 500];

    private readonly AppSession _session;
    private readonly TraceLog _trace;

    internal ActionabilityGate(AppSession session)
    {
        _session = session;
        _trace = session.Trace;
    }

    /// <summary>Runs the given checks for an element, polling with increasing backoff.</summary>
    /// <returns>The element that finally satisfied every check.</returns>
    /// <exception cref="DeskDxTimeoutError">A check did not turn green within the timeout.</exception>
    internal AutomationElement Run(
        Func<AutomationElement?> resolve,
        IReadOnlyList<ActionabilityCheck> checks,
        TimeSpan timeout,
        string description)
    {
        var element = resolve() ?? throw new DeskDxTimeoutError(description, timeout, _trace.ToCallLog());

        // The first condition of "stable" is the baseline for the next poll cycle.
        Rectangle? previousBounds = null;
        var stopwatch = Stopwatch.StartNew();
        var backoffIndex = 0;

        while (true)
        {
            element = resolve() ?? element;

            var failure = Evaluate(element, checks, ref previousBounds);
            if (failure is null)
            {
                return element;
            }

            // Log every failing check per poll cycle (Schritt 0: the log shows what it hung on).
            // A check that is already green on the first poll is never logged.
            _trace.InternalDetail(failure.Value.Message);

            if (stopwatch.Elapsed >= timeout)
            {
                AddStateDump(element);
                throw new DeskDxTimeoutError(description, timeout, _trace.ToCallLog());
            }

            var delay = BackoffMilliseconds[Math.Min(backoffIndex, BackoffMilliseconds.Length - 1)];
            backoffIndex++;
            var remaining = timeout - stopwatch.Elapsed;
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(delay, Math.Max(remaining.TotalMilliseconds, 1))));
        }
    }

    /// <summary>Returns the first failing check, or null when every check is green.</summary>
    private GateFailure? Evaluate(
        AutomationElement element,
        IReadOnlyList<ActionabilityCheck> checks,
        ref Rectangle? previousBounds)
    {
        foreach (var check in checks)
        {
            var failure = check switch
            {
                ActionabilityCheck.Visible => IsVisible(element) ? null : new GateFailure(check, "element is not visible"),
                ActionabilityCheck.Enabled => IsEnabled(element) ? null : new GateFailure(check, "element is not enabled"),
                ActionabilityCheck.Editable => IsEditable(element) ? null : new GateFailure(check, "element is not editable"),
                ActionabilityCheck.Stable => CheckStable(element, ref previousBounds),
                ActionabilityCheck.ReceivesEvents => CheckReceivesEvents(element),
                _ => throw new ArgumentOutOfRangeException(nameof(checks), check, "unknown check"),
            };

            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    // ── Visible ──────────────────────────────────────────────────────────────
    internal static bool IsVisible(AutomationElement element)
        => ElementDescription.TryGetBounds(element) is { Width: > 0, Height: > 0 }
           && !ElementDescription.SafeIsOffscreen(element);

    // ── Stable: bounds identical over two consecutive poll cycles ────────────
    private GateFailure? CheckStable(AutomationElement element, ref Rectangle? previousBounds)
    {
        var bounds = ElementDescription.TryGetBounds(element);
        if (bounds is null)
        {
            previousBounds = null;
            return new GateFailure(ActionabilityCheck.Stable, "element is not stable");
        }

        // The first observation only establishes the baseline: a single sample cannot violate
        // stability, so it is not reported (Schritt 0: only failing checks are logged).
        if (previousBounds is not { } previous)
        {
            previousBounds = bounds;
            return null;
        }

        previousBounds = bounds;
        return previous.Equals(bounds.Value)
            ? null
            : new GateFailure(ActionabilityCheck.Stable, "element is not stable");
    }

    // ── Enabled (with LegacyIAccessible fallback) ────────────────────────────
    internal static bool IsEnabled(AutomationElement element)
    {
        try
        {
            if (element.IsEnabled)
            {
                return true;
            }
        }
        catch (Exception)
        {
            // Fall through to the legacy check.
        }

        // Alt-Control fallback: STATE_SYSTEM_UNAVAILABLE must be absent.
        try
        {
            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                var state = element.Patterns.LegacyIAccessible.Pattern.State.ValueOrDefault;
                return (state & AccessibilityState.STATE_SYSTEM_UNAVAILABLE) == 0;
            }
        }
        catch (Exception)
        {
            // Treat an unreadable state as not enabled.
        }

        return false;
    }

    // ── Editable (Fill) ──────────────────────────────────────────────────────
    internal static bool IsEditable(AutomationElement element)
    {
        if (!IsEnabled(element))
        {
            return false;
        }

        try
        {
            if (!element.Patterns.Value.IsSupported)
            {
                return false;
            }

            return !element.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── ReceivesEvents (Click) ───────────────────────────────────────────────
    private GateFailure? CheckReceivesEvents(AutomationElement element)
    {
        var bounds = ElementDescription.TryGetBounds(element);
        if (bounds is null || bounds.Value.Width == 0 || bounds.Value.Height == 0)
        {
            return new GateFailure(ActionabilityCheck.ReceivesEvents, "element was covered by <unknown>");
        }

        var centre = new Point(
            bounds.Value.Left + (bounds.Value.Width / 2),
            bounds.Value.Top + (bounds.Value.Height / 2));

        AutomationElement? hit;
        try
        {
            hit = _session.Automation.FromPoint(centre);
        }
        catch (Exception)
        {
            return new GateFailure(ActionabilityCheck.ReceivesEvents, "element was covered by <unknown>");
        }

        if (hit is null)
        {
            return new GateFailure(ActionabilityCheck.ReceivesEvents, "element was covered by <unknown>");
        }

        if (IsSameOrAncestor(hit, element))
        {
            return null;
        }

        return new GateFailure(
            ActionabilityCheck.ReceivesEvents,
            $"element was covered by {ElementDescription.Describe(hit)}");
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the target or an ancestor of it. Playwright checks the
    /// other direction (the hit element is the target or inside its subtree); both directions are
    /// accepted here because the native provider can report the hit either as the child or the parent.
    /// </summary>
    private static bool IsSameOrAncestor(AutomationElement candidate, AutomationElement target)
    {
        if (SafeEquals(candidate, target))
        {
            return true;
        }

        // Candidate inside the target's subtree?
        if (IsDescendantOf(candidate, target))
        {
            return true;
        }

        // Candidate is an ancestor of the target (the provider returned the container)?
        return IsDescendantOf(target, candidate);
    }

    private static bool IsDescendantOf(AutomationElement element, AutomationElement potentialAncestor)
    {
        var current = SafeParent(element);
        var depth = 0;
        while (current is not null && depth < 50)
        {
            if (SafeEquals(current, potentialAncestor))
            {
                return true;
            }

            current = SafeParent(current);
            depth++;
        }

        return false;
    }

    private static AutomationElement? SafeParent(AutomationElement element)
    {
        try
        {
            return element.Parent;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SafeEquals(AutomationElement left, AutomationElement right)
    {
        try
        {
            return left.Equals(right);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── State dump on gate timeout ───────────────────────────────────────────
    private void AddStateDump(AutomationElement element)
        => _trace.InternalDetail(ElementDescription.DescribeState(element));

    private readonly record struct GateFailure(ActionabilityCheck Check, string Message);
}

/// <summary>The actionability checks of the Schritt 0 matrix.</summary>
internal enum ActionabilityCheck
{
    Visible,
    Stable,
    ReceivesEvents,
    Enabled,
    Editable,
}
