using System.Text;

namespace DeskDx;

/// <summary>
/// A minimal line-based LCS diff in unified-diff style.
/// </summary>
/// <remarks>
/// This is a deliberate choice over pulling in DiffPlex: the snapshots are small (one line per
/// control, typically &lt; 100 lines), the required output is only the interleaved <c>-</c>/<c>+</c>
/// rendering shown in the spec, and an own implementation keeps the library free of an extra
/// dependency for a feature that needs no hunk headers or move detection.
/// </remarks>
internal static class LineDiff
{
    /// <summary>Renders the baseline→actual difference as unified-diff-style lines.</summary>
    internal static IReadOnlyList<string> Render(IReadOnlyList<string> baseline, IReadOnlyList<string> actual)
    {
        var lcs = LongestCommonSubsequence(baseline, actual);
        var lines = new List<string>();
        int baselineIndex = 0, actualIndex = 0, lcsIndex = 0;

        while (baselineIndex < baseline.Count || actualIndex < actual.Count)
        {
            if (lcsIndex < lcs.Count && baselineIndex < baseline.Count && baseline[baselineIndex] == lcs[lcsIndex]
                && actualIndex < actual.Count && actual[actualIndex] == lcs[lcsIndex])
            {
                // Context line (unchanged on both sides).
                lines.Add(" " + baseline[baselineIndex]);
                baselineIndex++;
                actualIndex++;
                lcsIndex++;
            }
            else if (baselineIndex < baseline.Count
                     && (lcsIndex >= lcs.Count || baseline[baselineIndex] != lcs[lcsIndex]))
            {
                lines.Add("-" + baseline[baselineIndex]);
                baselineIndex++;
            }
            else
            {
                lines.Add("+" + actual[actualIndex]);
                actualIndex++;
            }
        }

        return lines;
    }

    /// <summary>Classic dynamic-programming LCS over the line arrays.</summary>
    private static List<string> LongestCommonSubsequence(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var lengths = new int[left.Count + 1, right.Count + 1];
        for (var i = left.Count - 1; i >= 0; i--)
        {
            for (var j = right.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = left[i] == right[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var result = new List<string>();
        int x = 0, y = 0;
        while (x < left.Count && y < right.Count)
        {
            if (left[x] == right[y])
            {
                result.Add(left[x]);
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return result;
    }

    /// <summary>Splits rendered snapshot text into lines without a trailing empty entry.</summary>
    internal static string[] ToLines(string snapshotText)
    {
        var normalized = snapshotText.Replace("\r\n", "\n").TrimEnd('\n');
        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    /// <summary>Renders lines as text with a trailing newline (file friendly).</summary>
    internal static string ToText(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.TrimEnd('\r')).Append('\n');
        }

        return builder.ToString();
    }
}
