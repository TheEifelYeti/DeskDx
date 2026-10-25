using System.Text;
using FlaUI.Core.Definitions;
using DeskDx;

namespace DeskDx.Mcp;

/// <summary>
/// Turns a locator <em>string</em> into a <see cref="Locator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Locators are the identification mechanism (DeskDx philosophy): a snapshot shows
/// <c>ControlType "Name" [AutomationId=…]</c> and the agent builds the locator from that line.
/// There is no second identity layer (no server-side <c>w1e5</c>-style refs), so nothing has to be
/// kept in sync with the live tree.
/// </para>
/// <para>
/// Grammar:
/// <code>
/// locator := step ( "&gt;&gt;" step )*
/// step    := ( name="..." | automationId="..." ) [ controlType=Button ] [ nth=N ]
///          | controlType=Button [ nth=N ]        (first step only)
/// </code>
/// Steps are chained with <c>&gt;&gt;</c>, which searches only the subtree of the previous step —
/// the same semantics as <c>Locator.GetByName</c> chaining. A <c>controlType</c> inside a step
/// AND-filters that step (it does not establish a new scope).
/// </para>
/// <example>
/// <code>
/// name="Speichern"
/// automationId="btnSave"
/// controlType=Button nth=1
/// name="Gruppe A" &gt;&gt; name="GruppenKnopf"
/// </code>
/// </example>
/// </remarks>
public static class LocatorString
{
    private const string ChainSeparator = ">>";

    /// <summary>Builds a locator from its textual description.</summary>
    /// <exception cref="FormatException">The text is not a well-formed locator.</exception>
    public static Locator Build(AppSession session, string locator)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(locator))
        {
            throw new FormatException(
                "locator is required, e.g. name=\"Speichern\", automationId=\"btnSave\" or controlType=Button");
        }

        Locator? current = null;
        var steps = locator.Split(ChainSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (steps.Length == 0)
        {
            throw new FormatException($"locator \"{locator}\" contains no step");
        }

        foreach (var step in steps)
        {
            current = ApplyStep(session, current, step, locator);
            if (current is null)
            {
                throw new FormatException($"locator \"{locator}\" could not be built");
            }
        }

        return current!;
    }

    private static Locator? ApplyStep(AppSession session, Locator? previous, string step, string wholeLocator)
    {
        var tokens = Tokenize(step);
        if (tokens.Count == 0)
        {
            throw new FormatException($"locator \"{wholeLocator}\" contains an empty step");
        }

        string? name = null;
        string? automationId = null;
        ControlType? controlType = null;
        int? nth = null;

        foreach (var token in tokens)
        {
            var separator = token.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException(
                    $"locator token \"{token}\" in step \"{step}\" is not a key=value pair; "
                    + "expected name=\"…\", automationId=\"…\", controlType=… or nth=N");
            }

            var key = token[..separator];
            var value = Unquote(token[(separator + 1)..], step);

            switch (key.ToLowerInvariant())
            {
                case "name":
                    if (name is not null)
                    {
                        throw new FormatException(
                            $"step \"{step}\" has two name conditions; use \"{ChainSeparator}\" to chain scopes");
                    }

                    name = RequireValue(key, value, step);
                    break;

                case "automationid":
                    if (automationId is not null)
                    {
                        throw new FormatException(
                            $"step \"{step}\" has two automationId conditions; use \"{ChainSeparator}\" to chain scopes");
                    }

                    automationId = RequireValue(key, value, step);
                    break;

                case "controltype":
                    controlType = ParseControlType(RequireValue(key, value, step), step);
                    break;

                case "nth":
                    nth = ParseIndex(RequireValue(key, value, step), step);
                    break;

                default:
                    throw new FormatException(
                        $"unknown locator key \"{key}\" in step \"{step}\"; "
                        + "expected name, automationId, controlType or nth");
            }
        }

        if (name is not null && automationId is not null)
        {
            throw new FormatException(
                $"step \"{step}\" combines name and automationId; use \"{ChainSeparator}\" to chain scopes");
        }

        var locator = BuildStep(session, previous, step, name, automationId, controlType);
        return nth is null ? locator : locator.Nth(nth.Value);
    }

    private static Locator BuildStep(
        AppSession session,
        Locator? previous,
        string step,
        string? name,
        string? automationId,
        ControlType? controlType)
    {
        Locator locator;
        if (previous is null)
        {
            // First step: seed from the session (scoped to its main window).
            locator = (name, automationId) switch
            {
                (not null, _) => session.GetByName(name),
                (_, not null) => session.GetByAutomationId(automationId),
                _ => controlType is { } type
                    ? session.GetByControlType(type)
                    : throw new FormatException($"step \"{step}\" has no condition"),
            };
        }
        else
        {
            // Chained step: the previous locator's element becomes the scope. controlType alone
            // cannot establish a scope, so a chained step must name its element.
            locator = (name, automationId) switch
            {
                (not null, _) => previous.GetByName(name),
                (_, not null) => previous.GetByAutomationId(automationId),
                _ => throw new FormatException(
                    $"chained step \"{step}\" must start with name or automationId "
                    + "(controlType only filters, it cannot scope)"),
            };
        }

        return controlType is { } filter ? locator.GetByControlType(filter) : locator;
    }

    private static string RequireValue(string key, string value, string step)
        => string.IsNullOrEmpty(value)
            ? throw new FormatException($"locator key \"{key}\" in step \"{step}\" has an empty value")
            : value;

    private static ControlType ParseControlType(string value, string step)
        => Enum.TryParse<ControlType>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException(
                $"controlType \"{value}\" in step \"{step}\" is not a UIA control type (e.g. Button, Edit, ComboBox, Window)");

    private static int ParseIndex(string value, string step)
        => int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new FormatException($"nth \"{value}\" in step \"{step}\" is not a non-negative integer");

    /// <summary>Removes one layer of surrounding double quotes, honouring the tokenizer's quote handling.</summary>
    private static string Unquote(string value, string step)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        if (value.StartsWith('"') || value.EndsWith('"'))
        {
            throw new FormatException($"unbalanced quote in step \"{step}\"");
        }

        return value;
    }

    /// <summary>
    /// Splits a step on whitespace, but keeps quoted values (which may contain spaces) together.
    /// </summary>
    private static List<string> Tokenize(string step)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in step)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                current.Append(character);
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new FormatException($"unterminated quote in step \"{step}\"");
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
