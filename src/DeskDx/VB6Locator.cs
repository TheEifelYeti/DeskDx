using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace DeskDx;

/// <summary>
/// Explicit fallback chain for legacy VB6 / WinForms controls. This is deliberately <em>not</em> a
/// magic auto-fallback inside <see cref="Locator"/>: each stage is probed once (polled until the
/// timeout) before the next stage is tried, and the stage that matched is written to the trace.
/// </summary>
/// <remarks>
/// Stages, in order:
/// <list type="number">
///   <item><c>AutomationId</c> — for modern rebuilds that assign stable ids.</item>
///   <item><c>Name + ControlType</c> — the WinForms/VB6 default path.</item>
///   <item><c>Name contains + ControlType</c> — VB6 captions are often truncated.</item>
///   <item><c>LegacyIAccessible</c> scan (DefaultAction / Value) — last resort.</item>
/// </list>
/// If a stage would match more than one element, the chain stops with a strict error instead of
/// silently falling through to the next stage.
/// </remarks>
public static class VB6Locator
{
    /// <summary>Finds a legacy control using the explicit fallback chain.</summary>
    /// <param name="session">The session to search in (its main window is the scope).</param>
    /// <param name="controlType">The expected control type.</param>
    /// <param name="expectedName">The expected caption / accessible name.</param>
    /// <param name="timeout">Timeout per stage. Defaults to <see cref="AppSessionOptions.DefaultTimeout"/>.</param>
    public static AutomationElement Find(
        AppSession session,
        ControlType controlType,
        string expectedName,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);

        var effectiveTimeout = timeout ?? session.DefaultTimeout;
        var description = $"{controlType} \"{expectedName}\"";

        // Stage 1: AutomationId.
        if (TryStage(session, () => session.GetByAutomationId(expectedName).GetByControlType(controlType), effectiveTimeout, out var element))
        {
            Log(session, 1, controlType, element!);
            return element!;
        }

        // Stage 2: Name (exact) + ControlType.
        if (TryStage(session, () => session.GetByName(expectedName).GetByControlType(controlType), effectiveTimeout, out element))
        {
            Log(session, 2, controlType, element!);
            return element!;
        }

        // Stage 3: Name contains + ControlType.
        var contains = FindByContains(session, controlType, expectedName, effectiveTimeout);
        if (contains.Count > 1)
        {
            throw new DeskDxStrictError(
                $"{description} (name contains, stage 3)",
                contains.Select(Format).ToList(),
                session.Trace.ToCallLog());
        }

        if (contains.Count == 1)
        {
            Log(session, 3, controlType, contains[0]);
            return contains[0];
        }

        // Stage 4: LegacyIAccessible scan.
        var legacy = FindByLegacyAccessible(session, controlType, expectedName, effectiveTimeout);
        if (legacy.Count > 1)
        {
            throw new DeskDxStrictError(
                $"{description} (LegacyIAccessible, stage 4)",
                legacy.Select(Format).ToList(),
                session.Trace.ToCallLog());
        }

        if (legacy.Count == 1)
        {
            Log(session, 4, controlType, legacy[0]);
            return legacy[0];
        }

        session.Trace.Phase($"vb6 locator: {description} (timeout {effectiveTimeout.TotalMilliseconds:0}ms)");
        session.Trace.InternalLog("vb6 locator failed: no stage matched");
        throw new DeskDxTimeoutError($"vb6 locator {description}", effectiveTimeout, session.Trace.ToCallLog());
    }

    private static bool TryStage(
        AppSession session,
        Func<Locator> build,
        TimeSpan timeout,
        out AutomationElement? element)
    {
        try
        {
            element = build().Resolve(timeout);
            return true;
        }
        catch (DeskDxTimeoutError)
        {
            element = null;
            return false;
        }
    }

    private static List<AutomationElement> FindByContains(
        AppSession session,
        ControlType controlType,
        string expectedName,
        TimeSpan timeout)
    {
        return Poll(session, timeout, scope => scope
            .FindAllDescendants(
                session.Automation.ConditionFactory.ByControlType(controlType))
            .Where(candidate => ElementDescription.SafeName(candidate)
                .Contains(expectedName, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    private static List<AutomationElement> FindByLegacyAccessible(
        AppSession session,
        ControlType controlType,
        string expectedName,
        TimeSpan timeout)
    {
        return Poll(session, timeout, scope => scope
            .FindAllDescendants(
                session.Automation.ConditionFactory.ByControlType(controlType))
            .Where(candidate => MatchesLegacyAccessible(candidate, expectedName))
            .ToList());
    }

    private static List<AutomationElement> Poll(
        AppSession session,
        TimeSpan timeout,
        Func<AutomationElement, List<AutomationElement>> find)
    {
        var scope = session.MainWindow;
        if (scope is null)
        {
            return [];
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var found = find(scope);
        while (found.Count == 0 && stopwatch.Elapsed < timeout)
        {
            Thread.Sleep(50);
            found = find(scope);
        }

        return found;
    }

    private static bool MatchesLegacyAccessible(AutomationElement element, string expectedName)
    {
        try
        {
            // UIA2 does not support LegacyIAccessible; IsSupported returns false there.
            if (!element.Patterns.LegacyIAccessible.IsSupported)
            {
                return false;
            }

            var legacy = element.Patterns.LegacyIAccessible.Pattern;
            return Contains(legacy.Name.ValueOrDefault)
                   || Contains(legacy.Value.ValueOrDefault)
                   || Contains(legacy.Description.ValueOrDefault)
                   || Contains(legacy.DefaultAction.ValueOrDefault);

            bool Contains(string? candidate)
                => candidate is not null
                   && candidate.Contains(expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Log(AppSession session, int stage, ControlType controlType, AutomationElement element)
        => session.Trace.InternalLog(
            $"vb6 locator stage {stage} matched: {controlType} \"{ElementDescription.SafeName(element)}\"");

    private static string Format(AutomationElement element) => ElementDescription.Describe(element);
}
