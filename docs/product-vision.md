# Product vision

Status: product direction and design reference, not an implementation completion report. See the [implemented foundation](foundation.md) for current behavior and [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24) for planned work.

## Product promise

Runspace turns PowerShell's object model into a navigable administration workbench. An administrator should be able to discover a resource, understand the returned objects, act on a deliberate selection, and inspect the command behind that action.

PowerGUI is the inspiration for the tree/object/action workflow. Runspace is a native administration console, not a terminal wrapper.

The console uses a left navigation tree, center object results, right contextual actions, history, and diagnostics. The result grid retains objects and supports selection-dependent operations.

## Who it is for

| Administrator | Core need |
| --- | --- |
| PowerShell-capable operator | Inspect and operate on systems faster than assembling one-off pipelines |
| Occasional PowerShell user | Discover appropriate operations and parameters without hiding their meaning |
| Console Kit author | Package a coherent administration experience around existing PowerShell modules |

The first release should optimize for a single administrator working locally. Fleet management, multi-user authorization, remote orchestration, and a hosted web product are different problems.

## Design priorities

| Principle | Direction |
| --- | --- |
| Tree -> objects -> selection -> actions | Native Avalonia desktop UI across Windows, macOS, Linux |
| Dense, practical, multi-pane layout | High-DPI rendering, keyboard access, screen-reader semantics |
| PowerShell-backed queries and operations | PowerShell 7 modules rather than old snap-in assumptions |
| Visible command history and diagnostics | Asynchronous execution, cancellation, structured stream display |
| Saved view/layout preferences | Per-user storage and honest platform capabilities |

Feature scope, sequencing, and acceptance gates belong in the issue tracker rather than this design reference.

## Built-in local administration

The built-in **Local System** views establish the administration workflow without requiring a kit loader:

- **Processes:** useful default columns, sorting, filtering, properties, and an explicitly confirmed process-stop action using disposable test processes during development.
- **Provider Drives:** enumerate drives from the active session; add/remove eligible drives; browse navigation-capable providers without assuming every drive is a directory tree.
- **Environment:** inspect environment entries with original query definitions and safe viewing actions.

Provider drives are session-scoped PowerShell resources, not a list of physical disks. Built-in and newly added drives should appear without restarting the application. Unsupported provider operations should have clear explanations.

The implemented console also includes capability-gated Windows views such as Services, Registry, Event Logs, and WMI/CIM administration. See the [usage guide](usage.md#built-in-views) for the current view list.

## Essential workflows

**Discover and inspect:** expand a resource group, run a query, inspect typed rows and full object properties, change columns, and follow a related view.

**Act deliberately:** choose one or several objects, see applicable actions, supply typed parameters, review a meaningful confirmation if needed, execute, and observe the declared refresh or result transition.

**Understand execution:** see whether the operation is queued, running, prompting, stopping, completed, completed with errors, failed, or cancelled. Inspect progress, diagnostics, and the PowerShell representation.

**Customize:** preserve deliberate layout and view preferences without persisting credentials or private live session data. The current implementation saves window/pane sizes only.

## Fidelity priorities

1. **Behavioral spine:** actual object selection and contextual operations, not text scraping.
2. **Spatial recognition:** left tree, center grid, right grouped actions, compact top chrome and bottom status.
3. **Information density:** rows, columns, and familiar desktop interactions take precedence over decorative spacing.
4. **PowerShell transparency:** reveal what is run; distinguish faithfully reproducible commands from session-dependent invocations.
5. **Visual details:** original compact icons, restrained pane headers, subtle borders, and a light classic-console theme, refined against reference captures.

Names, branding, historical artwork, and old Windows toolkit code are not fidelity requirements.

## Non-goals and guardrails

Do not present PowerShell as a sandbox. Imported kits and modules can execute arbitrary code with the administrator's process permissions; a declared read-only query is an authoring promise, not an enforcement mechanism.

Do not run elevated by default, execute downloaded kits on import, silently load arbitrary user profiles, or hide partial failures behind an empty table.

Do not claim that a cross-platform shell makes every administration domain cross-platform. The available modules/providers and the session's target determine what operations can work.

Do not claim `.powerpack` compatibility or complete PowerGUI parity in the first release.

Visual targets are in the [console experience](ux/admin-console.md); compatibility constraints are in the [platform research](research/modern-platform.md). Delivery status and acceptance work live in [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24).
