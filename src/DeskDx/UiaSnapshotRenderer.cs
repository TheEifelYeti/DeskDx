using System.Text;
using FlaUI.Core.AutomationElements;

namespace DeskDx;

/// <summary>
/// Renders a UIA subtree as canonical snapshot text: two spaces of indentation per tree level and
/// one <c>ControlType "Name" [AutomationId=id]</c> line per element.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is a <b>structural</b> artifact. It deliberately contains no <c>BoundingRectangle</c>
/// (position is not a structure signal), no handles, runtime ids, process data or timestamps — that
/// is what makes two snapshots byte-identical.
/// </para>
/// <para>
/// Children are emitted in UIA enumeration order (which is z-order and stable for WinForms).
/// Sorting would be canonicality without evidence of instability.
/// </para>
/// <para>
/// This type is public so that callers which already hold an element (for example a diagnostic tool
/// reached through the <see cref="AppSession.Automation"/> escape hatch) render with the library
/// instead of re-implementing the format. Use <see cref="Locator.ToUiaSnapshot"/> when a locator is
/// available; it is the same renderer.
/// </para>
/// </remarks>
public static class UiaSnapshotRenderer
{
    private const string Indent = "  ";

    /// <summary>Renders the element and its subtree as canonical snapshot text.</summary>
    public static string Render(AutomationElement root, UiaSnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new UiaSnapshotOptions();

        var builder = new StringBuilder();
        AppendElement(builder, root, depth: 0, options);
        return builder.ToString();
    }

    private static void AppendElement(StringBuilder builder, AutomationElement element, int depth, UiaSnapshotOptions options)
    {
        builder.Append(string.Concat(Enumerable.Repeat(Indent, depth)));
        builder.Append(ElementDescription.Describe(element));
        if (options.IncludeState)
        {
            builder.Append(ElementDescription.SafeIsEnabled(element) ? " (enabled)" : " (disabled)");
        }

        builder.Append('\n');

        AutomationElement[] children;
        try
        {
            children = element.FindAllChildren();
        }
        catch (Exception)
        {
            // The subtree changed underneath us; render what we already have.
            return;
        }

        foreach (var child in children)
        {
            if (IsExcluded(child, options))
            {
                continue;
            }

            AppendElement(builder, child, depth + 1, options);
        }
    }

    /// <summary>
    /// True when the element's AutomationId or Name matches one of the exclusions (exact,
    /// case-insensitive match). A match skips the element <em>and</em> its whole subtree.
    /// </summary>
    private static bool IsExcluded(AutomationElement element, UiaSnapshotOptions options)
    {
        if (options.ExcludeIds.Length == 0)
        {
            return false;
        }

        var automationId = ElementDescription.SafeAutomationId(element);
        var name = ElementDescription.SafeName(element);

        foreach (var excluded in options.ExcludeIds)
        {
            if (string.Equals(automationId, excluded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
