# DeskDx

**Playwright-style testing for Windows desktop apps, as a library and an MCP server, built for
legacy (VB6/MDI) realities.**

DeskDx exists so that a migration agent — Copilot, Claude Code, OpenCode, Cursor — can explore and
verify a WinForms or VB6 application on its own, with test discipline: locators instead of screen
coordinates, an actionability gate instead of blind clicks, a dialog watcher so the agent does not
die on a `MessageBox`, traces as evidence, and baselines as a structural contract.

## Why another desktop automation layer

Five things break naive UI automation on real legacy applications. DeskDx handles all five, and each
one has a failing-then-fixed dogfood run behind it:

| Reality | What breaks without it | What DeskDx does |
|---|---|---|
| VB6 / MDI containers | `MainWindowHandle` resolves to a control-less `ThunderRT6Main` window with 0 descendants, so every locator searches an empty tree | `AppSession.EnsureRichestMainWindow` re-ranks the process's top-level windows by descendant count |
| VB6 controls | `AutomationId` throws `PropertyNotSupportedException`; most captions are empty | `VB6Locator.Find` — an explicit 4-stage fallback chain that logs which stage matched |
| Impatient clicks | A click on a covered, animating or disabled control silently does nothing | `Locator.Click` runs an actionability gate (visible, stable, receives events, enabled) and writes *why* it failed |
| Modal dialogs | The app thread blocks inside `MessageBox.Show`; the next UIA call hangs forever | The dialog watcher answers the dialog and records it in the trace |
| "Did it change?" | A screenshot proves nothing and diffs in review | `ToMatchUiaSnapshot` compares a canonical structure baseline and returns a unified diff |

## Quick start (library)

```csharp
using DeskDx;
using static DeskDx.Expect;

using var session = AppSession.Launch(@"C:\legacy\MyApp.exe");

session.GetByName("Speichern").Click();
session.GetByAutomationId("txtKunde").Fill("Müller GmbH");
session.GetByName("Gruppe A").GetByName("GruppenKnopf").Click();   // chained scope

Expect.That(session.GetByAutomationId("btnDelayed")).ToBeEnabled();
session.GetByName("Hauptformular").ToMatchUiaSnapshot("hauptformular");
```

Every operation writes a Playwright-style call log; the trace directory is named in every error, so a
failure is inspectable instead of mysterious.

## The MCP server — the front end DeskDx was built for

`tools/DeskDx.Mcp` exposes the engine over the [Model Context Protocol](https://modelcontextprotocol.io/)
(stdio transport, official `ModelContextProtocol` C# SDK, GA 2.x).

### Connection setup

```json
{
  "mcpServers": {
    "deskdx": {
      "type": "local",
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\DeskDx\\tools\\DeskDx.Mcp"],
      "tools": ["*"]
    }
  }
}
```

`dotnet run` builds and starts the server straight from the repository — the simplest setup while
developing. For a faster start with a fixed binary, publish once and point `command` at the produced
`DeskDx.Mcp.exe`:

```powershell
dotnet publish tools/DeskDx.Mcp -c Release
```

Works with Copilot, Claude Code, OpenCode, Cursor and VS Code. Two environment variables control where
the server keeps state:

| Variable | Meaning |
|---|---|
| `DESKDX_MCP_TRACE_DIR` | Directory session traces are written below (default: a timestamped folder in the temp directory) |
| `DESKDX_MCP_SNAPSHOT_DIR` | Directory containing `snapshots/`, i.e. where baselines are read and written |

Baselines are committed next to the application's tests, so the server must be told where that is — a
process started by a host does not run in the project directory.

### Tools

Every tool is `sessionId`-first and addresses elements by **locator string**, never by an opaque
handle — locators are the identification, and the snapshot teaches them.

| Tool | Purpose |
|---|---|
| `ui_launch` | Start an app and open a session; returns `sessionId`, main window and trace path |
| `ui_attach` | Attach to a running process (never killed by the session) |
| `ui_dispose` | End a session |
| `ui_snapshot` | Canonical UIA tree as text — how the agent discovers what to address |
| `ui_match_snapshot` | Compare against a committed baseline, get the unified diff on mismatch |
| `ui_get_trace` | Read the call log, dialog history included |
| `ui_click` | Gate-checked mouse click (visible, stable, receives events, enabled) |
| `ui_fill` | Gate-checked value fill |
| `ui_select` | Gate-checked combo selection |
| `ui_invoke` | Gate-free `InvokePattern` call — the deterministic escape hatch |
| `ui_expect` | Polling assertion: `visible`, `hidden`, `enabled`, `editable`, `hasValue`, `hasText` |

**Locator grammar.** A snapshot line is `ControlType "Name" [AutomationId=id]`, from which the agent
builds the locator:

```
name="Speichern"                                  # by caption
automationId="btnSave"                            # by designer name
controlType=Button nth=1                          # by type, non-strict
name="Gruppe A" >> name="GruppenKnopf"            # chain: scope the search to a subtree
```

Failures are structured (`isError: true` plus the DeskDx error text and call log): a gate failure names
the failing check and dumps the element state, so the agent can diagnose instead of retrying blindly.

### The agent story: verify a form after a migration module

Six calls, no human, real application — the script the spec asks for:

```
ui_launch        exePath="C:\legacy\MyApp.exe" expectWindowTitle="Kundenverwaltung"
              → sessionId: s1
                mainWindow: "Kundenverwaltung" (class ThunderRT6Form, 214 descendants)
                tracePath: %TEMP%\DeskDx.Mcp\20261017-101500\20261017-101500-4213

ui_snapshot      sessionId="s1" locator="name=\"Kundenverwaltung\""
              → Window "Kundenverwaltung" [AutomationId=]
                  Button "Speichern" [AutomationId=btnSave]
                  Edit "" [AutomationId=txtKunde]
                  ...
                (the agent now knows the captions and ids it can address)

ui_fill          sessionId="s1" locator="automationId=\"txtKunde\"" text="Müller GmbH"
              → - fill executed: value set to "Müller GmbH"

ui_click         sessionId="s1" locator="name=\"Speichern\""
              → - click executed at (142, 37)

ui_expect        sessionId="s1" locator="automationId=\"lblStatus\"" assertion="hasText" expected="Gespeichert"
              → expect hasText "Gespeichert": ok

ui_match_snapshot sessionId="s1" baselineName="kundenverwaltung-nach-migration"
              → matched=true: kundenverwaltung-nach-migration
```

If the migration module had moved a control out of its panel, the last call returns `matched=false`
with the exact lines that appeared and disappeared — the structural contract, not a paragraph:

```diff
   Window "Kundenverwaltung" [AutomationId=]
-    Edit "" [AutomationId=txtKunde]
+  Panel "" [AutomationId=pnlFooter]
+    Edit "" [AutomationId=txtKunde]
```

### Security

The server clicks and types into real desktop applications. Approval is the host's job: configure a
narrow allow-list for the executable you are migrating, and prefer session-scoped approvals over a
blanket "always allow". Anything the agent can click, a person can click — the difference is that
DeskDx writes down what it did.

## Related: the FlaUI MCPs

There are already several MCP servers that wrap FlaUI (shanselman/FlaUI-MCP, Winapp-MCP,
desktop-pilot-mcp, WinUI_MCP). They are remote control; the difference here is **discipline**, and it is
where the five differentiators above come from:

- **Locators, not refs.** Those servers typically hand the agent opaque element handles. DeskDx makes the
  locator the identity, so there is no server-side state to drift from the live tree.
- **Gate before acting.** An action that would silently do nothing instead reports which check failed.
- **Legacy realities.** VB6/MDI window resolution and the VB6 fallback chain exist because those targets
  break the assumptions modern frameworks satisfy.
- **Text, not pixels.** Snapshots and diffs are text — readable by an agent and reviewable in git.
- **Evidence.** A dialog watcher, a call log and committed baselines, all dogfooded against a real VB6
  application (PhotoDemon) before the MCP server was built on top.

## Repository layout

```
src/DeskDx/                  the engine (library)
tools/TestTarget/          a deterministic WinForms test vehicle
tools/TestTarget.Mcp/      the same form under a separate image name (parallel test suites)
tools/DeskDx.Mcp/            the MCP server (stdio)
tools/DeskDx.Dogfood/        characterization run against PhotoDemon (VB6)
tools/DeskDx.Mcp.Demo/       the agent-story script, transcript as evidence
tests/DeskDx.Tests/          engine acceptance tests (N, T, G, E, D, S)
tests/DeskDx.Mcp.Tests/      MCP acceptance tests (M1–M8)
```

Build and test:

```
dotnet build DeskDx.slnx -warnaserror
dotnet test DeskDx.slnx --no-build
```

## License

See [LICENSE](LICENSE).
