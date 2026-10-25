using System.Drawing;
using FlaUI.Core.AutomationElements;

namespace DeskDx;

/// <summary>
/// Renders automation elements in the Schritt 0 call log format
/// (<c>ControlType "Name" [AutomationId=id]</c>). Shared by the locator, the VB6 fallback chain and
/// the actionability gate so the evidence format cannot drift between them.
/// </summary>
internal static class ElementDescription
{
    /// <summary>Renders <c>ControlType "Name" [AutomationId=id]</c>.</summary>
    internal static string Describe(AutomationElement element)
        => $"{SafeControlType(element)} \"{SafeName(element)}\" [AutomationId={SafeAutomationId(element)}]";

    /// <summary>
    /// Renders the UIA state dump line used for gate and expect failures.
    /// </summary>
    internal static string DescribeState(AutomationElement element)
    {
        var bounds = TryGetBounds(element);
        var rect = bounds is null
            ? "n/a"
            : $"({bounds.Value.X},{bounds.Value.Y},{bounds.Value.Width},{bounds.Value.Height})";
        return $"state: Name=\"{SafeName(element)}\" ControlType={SafeControlType(element)} "
               + $"IsEnabled={SafeIsEnabled(element)} IsOffscreen={SafeIsOffscreen(element)} "
               + $"Rect={rect} Patterns={string.Join(",", SafePatternNames(element))}";
    }

    internal static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Name;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static string SafeAutomationId(AutomationElement element)
    {
        try
        {
            return element.AutomationId;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static string SafeControlType(AutomationElement element)
    {
        try
        {
            return element.ControlType.ToString();
        }
        catch (Exception)
        {
            return "Unknown";
        }
    }

    internal static Rectangle? TryGetBounds(AutomationElement element)
    {
        try
        {
            var bounds = element.BoundingRectangle;
            return bounds.IsEmpty ? null : bounds;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool SafeIsOffscreen(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen;
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static bool SafeIsEnabled(AutomationElement element)
    {
        try
        {
            return element.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafePatternNames(AutomationElement element)
    {
        FlaUI.Core.Identifiers.PatternId[] patterns;
        try
        {
            patterns = element.GetSupportedPatterns();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var pattern in patterns)
        {
            yield return pattern.Name;
        }
    }
}
