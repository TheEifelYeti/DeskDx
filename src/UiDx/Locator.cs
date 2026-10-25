using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;

namespace UiDx;

/// <summary>
/// Describes a UI element lazily, exactly like a Playwright locator: the locator is the description,
/// resolution happens when <see cref="Resolve(TimeSpan?)"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// A locator created from an <see cref="AppSession"/> searches the main window of that session
/// (not the whole desktop tree). A locator created from an <see cref="AutomationElement"/> (chaining)
/// searches only the subtree of that element.
/// </para>
/// <para>
/// <b>Strict mode</b> is on by default: resolution polls until the timeout and requires exactly one
/// match. Zero matches keep polling; more than one match aborts immediately with
/// <see cref="UiDxStrictError"/>. Call <see cref="Nth"/> to disable strict mode for one locator and
/// take an explicit index.
/// </para>
/// </remarks>
public sealed class Locator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly AppSession _session;
    private readonly Locator? _parentScope;
    private readonly AutomationElement? _scopeElement;
    private readonly List<LocatorCondition> _conditions;
    private readonly int? _index;

    internal Locator(AppSession session)
        : this(session, scopeElement: null, parentScope: null, new List<LocatorCondition>(), index: null)
    {
    }

    private Locator(
        AppSession session,
        AutomationElement? scopeElement,
        Locator? parentScope,
        List<LocatorCondition> conditions,
        int? index)
    {
        _session = session;
        _scopeElement = scopeElement;
        _parentScope = parentScope;
        _conditions = conditions;
        _index = index;
    }

    /// <summary>The session this locator belongs to.</summary>
    public AppSession Session => _session;

    /// <summary>
    /// Adds an AutomationId condition. When the locator already describes an element, this returns a
    /// nested locator that searches only the subtree of the described element (chaining), e.g.
    /// <c>session.GetByName("Panel").GetByAutomationId("inner")</c>.
    /// </summary>
    public Locator GetByAutomationId(string automationId)
        => AddNameOrIdCondition(
            $"AutomationId=\"{automationId}\"",
            factory => factory.ByAutomationId(automationId),
            LocatorConditionKind.AutomationId,
            automationId);

    /// <summary>
    /// Adds a UIA Name condition (caption or AccessibleName). When the locator already describes an
    /// element, this returns a nested locator that searches only the subtree of that element.
    /// </summary>
    public Locator GetByName(string name)
        => AddNameOrIdCondition($"name=\"{name}\"", factory => factory.ByName(name), LocatorConditionKind.Other, name);

    /// <summary>
    /// Adds a control type filter, AND-combined with the conditions of the current locator. This is a
    /// filter (not a chain), so <c>GetByName(x).GetByControlType(Button)</c> matches one element.
    /// </summary>
    public Locator GetByControlType(ControlType type)
        => WithCondition($"ControlType={type}", factory => factory.ByControlType(type), LocatorConditionKind.Other, type.ToString());

    /// <summary>
    /// Disables strict mode for this locator and takes the match at <paramref name="index"/> (0-based).
    /// </summary>
    public Locator Nth(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "index must be >= 0");
        }

        return new Locator(_session, _scopeElement, _parentScope, [.. _conditions], index);
    }

    /// <summary>Resolves to a single element or throws if none / several match.</summary>
    public AutomationElement Resolve(TimeSpan? timeout = null) => ResolveCore(timeout);

    /// <summary>
    /// Resolves without writing a phase to the call log. Used by the actionability gate and the
    /// assertions, which have already logged their phase and only need a fast element lookup per poll.
    /// </summary>
    /// <param name="timeout">
    /// Probe timeout. Defaults to a single poll interval: the callers own the retry loop and the
    /// timeout, so a silent resolve must never block for the full assertion timeout.
    /// </param>
    internal AutomationElement ResolveSilently(TimeSpan? timeout = null)
        => ResolveCore(timeout ?? PollInterval, logPhase: false);

    /// <summary>Resolves to a single element of type <typeparamref name="T"/>.</summary>
    public T Resolve<T>(TimeSpan? timeout = null) where T : AutomationElement
    {
        var element = ResolveCore(timeout);
        return element.As<T>();
    }

    /// <summary>Resolves without throwing: returns false when nothing matched within the timeout.</summary>
    public bool TryResolve(out AutomationElement? element, TimeSpan? timeout = null)
    {
        try
        {
            element = ResolveCore(timeout);
            return true;
        }
        catch (UiDxTimeoutError)
        {
            element = null;
            return false;
        }
    }

    /// <summary>Renders the locator description used in the call log and error messages.</summary>
    public override string ToString()
        => BuildDescription();

    // ── UIA snapshots ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the subtree of the resolved scope element as canonical snapshot text: two spaces of
    /// indentation per level, and no state, position or handle information unless
    /// <see cref="UiaSnapshotOptions.IncludeState"/> is set.
    /// </summary>
    public string ToUiaSnapshot(UiaSnapshotOptions? options = null)
    {
        _session.EnsureAlive();
        return SnapshotText(options ?? new UiaSnapshotOptions());
    }

    /// <summary>
    /// Compares the snapshot of this locator against the baseline <c>snapshots/&lt;baselineName&gt;.uia.txt</c>,
    /// polling until they match or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// A missing baseline fails with a hint; it is only created when <c>UIDX_SNAPSHOT_UPDATE=1</c> is
    /// set. A mismatch writes <c>.actual.txt</c> and <c>.diff</c> into
    /// <c>snapshots/__mismatches__/</c> and throws a <see cref="UiDxExpectError"/> carrying the diff.
    /// </remarks>
    public void ToMatchUiaSnapshot(string baselineName, UiaSnapshotOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineName);

        var effectiveOptions = options ?? new UiaSnapshotOptions();
        var effectiveTimeout = effectiveOptions.Timeout ?? _session.DefaultTimeout;

        _session.EnsureAlive();
        _session.Trace.Phase(
            $"waiting for expect: locator({BuildDescription()}) to match uia snapshot \"{baselineName}\" (timeout {effectiveTimeout.TotalMilliseconds:0}ms)");
        try
        {
            // Update mode writes the baseline and passes — never a silent create without the flag.
            if (SnapshotStore.IsUpdateMode)
            {
                SnapshotStore.WriteBaseline(baselineName, SnapshotText(effectiveOptions));
                return;
            }

            var baseline = SnapshotStore.TryReadBaseline(baselineName);
            if (baseline is null)
            {
                var missingActual = SnapshotText(effectiveOptions);
                SnapshotStore.WriteMismatchArtifacts(
                    baselineName,
                    missingActual,
                    LineDiff.Render([], LineDiff.ToLines(missingActual)));

                throw new UiDxExpectError(
                    $"locator({BuildDescription()})",
                    "ToMatchUiaSnapshot",
                    effectiveTimeout,
                    [
                        $"baseline missing: {SnapshotStore.BaselinePath(baselineName)}"
                        + $" — set {SnapshotStore.UpdateEnvironmentVariable}=1 to create",
                        $"actual:   {SnapshotStore.MismatchActualPath(baselineName)}",
                    ],
                    $"\"{baselineName}\"");
            }

            var actualText = string.Empty;
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                actualText = SnapshotText(effectiveOptions);
                if (string.Equals(actualText, baseline, StringComparison.Ordinal))
                {
                    return;
                }

                if (stopwatch.Elapsed >= effectiveTimeout)
                {
                    break;
                }

                Thread.Sleep(PollInterval);
            }

            var diffLines = LineDiff.Render(LineDiff.ToLines(baseline), LineDiff.ToLines(actualText));
            SnapshotStore.WriteMismatchArtifacts(baselineName, actualText, diffLines);

            var failureLog = new List<string>
            {
                "baseline: " + SnapshotStore.BaselinePath(baselineName),
                "actual:   " + SnapshotStore.MismatchActualPath(baselineName),
            };
            failureLog.AddRange(diffLines);

            throw new UiDxExpectError(
                $"locator({BuildDescription()})",
                "ToMatchUiaSnapshot",
                effectiveTimeout,
                failureLog,
                $"\"{baselineName}\"");
        }
        finally
        {
            _session.Trace.EndPhase();
        }
    }

    /// <summary>
    /// Captures the snapshot text, waiting for the tree to settle first.
    /// </summary>
    /// <remarks>
    /// Two captures separated by <see cref="UiaSnapshotOptions.SettleDelay"/> must be identical before
    /// the text is accepted. VB6's lazy MSAA-to-UIA bridge can build its tree between two captures, so
    /// a single capture is not trustworthy. When the tree does not settle within
    /// <see cref="UiaSnapshotOptions.SettleTimeout"/> the last capture is returned: the caller asked for
    /// a snapshot, and a possibly unstable snapshot is more useful than a timeout — but the retry is
    /// recorded so a report can show it happened.
    /// </remarks>
    private string SnapshotText(UiaSnapshotOptions options)
    {
        if (!options.Settle)
        {
            return CaptureSnapshot(options);
        }

        var settleTimeout = options.SettleTimeout ?? options.Timeout ?? _session.DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        var previous = CaptureSnapshot(options);
        var attempts = 1;

        while (stopwatch.Elapsed < settleTimeout)
        {
            Thread.Sleep(options.SettleDelay);

            var current = CaptureSnapshot(options);
            attempts++;

            if (string.Equals(previous, current, StringComparison.Ordinal))
            {
                if (attempts > 2)
                {
                    _session.Trace.InternalLog($"snapshot settled after {attempts} captures");
                }

                return current;
            }

            previous = current;
        }

        _session.Trace.InternalLog(
            $"snapshot did not settle within {settleTimeout.TotalMilliseconds:0}ms ({attempts} captures); using the last capture");
        return previous;
    }

    private string CaptureSnapshot(UiaSnapshotOptions options)
    {
        var element = ResolveCore(PollInterval);
        return UiaSnapshotRenderer.Render(element, options);
    }

    // ── Actions (each one goes through the actionability gate) ───────────────

    /// <summary>
    /// Gate: Visible + Stable + ReceivesEvents + Enabled, then a real mouse click at the element's
    /// clickable point.
    /// </summary>
    /// <param name="timeout">Gate timeout. Defaults to <see cref="AppSession.DefaultTimeout"/>.</param>
    /// <param name="force">
    /// Escape hatch that skips Visible/Stable/ReceivesEvents. <b>Enabled is still checked</b>
    /// (Schritt 0 rule).
    /// </param>
    public void Click(TimeSpan? timeout = null, bool force = false)
    {
        var effectiveTimeout = timeout ?? _session.DefaultTimeout;
        _session.EnsureAlive();
        var checks = force
            ? new[] { ActionabilityCheck.Enabled }
            : new[]
            {
                ActionabilityCheck.Visible,
                ActionabilityCheck.Stable,
                ActionabilityCheck.ReceivesEvents,
                ActionabilityCheck.Enabled,
            };

        var element = BeginAction(effectiveTimeout);
        var gate = new ActionabilityGate(_session);
        try
        {
            element = gate.Run(
                CurrentElementResolver(effectiveTimeout),
                checks,
                effectiveTimeout,
                $"click locator({BuildDescription()})");
        }
        finally
        {
            EndAction();
        }

        var point = GetClickablePointOrCentre(element);
        _session.Trace.InternalLog($"click executed at ({point.X}, {point.Y})");

        // A real mouse click via the native input API (not InvokePattern).
        FlaUI.Core.Input.Mouse.Click(point);
    }

    /// <summary>
    /// Gate: Visible + Enabled + Editable. Sets the value via ValuePattern (no typing simulation).
    /// </summary>
    public void Fill(string text, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var effectiveTimeout = timeout ?? _session.DefaultTimeout;
        _session.EnsureAlive();
        var element = BeginAction(effectiveTimeout);
        var gate = new ActionabilityGate(_session);
        try
        {
            element = gate.Run(
                CurrentElementResolver(effectiveTimeout),
                // Editable is checked before Enabled because its definition already includes
                // "enabled" — this way a disabled/read-only field reports the specific
                // "element is not editable" message (Schritt 0 Fill gate).
                [ActionabilityCheck.Visible, ActionabilityCheck.Editable, ActionabilityCheck.Enabled],
                effectiveTimeout,
                $"fill locator({BuildDescription()})");
        }
        finally
        {
            EndAction();
        }

        var valuePattern = element.Patterns.Value.PatternOrDefault
                           ?? throw new UiDxTimeoutError(
                               $"fill locator({BuildDescription()})",
                               effectiveTimeout,
                               _session.Trace.ToCallLog());
        valuePattern.SetValue(text);
        _session.Trace.InternalLog($"fill executed: value set to \"{text}\"");
    }

    /// <summary>Gate: Visible + Enabled. Selects a combo item by label.</summary>
    public void Select(string itemLabel, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemLabel);

        var effectiveTimeout = timeout ?? _session.DefaultTimeout;
        _session.EnsureAlive();
        var element = BeginAction(effectiveTimeout);
        var gate = new ActionabilityGate(_session);
        try
        {
            element = gate.Run(
                CurrentElementResolver(effectiveTimeout),
                [ActionabilityCheck.Visible, ActionabilityCheck.Enabled],
                effectiveTimeout,
                $"select locator({BuildDescription()})");
        }
        finally
        {
            EndAction();
        }

        var comboBox = element.AsComboBox();

        // WinForms combo boxes only expose their items once the drop-down has actually been opened,
        // and the UIA Expand/Collapse pattern is not supported for them. A real mouse click on the
        // control opens the list, after which the items are readable.
        if (SafeItemCount(comboBox) == 0)
        {
            var bounds = comboBox.BoundingRectangle;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                FlaUI.Core.Input.Mouse.Click(
                    new System.Drawing.Point(bounds.Left + 20, bounds.Top + (bounds.Height / 2)));
            }
        }

        // Poll: the list may not be populated instantly.
        var stopwatch = Stopwatch.StartNew();
        FlaUI.Core.AutomationElements.ComboBoxItem? item = null;
        while (item is null && stopwatch.Elapsed < effectiveTimeout)
        {
            item = FindComboItem(comboBox, itemLabel);
            if (item is null)
            {
                Thread.Sleep(50);
            }
        }

        if (item is null)
        {
            CloseDropdown(comboBox);
            _session.Trace.InternalLog($"select failed: item \"{itemLabel}\" not found");
            throw new UiDxTimeoutError(
                $"select \"{itemLabel}\" in locator({BuildDescription()})",
                effectiveTimeout,
                _session.Trace.ToCallLog());
        }

        item.Select();
        CloseDropdown(comboBox);
        _session.Trace.InternalLog($"select executed: \"{itemLabel}\"");
    }

    private static int SafeItemCount(FlaUI.Core.AutomationElements.ComboBox comboBox)
    {
        try
        {
            return comboBox.Items.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static FlaUI.Core.AutomationElements.ComboBoxItem? FindComboItem(
        FlaUI.Core.AutomationElements.ComboBox comboBox,
        string itemLabel)
    {
        try
        {
            return comboBox.Items.FirstOrDefault(
                item => string.Equals(item.Text, itemLabel, StringComparison.Ordinal)
                        || string.Equals(item.Name, itemLabel, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CloseDropdown(FlaUI.Core.AutomationElements.ComboBox comboBox)
    {
        try
        {
            comboBox.Collapse();
        }
        catch (Exception)
        {
            // Closing is best-effort; the selection already happened.
        }
    }

    /// <summary>
    /// Escape hatch: calls the InvokePattern <em>without any gate</em> — deterministic, for targets
    /// that must not wait (e.g. deliberately disabled controls).
    /// </summary>
    public void Invoke(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? _session.DefaultTimeout;
        _session.EnsureAlive();
        var element = ResolveCore(effectiveTimeout);

        var invokePattern = element.Patterns.Invoke.PatternOrDefault
                            ?? throw new UiDxTimeoutError(
                                $"invoke locator({BuildDescription()})",
                                effectiveTimeout,
                                _session.Trace.ToCallLog());
        invokePattern.Invoke();
        _session.Trace.InternalLog("invoke executed (gate skipped)");
    }

    private static System.Drawing.Point GetClickablePointOrCentre(AutomationElement element)
    {
        try
        {
            if (element.TryGetClickablePoint(out var point) && !point.IsEmpty)
            {
                return point;
            }
        }
        catch (Exception)
        {
            // Fall back to the centre of the bounding rectangle.
        }

        var bounds = element.BoundingRectangle;
        return new System.Drawing.Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + (bounds.Height / 2));
    }

    /// <summary>
    /// Logs the locator phase once and resolves the target. The gate then re-checks the same element
    /// without writing further phases, so the call log shows one phase with all failing checks.
    /// </summary>
    private AutomationElement BeginAction(TimeSpan timeout)
        => ResolveCore(timeout);

    /// <summary>Returns the current element for a gate re-check (silent, no extra phase).</summary>
    private Func<AutomationElement?> CurrentElementResolver(TimeSpan timeout)
        => () =>
        {
            try
            {
                return ResolveSilently(timeout);
            }
            catch (UiDxTimeoutError)
            {
                // The element disappeared mid-gate; the checks fail and the gate retries.
                return null;
            }
        };

    /// <summary>Restores the base indentation after an action so the next phase starts flat.</summary>
    private void EndAction() => _session.Trace.EndPhase();

    private AutomationElement ResolveCore(TimeSpan? timeout, bool logPhase = true)
    {
        var session = _session;
        session.EnsureAlive();

        var effectiveTimeout = timeout ?? session.DefaultTimeout;
        var scope = ResolveScope(logScope: logPhase);

        if (logPhase)
        {
            session.Trace.Phase(
                $"waiting for locator({BuildDescription()}) (timeout {effectiveTimeout.TotalMilliseconds:0}ms)");
        }

        var stopwatch = Stopwatch.StartNew();
        var matches = FindMatches(scope);
        var take = _index ?? 0;
        while (matches.Count <= take && stopwatch.Elapsed < effectiveTimeout)
        {
            Thread.Sleep(PollInterval);
            matches = FindMatches(scope);
        }

        if (_index is null)
        {
            if (matches.Count > 1)
            {
                // No timeout wait on a multiple match.
                var rendered = matches.Select(Format).ToList();
                if (logPhase)
                {
                    session.Trace.InternalLog($"strict mode violation: {matches.Count} elements matched");
                }

                throw new UiDxStrictError(BuildDescription(), rendered, session.Trace.ToCallLog());
            }

            if (matches.Count == 0)
            {
                LogNotFound(scope, logPhase);
                throw new UiDxTimeoutError(
                    $"locator({BuildDescription()})",
                    effectiveTimeout,
                    session.Trace.ToCallLog());
            }

            if (logPhase)
            {
                session.Trace.InternalLog($"locator resolved to {Format(matches[0])}");
            }

            return matches[0];
        }

        // Strict mode off (Nth).
        var index = _index.Value;
        if (matches.Count == 0)
        {
            LogNotFound(scope, logPhase, $"no element found (nth({index}) of 0)");
            throw new UiDxTimeoutError(
                $"locator({BuildDescription()})",
                effectiveTimeout,
                session.Trace.ToCallLog());
        }

        if (index >= matches.Count)
        {
            if (logPhase)
            {
                session.Trace.InternalLog($"strict mode off: index {index} out of range (found {matches.Count})");
            }

            throw new UiDxTimeoutError(
                $"locator({BuildDescription()})",
                effectiveTimeout,
                session.Trace.ToCallLog());
        }

        if (logPhase)
        {
            session.Trace.InternalLog($"strict mode off: taking match {index} of {matches.Count}");
            session.Trace.InternalLog($"locator resolved to {Format(matches[index])}");
        }

        return matches[index];
    }

    /// <summary>
    /// Logs the "not found" evidence: the message line plus the scope children dump. Silent resolves
    /// skip it — their callers own the logging and would otherwise duplicate the dump.
    /// </summary>
    private void LogNotFound(AutomationElement scope, bool logPhase, string message = "no element found")
    {
        if (!logPhase)
        {
            return;
        }

        _session.Trace.InternalLog(message);
        AddScopeChildrenDump(scope);
    }

    /// <summary>The element that scopes the search: the chained scope or the session's main window.</summary>
    private AutomationElement ResolveScope(bool logScope = true)
    {
        if (_parentScope is not null)
        {
            // Chained: resolve the parent locator first (it is strict on its own level too).
            var parentElement = _parentScope.ResolveCore(timeout: null);
            if (logScope)
            {
                _session.Trace.InternalLog($"scope resolved to {Format(parentElement)}");
            }

            return parentElement;
        }

        if (_scopeElement is { } explicitScope)
        {
            return explicitScope;
        }

        var window = _session.MainWindow
                     ?? throw new UiDxTimeoutError(
                         "session has no main window",
                         _session.DefaultTimeout,
                         _session.Trace.ToCallLog());
        if (logScope)
        {
            _session.Trace.InternalLog($"scope resolved to {Format(window)}");
        }

        return window;
    }

    private List<AutomationElement> FindMatches(AutomationElement scope)
    {
        var matcher = BuildCondition();
        AutomationElement[] found;
        try
        {
            // Subtree = the scope element itself plus all its descendants. Including the scope is
            // what allows a locator to describe the window/panel that scopes the search, e.g.
            // session.GetByName("<window title>").ToUiaSnapshot().
            found = scope.FindAll(TreeScope.Subtree, matcher);
        }
        catch (Exception)
        {
            // The tree changed underneath us; report no match and let the poll loop retry.
            return [];
        }

        return found.Where(IsMatch).ToList();
    }

    private ConditionBase BuildCondition()
    {
        var factory = _session.Automation.ConditionFactory;
        ConditionBase? combined = null;
        foreach (var condition in _conditions)
        {
            var built = condition.Factory(factory);
            combined = combined is null ? built : combined.And(built);
        }

        return combined ?? throw new InvalidOperationException($"locator({BuildDescription()}) has no conditions");
    }

    /// <summary>
    /// Client-side re-check of the conditions. The AutomationId condition is compared strictly
    /// against the AutomationId property only — there is deliberately <em>no</em> name fallback here,
    /// because a silent OR-by-name would blur the locator semantics. Use
    /// <see cref="VB6Locator.Find"/> for the explicit legacy fallback chain.
    /// </summary>
    private bool IsMatch(AutomationElement element)
    {
        foreach (var condition in _conditions)
        {
            if (condition.Kind != LocatorConditionKind.AutomationId)
            {
                continue;
            }

            if (!string.Equals(ElementDescription.SafeAutomationId(element), condition.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the `scope children:` state dump for the locator's current scope. Used by the
    /// assertions so a failure on a never-existing element carries the same evidence as a resolve
    /// timeout.
    /// </summary>
    internal void LogScopeChildrenDump()
    {
        try
        {
            AddScopeChildrenDump(ResolveScope(logScope: false));
        }
        catch (Exception)
        {
            // The scope itself is gone; there is nothing to dump.
        }
    }

    private void AddScopeChildrenDump(AutomationElement scope)
    {
        const int maxLines = 20;
        AutomationElement[] children;
        try
        {
            children = scope.FindAllChildren();
        }
        catch (Exception)
        {
            children = [];
        }

        _session.Trace.InternalLog($"scope children: {children.Length}");
        foreach (var child in children.Take(maxLines))
        {
            _session.Trace.InternalDetail(Format(child));
        }

        if (children.Length > maxLines)
        {
            _session.Trace.InternalDetail($"… (+{children.Length - maxLines} more)");
        }
    }

    private Locator WithCondition(
        string text,
        Func<ConditionFactory, ConditionBase> factory,
        LocatorConditionKind kind,
        string value)
    {
        var conditions = new List<LocatorCondition>(_conditions)
        {
            new(text, factory, kind, value),
        };

        return new Locator(_session, _scopeElement, _parentScope, conditions, _index);
    }

    /// <summary>
    /// Name/AutomationId establish a new scope when the locator already describes an element
    /// (chaining), otherwise they add a condition to the current locator.
    /// </summary>
    private Locator AddNameOrIdCondition(
        string text,
        Func<ConditionFactory, ConditionBase> factory,
        LocatorConditionKind kind,
        string value)
    {
        if (_conditions.Count == 0)
        {
            return WithCondition(text, factory, kind, value);
        }

        return new Locator(_session, scopeElement: null, this, [new LocatorCondition(text, factory, kind, value)], _index);
    }

    private string BuildDescription()
    {
        var description = _conditions.Count == 0
            ? "empty"
            : string.Join(" ", _conditions.Select(condition => condition.Text));
        return _index is null ? description : $"{description} nth({_index})";
    }

    private static string Format(AutomationElement element) => ElementDescription.Describe(element);

    private enum LocatorConditionKind
    {
        Other,
        AutomationId,
    }

    private sealed record LocatorCondition(
        string Text,
        Func<ConditionFactory, ConditionBase> Factory,
        LocatorConditionKind Kind,
        string Value);
}
