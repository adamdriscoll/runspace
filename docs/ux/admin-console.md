# Admin console: visual and interaction specification

Status: proposed design. **Observed reference facts** come from [PowerGUI inspection](../research/powergui-3.8.md). All modern dimensions, colors, shortcuts, and behavior below are **targets**, not measurements of a running PowerGUI instance.

This is the original visual proposal. The later [reference run and implemented foundation](../foundation.md#actual-powergui-reference-run) use observed gray pane chrome and deliberately defer kits, full docking, and broader workspace features.

## Recognizable shell

Use a light, dense desktop-console presentation by default. The visual hierarchy is resource tree, object results, contextual operations, then execution details. Avoid card dashboards, an oversized navigation rail, large rounded panels, gratuitous shadows, and a terminal-first layout.

```text
+--------------------------------------------------------------------------------------+
| Runspace - Local workspace                                             window controls |
+--------------------------------------------------------------------------------------+
| File    View    Tools    Help                                                         |
| Back  Forward | Refresh  Stop | Console Kits | Session: Local                         |
+----------------------+----------------------------------------+----------------------+
| Navigation           | Results | History                      | Actions              |
+----------------------+----------------------------------------+----------------------+
| Local System         | Local System > Processes               | Selected: 2 objects  |
|   Processes          | Filter...                   Columns... |                      |
|   Provider Drives    +----------------------------------------+ General              |
|     FileSystem       | Name          Id      CPU      Memory  |   Properties...      |
|     Environment      | -------------------------------------- |   Copy               |
|   Environment        | app-a         1234    0.3       40 MB  |   Export...          |
|                      | app-b         5678    1.8       62 MB  |                      |
| Enabled Console Kits |                                        | Process              |
|                      |                                        |   Stop process...    |
|                      +----------------------------------------+                      |
|                      | Diagnostics [collapsed unless needed]  |                      |
+----------------------+----------------------------------------+----------------------+
| Ready | Local / PowerShell 7 | 240 objects, 2 selected | Last query: 0.4 s              |
+--------------------------------------------------------------------------------------+
```

The reference designer uses 200-wide side-pane contents, a 50-high top bar region, a 22-high status strip, and Tahoma 8.25 pt in a 979 x 677 client area. Its layout initialization makes results/history/chart tabbed documents, and initially closes some tool windows. These facts motivate compact proportions but do not dictate a portable layout. [PG-04, PG-05]

## Proposed layout targets

All dimensions are Avalonia device-independent pixels. OS title bars are separate from client-area targets.

| Region | Initial target | Behavior |
| --- | --- | --- |
| Client area | 1200 x 800; usable at 1000 x 680 | Restore size/location while keeping the window on an available display |
| Menu and toolbar | Approximately 24 + 28 high | Stable command placement; overflow rather than clipped commands |
| Navigation | 220 wide; minimum 160 | Vertical splitter, lazy child expansion, independent scroll |
| Actions | 220 wide; minimum 180 | Vertical splitter, grouped links/buttons, visible empty-selection guidance |
| Result area | Remaining width | Largest region; no decorative padding around the grid |
| Pane captions/document tabs | 24-28 high | Obvious focus, close/collapse controls with tooltips |
| Grid rows | 22-24 high | Compact default; user-selectable comfortable density |
| Diagnostics | Collapsed normally; about 160 high when expanded | Horizontal splitter; do not repeatedly steal focus on errors |
| Status strip | 22-24 high | Runtime/session, counts, execution state, progress, elapsed time |

Use splitters and document tabs for the first implementation. Full floating/redockable tool windows can follow after the core shell is usable; choosing a docking dependency is not yet settled.

Provide **View -> Reset layout**. The action pane remains visible by default, even though the original closes it during initialization; discoverability is an intentional modern deviation.

## Proposed visual tokens

These values form an original starting palette. They are not extracted PowerGUI theme values.

| Token | Light target |
| --- | --- |
| Chrome background | `#F0F0F0` |
| Content background | `#FFFFFF` |
| Pane-header gradient | `#EDF3FA` to `#D9E6F5` |
| Border/grid separator | `#C6CDD5` / `#E4E8ED` |
| Primary/muted text | `#1F2328` / `#59636E` |
| Selected row background/text | `#245A91` / `#FFFFFF` |
| Link/action accent | `#1F5A95` |
| Focus outline | `#155FAD` |
| Warning/error | Semantic icon and text as well as color |

The soft blue-gray pane header is a proposed classic-console cue; its exact fidelity remains unverified. Adjust against a controlled reference capture rather than claiming it is an observed PowerGUI gradient.

Use approximately 12-DIP UI text. Prefer Segoe UI on Windows, the platform UI face on macOS, and an available portable sans-serif on Linux; do not depend on redistributing Tahoma. Use a readable monospace face for history/PowerShell. Validate line metrics across platforms.

Create or license original 16/20-DIP icons for folders, providers, query nodes, refresh, stop, properties, and actions. Keep their silhouettes legible at compact sizes. Never reuse extracted logos or icons.

Provide explicit light/dark/high-contrast choices eventually; first validate the light default. High-contrast operation, visible focus, and non-color status indicators are baseline accessibility requirements, not optional polish.

## Navigation and provider drives

Top-level resource groups come from enabled Console Kits. A session selector identifies the environment behind those resources. Provider Drives is a built-in view contributed by the Local System kit, not a replacement for the rest of the tree.

Expanding a provider-drive node requests children only when its provider supports container navigation. Selecting it queries the current location. Display the provider name and root rather than treating `Env:` or `Variable:` like disks.

**Add drive** prompts for session, provider, name, root, and provider-supported options. Validate in the owning session, show PowerShell's actual error if creation fails, and refresh the list on success. Credentials are requested only when supported and never stored in workspace files.

**Remove drive** is offered only where permitted, with a clear explanation and confirmation. It must not imply deleting the underlying files or resources. Do not silently force removal or recreate a removed drive.

Folder nodes organize content without automatically executing a script. Query/provider nodes execute when explicitly selected after kit trust is established; parameterized queries prompt before execution. Background refresh of mutating actions is never allowed.

Show separate states for not loaded, loading, no children, unsupported provider operation, access denied, and execution failure.

## Result grid

Preserve read-only, whole-row, multi-selection with sortable/reorderable/resizable columns. The reference uses virtual cell retrieval and hides row headers. [PG-06, PG-13]

Use kit-defined default columns before dynamic property discovery. Fallback discovery must be bounded and explain heterogeneous/missing properties. Distinguish missing, null, pending, and failed evaluation; a getter failure must not become a blank cell indistinguishable from null.

Column menus allow show/hide, restore defaults, and copy values. Preserve typed sorting/filtering so numeric values do not sort lexically. A client-side display filter does not rerun a query; label query parameters separately.

Changing a filter cannot leave hidden rows selected without an explicit indication. Proposed default: clear selections that leave the visible set and report the change.

Ctrl/Command selection and Shift range selection follow platform conventions. Right-click on an unselected row first selects that row; right-click on an already selected row preserves the multi-selection. The context menu and action pane must resolve the same eligible actions.

Double-click/Enter invokes the configured default action. Prefer Properties as the initial safe default for built-in objects; never make a destructive action the default. The reference dispatches a configurable default item, not invariably a properties dialog. [PG-07]

## Contextual actions and prompts

The action pane shows a selection summary and named Action Groups. Actions declare supported object types, selection cardinality, platform/module prerequisites, parameter definitions, and result behavior.

Keep common operations such as Properties, Copy, and Export stable. Show known but unavailable operations disabled with a reason; do not flood the pane with irrelevant domain actions.

Selection changes must update eligibility without running arbitrary kit scripts. Freeze the selected input at invocation creation; changes to UI selection while prompting must not retarget the operation.

Prompt with appropriate editors for strings, switches, numbers, enumerations, paths, credentials, and secure input. Required fields, validation messages, and cancellation are explicit. Use PowerShell parameter metadata where applicable rather than creating a second inconsistent signature.

Destructive confirmation identifies the action, session/target, object count, and representative object names. Respect command-supported `ShouldProcess`/`WhatIf`/confirmation; do not claim a preview exists when the command cannot provide one.

After execution, apply the action's declared outcome: retain the current view, refresh it, replace it, or open a Related View. Related Views retain breadcrumbs/back navigation.

## History and diagnostics

**History** is a document tab containing the PowerShell representation, timestamp, originating resource/action, selection summary, duration, and outcome. Copy/export excludes secrets.

Label each entry as reproducible, dependent on current session state, or display-only. Arbitrary live objects cannot always be reconstructed from text; never offer a misleading replay button.

**Diagnostics** is a separate pane for output/error/warning/verbose/debug/information/progress records and host prompts. Filters may reduce noise, but errors and partial results remain visible in the invocation status.

A failure can reveal diagnostics without moving keyboard focus away from the user's current control. A prompt is a deliberate modal interaction associated with a specific invocation.

## Execution and persistence

| State | Required presentation |
| --- | --- |
| Queued/running | Resource name, elapsed time, progress when known, available Stop |
| Awaiting input | Prompt attached to the invocation; underlying result remains understandable |
| Stopping | Immediate state change; no claim that cancellation already completed |
| Completed | Object count and duration |
| Completed with errors | Partial-result indication plus visible error count |
| Failed | Actionable error and diagnostics; distinct from "no results" |
| Cancelled | Incomplete results identified; no success indicator |

Refresh does not overlap pipelines in the same session. Rapid navigation must not let an older invocation overwrite a newer selected view.

Persist pane sizes, visibility, selected kit/node identifiers, and per-view column/sort/filter preferences. Do not persist live objects, secrets, drive credentials, or raw host prompts. On restart, unavailable sessions/resources remain visibly unresolved rather than automatically reconnecting or executing code.

## Proposed keyboard map

| Input | Operation |
| --- | --- |
| F5 | Refresh active resource |
| Alt+Left / Alt+Right | Navigate back/forward; adapt where OS conventions conflict |
| Ctrl/Command+F | Focus result filter |
| Ctrl/Command+A / C | Select all visible rows / copy selection |
| Enter | Safe configured default action |
| Escape | Cancel an open prompt; request stop only when execution context has focus |
| Tab / Shift+Tab | Traverse panes and controls with visible focus |
| Platform menu key / Shift+F10 | Open focused object's context menu where supported |

Expose command gestures in menus/tooltips and test collisions on each OS. This is a proposal, not a recovered historical shortcut map.

## Fidelity acceptance

At a 1200 x 800 client area and 100% scale, the tree/grid/action proportions should match the declared targets and show at least 20 result rows with diagnostics collapsed. At 150% and 200% scale, no labels, commands, or splitter controls are clipped.

Capture original reference and modern screenshots at matched client sizes/DPI with benign data. Compare region placement, information density, grid affordances, pane chrome, and action grouping; record intentional deviations. Do not score copied branding or artwork.

Behavioral walkthroughs must cover multi-selection, related navigation, prompts, a failing query, partial errors, cancellation, saved layout, and keyboard-only operation. Until those comparisons exist, describe the UI as **PowerGUI-inspired**, not pixel-identical.

The [2026-10-06 fixture acceptance record](../shell-validation.md) documents the implemented size/scale, keyboard, contrast, selection, and adapter checks. Its offscreen captures are not native-monitor or screen-reader certification; the remaining platform qualifications are listed there.
