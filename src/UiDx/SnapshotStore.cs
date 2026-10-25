namespace UiDx;

/// <summary>
/// Reads and writes UIA snapshot baselines under <c>snapshots/</c> and records mismatch artifacts
/// under <c>snapshots/__mismatches__/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Baselines are meant to be committed: the git diff over a baseline is evidence of what a migration
/// module changed about a form.
/// </para>
/// <para>
/// A baseline is never created silently. It is only written when the
/// <c>UIDX_SNAPSHOT_UPDATE</c> environment variable is set to <c>1</c> — a self-generated baseline is
/// a step to review, not something that happens on its own.
/// </para>
/// </remarks>
internal static class SnapshotStore
{
    internal const string UpdateEnvironmentVariable = "UIDX_SNAPSHOT_UPDATE";

    private const string SnapshotDirectoryName = "snapshots";
    private const string MismatchDirectoryName = "__mismatches__";

    /// <summary>
    /// Base directory the <c>snapshots/</c> folder is resolved against. Defaults to the current
    /// directory, which is right for a tool run from the repository root. A test project points this
    /// at its own root so the baselines are committed next to the tests instead of living in
    /// <c>bin/</c>.
    /// </summary>
    internal static string BaseDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>True when the update mode is active (baselines are written instead of compared).</summary>
    internal static bool IsUpdateMode
        => string.Equals(
            Environment.GetEnvironmentVariable(UpdateEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    /// <summary>The directory the baselines live in.</summary>
    internal static string SnapshotDirectory => Path.Combine(BaseDirectory, SnapshotDirectoryName);

    internal static string BaselinePath(string baselineName)
        => Path.Combine(SnapshotDirectory, baselineName + ".uia.txt");

    internal static string MismatchDirectory
        => Path.Combine(SnapshotDirectory, MismatchDirectoryName);

    internal static string MismatchActualPath(string baselineName)
        => Path.Combine(MismatchDirectory, baselineName + ".actual.txt");

    internal static string MismatchDiffPath(string baselineName)
        => Path.Combine(MismatchDirectory, baselineName + ".diff");

    /// <summary>
    /// Reads the baseline, or null when it does not exist. Line endings are normalized to LF: with
    /// <c>core.autocrlf</c> enabled git checks the file out as CRLF, and a comparison that is
    /// sensitive to that would fail on a fresh clone.
    /// </summary>
    internal static string? TryReadBaseline(string baselineName)
    {
        var path = BaselinePath(baselineName);
        return File.Exists(path) ? NormalizeLineEndings(File.ReadAllText(path)) : null;
    }

    /// <summary>Writes the baseline, creating the directory when needed.</summary>
    internal static void WriteBaseline(string baselineName, string snapshotText)
    {
        var path = BaselinePath(baselineName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, NormalizeLineEndings(snapshotText));
    }

    /// <summary>Normalizes CRLF/CR to LF so the comparison is line-ending independent.</summary>
    internal static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// Writes the actual snapshot and the diff next to it, so a failure is inspectable without
    /// re-running the test. Both live in the (git-ignored) mismatch directory.
    /// </summary>
    internal static void WriteMismatchArtifacts(string baselineName, string actualText, IReadOnlyList<string> diffLines)
    {
        Directory.CreateDirectory(MismatchDirectory);
        File.WriteAllText(MismatchActualPath(baselineName), actualText);
        File.WriteAllText(MismatchDiffPath(baselineName), LineDiff.ToText(diffLines));
    }
}
