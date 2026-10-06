# Product vision

Status: proposed product foundation. User-required technologies and product direction are identified in the [README](../README.md); this document is not an implementation completion report.

## Product promise

Runspace turns PowerShell's object model into a navigable administration workbench. An administrator should be able to discover a resource, understand the returned objects, act on a deliberate selection, and inspect the command behind that action.

The goal is to recreate **PowerGUI Admin Console's mental model and desktop character**, not merely embed a terminal in an Avalonia window. Its separate script editor is not the starting product.

The reference supports a left navigation tree, center object results, right contextual actions, document-style result/history/chart views, and a diagnostic log. The result grid retains objects and supports selection-dependent operations. [Reference: PG-04 through PG-13](research/powergui-3.8.md#source-ledger)

## Who it is for

| Administrator | Core need |
| --- | --- |
| PowerShell-capable operator | Inspect and operate on systems faster than assembling one-off pipelines |
| Occasional PowerShell user | Discover appropriate operations and parameters without hiding their meaning |
| Console Kit author | Package a coherent administration experience around existing PowerShell modules |

The first release should optimize for a single administrator working locally. Fleet management, multi-user authorization, remote orchestration, and a hosted web product are different problems.

## Preserve, modernize, defer

| Preserve | Modernize | Defer |
| --- | --- | --- |
| Tree -> objects -> selection -> actions | Avalonia desktop UI across Windows, macOS, Linux | Full script IDE/debugger |
| Dense, practical, multi-pane layout | High-DPI rendering, keyboard access, screen-reader semantics | Marketplace and automatic kit updates |
| PowerShell-backed queries and operations | PowerShell 7 modules rather than old snap-in assumptions | Legacy `.powerpack` importer |
| Named reusable administration collections | Versioned Console Kits with explicit compatibility/trust | Remoting/session-farm management |
| Visible command history and diagnostics | Asynchronous execution, cancellation, structured stream display | Chart designer and snapshot comparison |
| Saved view/layout preferences | Per-user storage and honest platform capabilities | Native third-party UI extensions |

Defer does not mean reject. The original distribution has charts, snapshots, an editor, update machinery, and numerous domain packs; reproducing all of those at once would delay validating the defining experience.

## First useful console

Provide an original built-in **Local System** Console Kit with:

- **Processes:** useful default columns, sorting, filtering, properties, and an explicitly confirmed process-stop action using disposable test processes during development.
- **Provider Drives:** enumerate drives from the active session; add/remove eligible drives; browse navigation-capable providers without assuming every drive is a directory tree.
- **Environment:** inspect environment entries with original query definitions and safe viewing actions.

Provider drives are session-scoped PowerShell resources, not a list of physical disks. Built-in and newly added drives should appear without restarting the application. Unsupported provider operations should have clear explanations.

Windows-only views such as Services, Registry, Event Logs, and WMI/CIM administration can follow as capability-gated additions. Active Directory, Hyper-V, Exchange, and VMware experiences are later independent kits, not prerequisites for portability.

## Essential workflows

**Discover and inspect:** enable a kit, expand a resource group, run a query, inspect typed rows and full object properties, change columns, and follow a related view.

**Act deliberately:** choose one or several objects, see applicable actions, supply typed parameters, review a meaningful confirmation if needed, execute, and observe the declared refresh or result transition.

**Understand execution:** see whether the operation is queued, running, prompting, stopping, completed, completed with errors, failed, or cancelled. Inspect progress, diagnostics, and the PowerShell representation.

**Customize and share:** persist a workspace's layout and view choices; later, author an original kit and export it without credentials or private session data.

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

## Definition of the first milestone

A user can open Runspace on all three target operating systems, use the same tree/grid/action workflow to inspect processes and provider drives, perform a safe contextual operation, cancel a cooperative query, and understand a failed query without using a separate terminal.

Detailed acceptance gates are in the [roadmap](roadmap.md). Visual targets are in the [console experience](ux/admin-console.md); compatibility constraints are in the [platform research](research/modern-platform.md).
