namespace DeskDx;

/// <summary>Options for <see cref="Locator.ToUiaSnapshot"/> and <see cref="Locator.ToMatchUiaSnapshot"/>.</summary>
public sealed class UiaSnapshotOptions
{
    /// <summary>
    /// Include the <c>(enabled|disabled)</c> state in every line. Default: structure only
    /// (control type, name, automation id) — state belongs in <c>Expect.ToBeEnabled</c>, not in the
    /// structural contract.
    /// </summary>
    public bool IncludeState { get; init; }

    /// <summary>
    /// Elements whose AutomationId or Name matches are excluded along with their whole subtree.
    /// For volatile controls such as status labels.
    /// </summary>
    public string[] ExcludeIds { get; init; } = [];

    /// <summary>
    /// Poll timeout for the match (only used by <see cref="Locator.ToMatchUiaSnapshot"/>).
    /// Defaults to <see cref="AppSession.DefaultTimeout"/>.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Delay between the two captures of the settle check. Default 300 ms.
    /// </summary>
    /// <remarks>
    /// VB6 applications expose their control tree through an MSAA-to-UIA bridge that is built lazily,
    /// so two captures taken back to back can differ (observed: 99 vs 98 lines). A capture is only
    /// accepted once two consecutive captures are identical — the same rule the actionability gate
    /// applies to the Stable check.
    /// </remarks>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long the settle check may retry before the last capture is used anyway. Defaults to
    /// <see cref="Timeout"/>, or <see cref="AppSession.DefaultTimeout"/> when no timeout is set.
    /// </summary>
    /// <remarks>
    /// A tree that never settles must not block the operation forever: a caller that asked for a
    /// snapshot gets one, at the cost of a possibly unstable result. Only the two-capture agreement
    /// is best effort; the capture itself is not.
    /// </remarks>
    public TimeSpan? SettleTimeout { get; init; }

    /// <summary>
    /// True when the settle check is active. Exposed so a caller can disable it explicitly.
    /// </summary>
    /// <remarks>
    /// Disabling is the escape hatch for a tree with permanently animated content, where two
    /// identical captures may never occur.
    /// </remarks>
    public bool Settle { get; init; } = true;
}
