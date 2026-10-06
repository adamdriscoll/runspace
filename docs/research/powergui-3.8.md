# PowerGUI 3.8: clean-room reference findings

Inspection date: 2026-10-05. This is a behavioral and structural reference, not a port of the original implementation.

The first pass below was static inspection. A later pass on the same date successfully ran the extracted managed console in a session-local portable layout and captured its startup/process view. See [the implementation reference run](../foundation.md#actual-powergui-reference-run) for the observed appearance and deliberate modern differences.

## Provenance and method

| Item | Observed value |
| --- | --- |
| Supplied installer | `PowerGUI.3.8.0.129.msi` |
| MSI product name | Quest PowerGUI 3.8 |
| MSI product version | `3.8.0.129` |
| MSI manufacturer | Quest Software, Inc. |
| Installer size | 15,116,288 bytes |
| Installer SHA-256 | `F3570BD7DBE4B2D5B2FC41FF1D1D3E5B8173229BB69116678405024E0CEA6EEA` |
| Embedded cabinet | `Cabs.w1.cab`; MSI `Media` last sequence 559 |
| Inspection tools | Windows Installer read-only database access, cabinet expansion, XML parsing, ILSpy MCP |

The installer was supplied at `C:\Users\AdamDriscoll\Downloads\PowerGUI.3.8.0.129.msi`. Its embedded cabinet was read as data and expanded into session artifacts outside the repository. The MSI was **not installed**. No executable or bundled script was run during this initial static pass; the later portable reference run is documented separately.

ILSpy was used to inspect assembly/type metadata and selected methods. Some ILSpy operations returned decompiled code rather than the requested summaries; none of that code is reproduced in this repository. Findings below are paraphrased facts with assembly/type/method anchors, not implementation recipes.

The reference binaries, scripts, icons, embedded images, and extracted source are not project dependencies and must not be committed. Ownership and redistribution permissions have not been established. This inspection does not assert a right to reuse those materials.

## Evidence levels

- **Observed**: present in installer metadata, assembly metadata, an inspected method, or bundled XML.
- **Inferred**: a likely interaction implied by those observations, but not exercised in the running product.
- **Proposed**: a new Runspace behavior or design decision, documented elsewhere.

No running-console screenshots were captured during this initial pass. Static dimensions below are designer defaults, not measured rendered geometry; system theme, DPI, localization, runtime initialization, and saved layouts can change the result.

## Source ledger

These identifiers are local primary-source anchors. To reproduce an observation, obtain the same installer hash, extract the cabinet without installing, and open the named assembly in ILSpy. The ledger does not require the original research session's temporary paths.

| ID | Source inspected | What it establishes |
| --- | --- | --- |
| PG-01 | MSI `Property`, `Media`, `_Streams`, and `File` tables | Product identity, cabinet, assembly inventory, bundled packages |
| PG-02 | `AdminConsole.exe.config` | .NET Framework v4.0 and v2.0.50727 startup declarations, with legacy v2 activation policy |
| PG-03 | `AdminConsole.exe`, type inventory | Small launcher including `Quest.PowerGUI.Program`; not the principal UI implementation |
| PG-04 | `UI.Forms.dll`, `Quest.PowerGUI.UI.WinForm.MainForm.InitializeComponent` | WinForms shell, menu/toolbar arrangement, pane placement, static dimensions and font |
| PG-05 | `UI.Forms.dll`, `MainForm.BuildLayout`, `InitializeToolWindows`, `LoadToolWindowLayout` | Results/history/chart become tabbed documents; initial pane closing; persisted docking layout restoration |
| PG-06 | `UI.Controls.dll`, `Quest.PowerGUI.UI.WinForm.Controls.ResultCtrl.InitializeComponent` | Read-only virtual result grid, full-row selection, movable columns, filter region and related-link strips |
| PG-07 | `UI.Controls.dll`, `ResultCtrl.dataGridView1_CellDoubleClick` | Double-click on a valid result cell dispatches the default item |
| PG-08 | `ViewModel.dll`, `Quest.PowerGUI.ViewModel.ActionsViewModel` members and `ResultSelectionChanged` | Selected-object/type-aware action machinery; selection changes trigger command selection |
| PG-09 | `Engine.dll`, `Quest.PowerGUI.Engine.Shared.ScriptContainer` members; `Quest.PowerGUI.RequireSelection` | Selection requirements, display-result policy, applicable types, supported UI flags; legacy selection enum is `None`, `Yes`, `No` |
| PG-10 | `Engine.Shell.dll`, `Quest.PowerGUI.Engine.Shell.NodeScriptItem` | Input objects, links, dynamic/subitems, filters, sorting, computed properties |
| PG-11 | `ViewModel.dll`, `Quest.PowerGUI.ViewModel.HistoryScriptBuilder.BuildScript` | PowerShell script history assembled from query/link/action execution context and projected properties |
| PG-12 | `Engine.Shell.dll`, `MonadShell` and `Host` type/member inventories | Embedded runspace hosting, modules/snap-ins, profiles, worker execution, errors, progress, credential/choice/parameter prompts |
| PG-13 | `ViewModel.dll`, `Quest.PowerGUI.ViewModel.ResultCache` members | Source/result objects, cached cell values, property sorting/filtering, columns and incomplete-result state |
| PG-14 | `UI.Controls.dll`, `Quest.PowerGUI.UI.WinForm.Controls.PowerPackManagerForm` members | Import, export, uninstall, properties, update checks, installed-pack selection |
| PG-15 | `Configuration.dll`, `Quest.PowerGUI.Configuration.XML` type inventory | XML-backed containers, commands, scripts, filters, configuration; separate formatting definitions also exist |
| PG-16 | `Local.powerpack` XML metadata and navigation definitions | Local-system inventory, explicit view columns, type-specific results, action metadata |
| PG-17 | `Export_Actions.powerpack` XML structure | A pack can contribute actions without a substantial resource-tree contribution |
| PG-18 | `AdminConsoleMainPage.zip` entry inventory | Bundled HTML/JavaScript/CSS start-page content and images |

## Original architecture: useful facts, not a template

**Observed:** the product separates launcher, configuration, engine, PowerShell shell, view models, and WinForms UI across assemblies. `ViewModel.dll` contains navigation, results, links, actions, logging, history, parameter presenters, and charts. [PG-01, PG-03, PG-08, PG-11, PG-13, PG-15]

**Observed:** the distribution contains Actipro bar/docking/syntax-editor assemblies and Infragistics grid/explorer-bar/chart assemblies. `MainForm` uses Actipro docking/bar types; the result control itself uses a custom WinForms `DataGridView`, not an Infragistics grid. The pack-manager control has an `UltraGrid`, and the actions visualizer has explorer-bar types. Do not assume every table uses the same toolkit. [PG-01, PG-04, PG-06, PG-14]

**Observed:** the PowerShell host has distinct prompt, credential, choice, secure-input, progress, output, and execution-state types. `MonadShell` exposes a runspace and machinery for modules, snap-ins, profiles, and asynchronous worker execution. These are capabilities visible in the inspected surface, not proof of all runtime guarantees. [PG-12]

**Design implication:** preserve object-aware administration and execution transparency. Replace the legacy toolkit, static/singleton coupling, Win32 assumptions, and obsolete snap-in environment rather than transplanting their architecture.

## Observed console anatomy

The initial designer arrangement is a navigation tree on the **left**, results in the **center**, contextual actions on the **right**, and a log tool window below the center. Results, history, and chart windows are registered as tabbed documents during layout construction. [PG-04, PG-05]

| Static designer fact | Value | Qualification |
| --- | --- | --- |
| Main client area | 979 x 677 | Not outer-window size |
| Top bar area | 50 high | Contains menu and a second toolbar row |
| Status strip | 22 high | At the bottom |
| Side containers | 204 wide each | Content panes are 200 wide |
| Center design width | 571 | Before runtime layout changes |
| Bottom log container | 113 high | Log is closed during initialization |
| Shell/results font | Tahoma, 8.25 pt | Roughly 11 pixels at 96 DPI; not a portable font requirement |
| Grid background | System window color | Exact RGB depends on the system |
| Grid-line color | System inactive-caption gradient color | Not a fixed project palette |

The shell includes File, Tools, Help, and View menu definitions, plus commands for results, history, log, actions, navigation, chart, cancellation, library management, profiles, pack management, and launching the separate editor/PowerShell console. Those are observed command definitions; their exact localization and final ordering were not verified at runtime. [PG-04]

`InitializeToolWindows` closes history, log, and actions before loading a saved layout. Consequently, the three-pane designer arrangement is **not evidence that all panes are visible on every first launch**. Runspace's proposed layout intentionally keeps a discoverable action pane; that is a design choice, not a claim about PowerGUI startup. [PG-05]

## Objects, actions, and history

**Observed:** the result grid is read-only, suppresses row headers, supports column reordering, selects entire rows, and requests cell values in virtual mode. Filters occupy a separate region above the grid. Two compact strips expose related links and filtering commands. [PG-06]

**Observed:** double-click dispatches the default item; the actual result depends on the configured default action/link. It is not evidence that double-click always opens properties. [PG-07, PG-08]

**Observed:** action selection uses current/selected object types, and script containers carry selection and result-display metadata. The legacy selection enum should not be interpreted as a count range without further study. [PG-08, PG-09]

**Observed:** node-script items contain object input, links, dynamic children, filters, sorting, and computed property definitions. Result caching retains objects separately from their displayed values. [PG-10, PG-13]

**Observed:** history is a PowerShell representation built from execution context, not merely a message log. Inspected methods incorporate query/link/action information and property projection. Fidelity and replay of arbitrary selected live objects were not tested. [PG-11]

**Inferred:** the distinctive workflow is "navigate -> inspect objects -> select -> act -> inspect the next result -> see the PowerShell behind the operation." This is the behavioral spine to preserve.

## PowerPacks

**Observed:** the inspected `.powerpack` files are UTF-8 XML documents, not ZIP archives. They use a `configuration` root with containers, items, scripts, commands, parameters, values, icons, and type definitions. `Local.powerpack` contributes sections for metadata, navigation, actions, scripts, types, icons, and chart presets. [PG-15, PG-16, PG-17]

Pack metadata includes name, description, required snap-ins, version, creation date, update URL, home page, icon, and console version. Embedded icons and scripts are reference materials only, not reusable project assets. [PG-16]

The local-system navigation includes Processes, Services, Event Logs, Network Configuration, Registry, Drives, Shares, Local Users and Groups, and a WMI Browser. Processes define familiar task-manager-like columns; Services define status/name/display name. Drives declare `PSDriveInfo` result types, establishing that provider-drive administration is part of the reference concept. The drive query and interactive behavior were not executed. [PG-16]

Bundled pack files include local system, Active Directory/computers, Exchange 2007, Hyper-V, VMware, HTML reporting, and export actions. Their presence does not establish current compatibility, licensing, or completeness. [PG-01]

**Design implication:** a Console Kit should be able to add a tree, actions, or both. Its data/view/action definitions should remain independent of the desktop UI toolkit. Modern packages need their own format and trust model; legacy XML import is optional future work, not required for the first version.

## Fidelity gaps and next reference pass

| Unknown | Why it matters | How to resolve without copying |
| --- | --- | --- |
| Actual theme colors, hover states, docking captions | Static system-color references do not provide a screenshot baseline | Capture the running console in an isolated Windows reference environment |
| First-launch layout and action-pane reveal timing | Initialization closes several windows | Compare clean-profile startup and selection-dependent reveal behavior |
| Exact prompts and action refresh/link behavior | Metadata reveals capabilities, not complete interactions | Exercise benign queries and documented actions with disposable test objects |
| Filtering and sorting execution semantics | Member inventories do not prove locality or type comparison rules | Trace representative methods and observe a fixed sample result set |
| Pack editing/upgrade conflict behavior | Management methods exist, but lifecycle was not exercised | Observe import/export/upgrade using a disposable user-owned pack |
| History replay with object selection | A textual representation may depend on prior session state | Compare history text with actual invocation input in a controlled session |

Do not call the modern UI pixel-faithful until this reference pass exists. Current findings are sufficient to specify a recognizable console shell and its core workflows, not to claim visual or functional parity.
