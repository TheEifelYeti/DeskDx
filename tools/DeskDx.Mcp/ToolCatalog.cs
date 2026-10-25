using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DeskDx.Mcp;

/// <summary>
/// The tool catalogue: names, descriptions and input schemas.
/// </summary>
/// <remarks>
/// <para>
/// The handlers are dispatched by name in the stdio adapter, so the schemas are declared here by hand
/// instead of being generated from method signatures. That is a deliberate trade: the spec's tools
/// take a small, fixed set of arguments, and a hand-written schema can carry the <em>locator
/// grammar</em> in the parameter descriptions, which is the part the agent has to get right.
/// </para>
/// <para>
/// Every tool is sessionId-first and speaks locators, never refs.
/// </para>
/// </remarks>
public static class ToolCatalog
{
    /// <summary>Builds the tool definitions advertised to the client.</summary>
    public static List<Tool> Define() =>
    [
        Lifecycle(
            "ui_launch",
            "Start a Windows desktop application and open a DeskDx session for it. Returns the sessionId, "
            + "the chosen main window and the trace path. Modal dialogs are answered automatically.",
            new Dictionary<string, JsonSchemaProperty>
            {
                ["exePath"] = Prop("string", "Absolute path of the executable (or alias) to start."),
                ["args"] = Prop("string", "Optional command line arguments."),
                ["startupTimeoutMs"] = Prop("integer", "Timeout to wait for the main window. Default 10000 ms."),
                ["expectWindowTitle"] = Prop(
                    "string",
                    "Expected main-window title (substring, case-insensitive). Set this for applications "
                    + "whose process owns several top-level windows — notably VB6/MDI apps, where the "
                    + "process's 'main window' can be a control-less container."),
            },
            ["exePath"]),

        Lifecycle(
            "ui_attach",
            "Attach to an already running process instead of starting a new one. The process is never "
            + "killed by the session.",
            new Dictionary<string, JsonSchemaProperty>
            {
                ["processName"] = Prop("string", "Image name or executable, e.g. MyApp or MyApp.exe."),
            },
            ["processName"]),

        Session(
            "ui_dispose",
            "End a session: close its trace and terminate the process if this session started it.",
            new Dictionary<string, JsonSchemaProperty>(),
            []),

        new Tool
        {
            Name = "ui_snapshot",
            Description =
                "Render the canonical UIA tree as text. This is how you discover what to address: each "
                + "line reads `ControlType \"Name\" [AutomationId=id]`, from which you build a locator "
                + "(`name=\"…\"`, `automationId=\"…\"`).",
            InputSchema = Schema(
                new Dictionary<string, JsonSchemaProperty>
                {
                    ["sessionId"] = Prop("string", "The session to read."),
                    ["locator"] = Prop(
                        "string",
                        "Optional scope. Default is the whole main window. Chain with `>>` to descend, "
                        + "e.g. `name=\"Gruppe A\" >> name=\"GruppenKnopf\"`."),
                    ["includeState"] = Prop("boolean", "Add the (enabled|disabled) state to every line."),
                    ["excludeIds"] = Prop(
                        "array", "AutomationIds/Names whose subtree is omitted (volatile controls).",
                        items: Prop("string", "An AutomationId or Name.")),
                },
                ["sessionId"]),
        },

        new Tool
        {
            Name = "ui_match_snapshot",
            Description =
                "Compare the current tree against a committed baseline and get the unified diff on "
                + "mismatch. This is the structural contract: a form that changed is a diff, not a "
                + "paragraph.",
            InputSchema = Schema(
                new Dictionary<string, JsonSchemaProperty>
                {
                    ["sessionId"] = Prop("string", "The session to read."),
                    ["locator"] = Prop("string", "Optional scope. Default is the whole main window."),
                    ["baselineName"] = Prop("string", "Baseline name without the .uia.txt suffix."),
                    ["update"] = Prop(
                        "boolean",
                        "Write the baseline instead of comparing. Only for the first, reviewed capture."),
                    ["excludeIds"] = Prop(
                        "array",
                        "AutomationIds/Names left out of the comparison. A form with timer-driven or "
                        + "otherwise volatile captions only compares stably with these exclusions.",
                        items: Prop("string", "An AutomationId or Name.")),
                },
                ["sessionId", "baselineName"]),
        },

        new Tool
        {
            Name = "ui_get_trace",
            Description =
                "Read the session's call log as text. Includes the dialog history (`dialog appeared` / "
                + "`dialog answered`) — the trace is where a handled MessageBox is visible.",
            InputSchema = Schema(
                new Dictionary<string, JsonSchemaProperty>
                {
                    ["sessionId"] = Prop("string", "The session to read."),
                    ["lastLines"] = Prop("integer", "Trailing lines to return. Default 50."),
                },
                ["sessionId"]),
        },

        Action(
            "ui_click",
            "Click an element with the mouse, through the actionability gate (visible, stable, receives "
            + "events, enabled). On failure the result explains which check failed and dumps the state.",
            new Dictionary<string, JsonSchemaProperty>
            {
                ["force"] = Prop(
                    "boolean",
                    "Skip the visible/stable/receives-events checks. The enabled check always runs."),
                ["timeoutMs"] = TimeoutProperty,
            }),

        Action(
            "ui_fill",
            "Set a text field's value through the gate (visible, editable, enabled). Does not simulate "
            + "typing.",
            new Dictionary<string, JsonSchemaProperty>
            {
                ["text"] = Prop("string", "The value to set."),
                ["timeoutMs"] = TimeoutProperty,
            },
            ["text"]),

        Action(
            "ui_select",
            "Select a combo-box item by its label, through the gate (visible, enabled).",
            new Dictionary<string, JsonSchemaProperty>
            {
                ["item"] = Prop("string", "The item label to select."),
                ["timeoutMs"] = TimeoutProperty,
            },
            ["item"]),

        new Tool
        {
            Name = "ui_invoke",
            Description =
                "Call the element's InvokePattern without any gate. Deterministic escape hatch for "
                + "targets that must not wait — a deliberately disabled control, or a button that opens "
                + "a modal dialog.",
            InputSchema = Schema(
                new Dictionary<string, JsonSchemaProperty>
                {
                    ["sessionId"] = Prop("string", "The session."),
                    ["locator"] = LocatorProperty,
                    ["timeoutMs"] = TimeoutProperty,
                },
                ["sessionId", "locator"]),
        },

        new Tool
        {
            Name = "ui_expect",
            Description =
                "Web-first assertion that polls until it holds or the timeout elapses. A failure returns "
                + "the reason and the element state, so you can see what the app actually showed.",
            InputSchema = Schema(
                new Dictionary<string, JsonSchemaProperty>
                {
                    ["sessionId"] = Prop("string", "The session."),
                    ["locator"] = LocatorProperty,
                    ["assertion"] = Prop(
                        "string",
                        "One of: visible, hidden, enabled, editable, hasValue, hasText.",
                        @enum: ["visible", "hidden", "enabled", "editable", "hasValue", "hasText"]),
                    ["expected"] = Prop("string", "Required for hasValue and hasText."),
                    ["timeoutMs"] = Prop("integer", "Timeout. Default is the session's default timeout."),
                },
                ["sessionId", "locator", "assertion"]),
        },
    ];

    private const string LocatorGrammar =
        "Target locator, e.g. `name=\"Speichern\"`, `automationId=\"btnSave\"`, `controlType=Button`, "
        + "or `name=\"Gruppe A\" >> name=\"GruppenKnopf\"` to scope the search to a subtree. Take the "
        + "values from a snapshot line: `Button \"Speichern\" [AutomationId=btnSave]`.";

    private static JsonSchemaProperty LocatorProperty => Prop("string", LocatorGrammar);

    private static JsonSchemaProperty TimeoutProperty => Prop(
        "integer",
        "Timeout for the gate/action. Defaults to the session's default timeout (5000 ms).");

    private static Tool Lifecycle(string name, string description, Dictionary<string, JsonSchemaProperty> properties, string[] required)
        => new()
        {
            Name = name,
            Description = description,
            InputSchema = Schema(properties, required),
        };

    private static Tool Session(string name, string description, Dictionary<string, JsonSchemaProperty> properties, string[] required)
        => new()
        {
            Name = name,
            Description = description,
            InputSchema = Schema(
                WithSession(properties),
                ["sessionId", .. required]),
        };

    private static Tool Action(
        string name,
        string description,
        Dictionary<string, JsonSchemaProperty> extra,
        string[] extraRequired = null!)
    {
        var properties = WithSession(extra);
        properties["locator"] = LocatorProperty;

        return new Tool
        {
            Name = name,
            Description = description,
            InputSchema = Schema(properties, ["sessionId", "locator", .. extraRequired ?? []]),
        };
    }

    private static Dictionary<string, JsonSchemaProperty> WithSession(Dictionary<string, JsonSchemaProperty> properties)
    {
        var result = new Dictionary<string, JsonSchemaProperty>(properties)
        {
            ["sessionId"] = Prop("string", "The session returned by ui_launch/ui_attach."),
        };

        return result;
    }

    private static JsonSchemaProperty Prop(string type, string description, JsonSchemaProperty? items = null, string[]? @enum = null)
        => new()
        {
            Type = type,
            Description = description,
            Items = items,
            Enum = @enum,
        };

    private static JsonElement Schema(Dictionary<string, JsonSchemaProperty> properties, string[] required)
    {
        var schema = new
        {
            type = "object",
            properties = properties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToJson()),
            required,
        };

        return JsonSerializer.SerializeToElement(schema, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A single JSON Schema property (a subset sufficient for these tools).</summary>
    public sealed class JsonSchemaProperty
    {
        /// <summary>JSON Schema type, e.g. <c>string</c>, <c>integer</c>, <c>boolean</c>, <c>array</c>.</summary>
        public string Type { get; init; } = "string";

        /// <summary>Human-readable description, shown to the agent.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Item schema for arrays.</summary>
        public JsonSchemaProperty? Items { get; init; }

        /// <summary>Allowed values, for enumerations.</summary>
        public string[]? Enum { get; init; }

        internal object ToJson() => new
        {
            type = Type,
            description = Description,
            items = Items?.ToJson(),
            @enum = Enum,
        };
    }
}
