# Using Runspace

Runspace presents a resource tree on the left, typed object tables in the center, and grouped contextual actions on the right. PowerShell History and Diagnostics explain what ran and how it finished.

## Launching Runspace

### Release packages

Download the package matching your platform from [GitHub Releases](https://github.com/adamdriscoll/runspace/releases). All packages include the .NET runtime and embedded PowerShell; neither needs to be installed separately. Extract the complete ZIP, not just its executable.

| Platform | Release assets | Install and launch |
| --- | --- | --- |
| Windows x64 | `Runspace-<version>-win-x64.zip` and `.msi` | Extract the ZIP and run `Runspace.Desktop.exe`, or run the MSI (requires administrator permission) and launch **Runspace** from the Start menu. The MSI installs in Program Files and can be removed through Windows Installed apps. |
| Ubuntu 24.04 x64 | `Runspace-<version>-linux-x64.zip` | Install the [native prerequisites](#linux-prerequisites), extract with `unzip`, and launch `./Runspace.Desktop` in a graphical session. |
| macOS 15 ARM64 | `Runspace-<version>-osx-arm64.zip` and `.dmg` | Open the DMG and drag **Runspace.app** to **Applications**, or extract the ZIP and move the app there. Launch **Runspace** from Applications. |

Packages are **unsigned**, and the macOS app is not notarized. Windows may show an unknown-publisher/SmartScreen warning. macOS Gatekeeper may block the downloaded app; after verifying its source, use **System Settings -> Privacy & Security -> Open Anyway** if macOS offers that option. Do not disable OS security globally. Managed-device policy may prevent launching unsigned applications.

Release packaging preserves Unix executable permissions. If another extraction tool removes them, run `chmod +x ./Runspace.Desktop` on Linux, or `chmod +x /Applications/Runspace.app/Contents/MacOS/Runspace.Desktop` on macOS. Command-line macOS launch uses `open /Applications/Runspace.app`.

The [support matrix](#supported-platform-matrix) still applies; packaging does not broaden the verified OS/architecture scope.

### Source and CI outputs

To run from source, install the .NET 10 SDK matching `global.json` and follow the [README quick start](../README.md#quick-start). The PowerShell engine is embedded; a separate PowerShell installation is not required.

On macOS/Linux, use this project path after restoring and building the solution:

```sh
dotnet run --project src/Runspace.Desktop/Runspace.Desktop.csproj
```

Linux requires a graphical session and native dependencies; see [Linux prerequisites](#linux-prerequisites). The default backend uses X11 or XWayland, not native Wayland.

The [Build and test workflow](https://github.com/adamdriscoll/runspace/actions/workflows/build.yml) produces self-contained outputs for `win-x64`, `linux-x64`, and `osx-arm64`. Download the matching `runspace-<rid>` artifact from a run whose **Published payload** job also passed. Extract the entire payload, including `runtimes` and `ref`; do not copy only the executable. These are build artifacts, not installers.

Run `.\Runspace.Desktop.exe` on Windows. On macOS/Linux, restore the executable bit if the artifact download removed it, then launch `./Runspace.Desktop`:

```sh
chmod +x ./Runspace.Desktop
./Runspace.Desktop
```

A separate .NET runtime, .NET SDK, or PowerShell installation is not required for these self-contained outputs.

For build, test, debugger, and publish instructions, see [Contributing](../CONTRIBUTING.md).

## Supported platform matrix

Support is limited to the OS/architecture and validation scope below, not every OS supported separately by .NET, PowerShell, or Avalonia. [Published-build validation](publish-validation.md) records the commands, exact versions, results, and remaining qualifications.

| OS / architecture | Artifact | Validation and support scope |
| --- | --- | --- |
| Ubuntu 24.04 LTS / x64 | `linux-x64` | Verified on clean Ubuntu 24.04.5 with X11/Xvfb, no installed PowerShell/.NET, and no checkout. Supported baseline with the native prerequisites below; a physical desktop/XWayland session remains a separate qualification. |
| Windows 11 / x64 | `win-x64` | Verified on build 26200 with the published payload relocated, empty executable/module search paths, and a temporary home. Provisional desktop baseline; this host was not a pristine Windows installation. Other Windows 11 builds are not yet verified. |
| macOS 15 / ARM64 | `osx-arm64` | Verified on macOS 15.7.9 ARM64 using a relocated payload, temporary home, empty application search paths, and the real native desktop backend. Provisional desktop baseline; hosted-runner isolation is not pristine-install/GPU certification. Other macOS 15 updates are not yet verified. |
| Windows Server 2025 / x64 | `win-x64` | Verified on hosted build 26100 with isolated application search paths. Validation-only coverage, not a desktop-product support commitment. |

Other versions and architectures are **unverified and outside the current support matrix**, including Windows ARM64/x86, macOS Intel or macOS 26, other Linux distributions/architectures, and musl/Alpine. Native Wayland, headless operation without a display server, Windows PowerShell 5.1 hosting, trimming, Native AOT, and single-file publication are not supported deployment modes.

### Linux prerequisites

On Ubuntu 24.04, provision the OS runtime/graphics packages before launching:

```sh
sudo apt-get update
sudo apt-get install --no-install-recommends \
  libicu74 libssl3t64 zlib1g libgcc-s1 libstdc++6 libgssapi-krb5-2 \
  libx11-6 libice6 libsm6 libfontconfig1 libxext6 libxrandr2 libxi6 libxcursor1 \
  libglib2.0-0t64 fonts-dejavu-core
```

Use a working X11 desktop or an XWayland-enabled Wayland desktop, with `DISPLAY` and any required `XAUTHORITY` set. Self-contained .NET publication does not bundle the display server, system fonts, ICU, OpenSSL, or all OS libraries. The automated clean-container check also installs `xvfb`, `xauth`, and `python3` for its harness; these are not application requirements.

### Embedded PowerShell limitations

The SDK-only host is not the `pwsh` command-line distribution. **`Start-Job` is not supported** because no `pwsh` executable is shipped beside the engine; installing one elsewhere does not turn this into a validated capability. PowerShell profiles remain disabled.

Management, Utility, and Security module manifests and implementations ship with the payload. Windows also includes the CIM and Diagnostics SDK assets. Additional administration modules, remoting, Windows PowerShell compatibility, and their external dependencies are not supplied or certified by this validation. A missing bundled dependency produces an explicit diagnostic directing you to extract the complete matching artifact again; changing `PSModulePath` is not a repair.

## Built-in views

| View | Available operations |
| --- | --- |
| Processes | Resource usage, live properties, start/confirmed stop, Windows priority, related modules and threads |
| Services | Windows service table and confirmed start/stop/restart |
| Event Logs | Windows logs and related recent-event tables |
| Network Configuration | Local IP properties and network-interface objects |
| Registry | Windows provider roots, keys, and object properties |
| Drives | Session provider drives, create/remove drives, and browse their resources |
| Shares | Windows share objects |
| Local Users and Groups | Windows accounts/groups and related membership |
| WMI Browser | Windows CIM namespaces, classes, and instances |
| Environment | Environment entries and edit/remove actions |
| PowerShell Providers / Drives | Discover the embedded session's providers and browse available roots |
| Network / Managed Computers | Local computer information only |

Windows-specific views remain identifiable on other platforms and explain their limitations. Availability depends on the OS, embedded runtime, modules, and account permissions. Missing capabilities and access failures appear in Diagnostics, not as successful empty tables.

A provider drive is a session resource root, not necessarily a physical disk. FileSystem, Environment, Registry, and other providers expose different capabilities.

## Browse and inspect

Select a resource in the navigation tree to load its objects. Filter the displayed table, click column headers to sort typed values, and drag or resize columns. **Columns...** also provides keyboard-operable visibility, width, order, numeric-aware sorting, and default-column restoration. At least one column must remain visible.

Select one or multiple rows to see applicable operations. The Actions pane and row context menu use the same selection rules. Double-click or press **Enter** to inspect properties or follow a related view; use **Back/Forward** to navigate between resources.

**Tools -> Go to provider path** accepts literal PowerShell provider paths, such as `Env:` or `C:\Windows`. Available providers and access permissions determine which paths can be browsed.

Provider-tree expansion loads only that location's containers, not their descendants. Flat providers such as Environment, Alias, Variable, and Function still display their items in Results without invented child containers or mutation actions. Use the built-in **Environment** view to edit environment variables. Failed expansion remains retryable; partial expansion keeps usable children and shows an incomplete-navigation warning. Collapse and expand to retry.

The tree loads at most 200 children per location and 1,000 nodes overall. Omitted paths get a visible limit notice; collapse a provider/registry branch to unload it, or use a literal Location to browse an omitted path. Back/Forward retains the latest 100 routes and **requeries**, rather than caching the old objects.

| Shortcut | Operation |
| --- | --- |
| F5 | Refresh the active resource outside editor focus |
| Alt+Left / Alt+Right | Navigate back/forward outside the editor |
| Ctrl/Command+F | Find in a focused script editor; otherwise focus the result filter |
| Ctrl/Command+A | Select all visible result rows |
| Ctrl/Command+C | Copy selected rows |
| Enter | Inspect properties or follow the row's related view |
| Shift+F10 / menu key | Open the focused result table's context menu |
| Tab / Shift+Tab | Traverse controls, including the pane splitters |
| Left / Right on a splitter | Resize its neighboring panes within their minimum widths |
| Escape in a dialog | Dismiss properties, column controls, or a prompt |

Use **Export table...** to save the visible table as CSV. Filtering changes displayed rows without rerunning the query. Rows hidden by the filter lose their selection, with an explicit status message; visible selected rows remain selected. Selection alone does not execute an action.

**View -> High contrast** switches the console and subsequently opened dialogs to black/white chrome with yellow command/focus accents. It is an explicit, saved workspace choice, not automatic detection of the OS contrast palette. Controls expose accessible names; selected result rows expose Selected/Not selected item status, and selection/outcome/error text does not depend on color. See the [fixture acceptance evidence and native accessibility limits](shell-validation.md) before treating this as screen-reader certification.

## Run actions deliberately

Operations run with your current account's permissions; UI confirmation does not grant elevated rights. Actions that change resources ask for parameters or confirmation where appropriate.

Selection is frozen when an action begins, before parameter or host prompts open. Changing the selected row while a prompt is open never retargets that action. Command-backed actions offer **Preview only (PowerShell WhatIf)**; commands receive typed `WhatIf`/`Confirm` values only when they support them. Preview reports what PowerShell would do without performing the change. Explicitly confirmed actions use the command's own choice prompt; a direct process-priority assignment uses the console's confirmation instead.

PowerShell host prompts support required/typed fields, single choices, credentials, ordinary input, and secure input. Type conversion runs on the execution side, not the Avalonia dispatcher. The prompt and main window remain responsive: **Stop**, **Stop invocation**, Cancel, closing the prompt, or closing the application releases a waiting host callback. Raw-terminal operations and nested terminal prompts are explicitly unsupported.

Removing a provider drive does **not** delete the underlying files or resources. Environment edits change the application process's environment, not persistent user or machine settings.

**Stop** requests cooperative PowerShell cancellation. It does not roll back side effects or guarantee interruption of blocked native calls. If an invocation remains active two seconds after Stop, the status shows **Stopping / unresponsive**. The serialized session stays occupied until that call returns; the application does not dispose an active pipeline or claim it was terminated. Application close also requests Stop and waits for active work to finish safely.

Completed, completed-with-errors, failed, and cancelled outcomes remain distinct; consult Diagnostics when results are incomplete or an action fails. Action definitions declare retain, refresh, replace, or related-view result handling. Built-in mutations refresh their source after execution, including failed/cancelled execution that may have left side effects; modules and threads open related results from the original retained process, with Back navigation to the source. Partial-failure diagnostics and the original action outcome remain visible even after a successful refresh.

## History and diagnostics

**PowerShell History** records command representations, timing, and outcomes. Selected-object actions may depend on retained objects in the live session, so history is not an arbitrary replay engine. The latest 200 invocations are retained in memory.

**Diagnostics** displays execution messages and errors separately from history. A query can return useful objects and still report errors; an empty successful query is different from a failed query.

Results retain at most **25,000 objects per invocation**. Producing an excess object or reaching 1,000 errors requests cooperative stop and shows a Failed/incomplete-result retention notice; a user-requested stop remains Cancelled. The adapter allows four nonempty live result sets and visibly rejects/releases new output when that budget is full, without invalidating existing handles. Mutations may already have left side effects even if result admission fails. The desktop retains one current result and no related-result cache.

Diagnostics keeps the latest 2,000 ordinary records, a separate latest-1,000 error tail, and one latest progress update. Messages and history command text are shortened after 16,384 characters. Eviction, coalescing and shortening are visible and do not turn an error outcome into success. These are bounded transcripts, not complete file logging.

**Export table...** saves only retained, filtered visible rows; clear the filter to export every retained row before leaving the view. Copy diagnostic/history text into a file before eviction or close. Recover complete read-only output by deliberately querying a narrower scope or using a separate PowerShell with file-directed export/logging; do not automatically replay mutations, redacted history or interactive input. See [measured responsiveness and the full retention policy](result-performance.md).

Interactive host input is not recorded and its history is labeled **Non-replayable**. Credentials and secure values are disposed after execution, and password editors are cleared when their prompt closes. Because scripts can echo or embed secrets in arbitrary objects, an invocation using credential/secure host input does not retain returned objects and redacts all textual diagnostics and its command representation. This conservative policy is visible in Diagnostics; it is not a security sandbox or a guarantee that arbitrary PowerShell scripts cannot keep or disclose their own copies.

## Script editing only

**View -> Script Editor** opens one editable document using published `PoshTools.Iseberg.Editor` **0.0.4**. Use its toolbar or **File -> New/Open/Save/Save As**. Ctrl+N/O/S and Ctrl+Shift+S (Command on macOS) select the editor and operate on that document. Ctrl/Command+F opens Find when a script editor has focus; normal selection, undo/redo and clipboard editing remain available. The editor shows its full file path, an unsaved-change marker, and file-operation status.

New, Open and application close protect unsaved changes with **Save / Discard / Cancel**. Cancelling a save picker or a failed save aborts that operation. File failures show a script-specific dialog without changing the document/path/dirty state or the console's result/history/diagnostic state. File operations temporarily lock the editing area, not the administration session. Files are opened as text only; nothing is executed.

On application close, the script guard runs before saving workspace preferences. Cancelling either the script decision/save or a later workspace-save failure keeps the window open and re-enables editing. **Close without saving** in the workspace failure prompt applies only to preferences, not to the script's separate deliberate-save decision. Restored workspace contrast applies to both editors; saved resources remain paused until explicitly opened, and neither editor document/path is restored or serialized.

Opened UTF-8 (with or without BOM), UTF-16 and UTF-32 BOM-marked files retain their encoding and existing line endings. New documents save as UTF-8 without BOM. Invalid UTF-8 without a supported BOM is rejected rather than silently replacing characters. Save uses a flushed, same-directory temporary file and atomic replacement; changes on disk are detected and require reopening or Save As. Save As asks before replacing an existing file. A competing filesystem rename during the final swap preserves the displaced version in a named `.save-backup` file and reports its location instead of silently losing it. This is conflict detection/recovery, not a cross-process filesystem transaction. Unix permission bits are preserved on replacement; new Unix files start owner-only. Symbolic-link/reparse-point files may be read but require Save As to a regular file. Local filesystem paths are required; cloud-only storage-provider URIs are unsupported.

**Current Script** is a separate, read-only highlighted representation of the latest administration invocation. It updates as the console runs queries/actions and never replaces the editable document. Both editors use lexical highlighting only: **semantic diagnostics and completion are unavailable**, not "zero syntax errors." High contrast disables both lexical palettes and uses the console's text/background/gutter colors.

There are no Run controls, debugger, script session or run history. Editor F5/F8/Ctrl+Pause do not execute scripts or refresh the console; focused editor navigation does not invoke administration Back/Forward. Deliberate administration toolbar/menu actions remain available. Switching tabs retains the existing administration result objects, selection, history and session. User-edited scripts are kept in memory and never included in layout/workspace persistence, invocation history or logs; only deliberate script saves write them to disk.

The package and AvaloniaEdit are MIT-licensed. Published output includes their license/distribution notices in `ThirdPartyNotices`; the existing Avalonia/native and embedded-PowerShell requirements still apply. The editor adds no PowerShell runtime dependency or parser/provider fallback. Physical keyboard and screen-reader behavior and native editor interaction on Linux/macOS remain unverified; the editor's Edit/Value automation peer is not a full TextPattern implementation. Trimming/Native AOT are not validated.

## Saved settings

Preferences are saved in versioned `Runspace/workspace.json` under the platform's local application-data directory. The workspace restores window size/location, pane sizes and visibility, high contrast, and each built-in view's column order/visibility/width, typed sorting, and display filter. **View -> Navigation pane / Actions pane** toggles the side panes. **View -> Reset layout** immediately saves default window/pane proportions, visibility, and contrast without removing per-view overrides or saved references.

Startup restores preferences **without running a query**, discovering session drives, loading profiles, recreating drives, enabling kits, or reconnecting sessions. Choose a resource, or use **Open saved resource** to deliberately run the saved built-in view. Expand **PowerShell Drives** to discover the current session's drives. Dynamic provider paths and related views containing live object handles are session-only, not saved navigation targets.

The old `layout.json` is read only when no workspace exists. Its positive, finite window/pane dimensions are clamped to supported ranges; the original is left untouched when the new workspace is saved. Window placement is fitted to an available display's working area using its current scaling, including when a saved monitor is no longer attached.

Saves validate the document, flush a unique temporary file in the same directory, and atomically replace the committed workspace. Interrupted temporary files are not restored. Corrupt, unreadable, unknown-field, or unsupported-version data produces a persistent recovery notice and blocks saving; **Reset layout does not bypass that protection**. In **View -> Workspace settings**, repair the original externally and choose **Reload repaired workspace**, or explicitly **Back up damaged file and reset preferences**. Reset archives the original as a uniquely named `.bak` before saving defaults. If an ordinary save fails on close, Cancel keeps the window open so storage can be repaired; closing without saving is a separate explicit choice.

Unavailable resource/session/kit references remain visibly unresolved and retain their overrides across saves. Workspace settings lists their identifiers; reload after repairing the data, or confirm **Remove unresolved references** to discard just those user preferences. This does not delete installed content or establish a session. Kits and remote-session contracts are not implemented: their identifiers are inert references, not install, trust, connection, or execution instructions.

Only preference data and stable identifiers are serialized. Live objects, results, row selections/handles, scripts, invocation history, diagnostics, raw host prompts, credentials, secure parameters, custom drives, and private session state are never saved. Installed kit content and trust decisions are not part of the workspace file.

### Workspace version 1

Files use UTF-8 (an optional UTF-8 BOM is accepted), and property names are case-sensitive. The root requires `Version`, `Layout`, and `Views`; `ActiveView` may be null. Unknown fields, duplicate JSON properties, incomplete view/reference/column/sort entries, invalid encoding, and unsupported versions require recovery rather than guessing or dropping data.

| Field | Saved data |
| --- | --- |
| `Version` | Integer `1` |
| `Layout` | `Width`/`Height` in DIP, optional paired `X`/`Y` in screen pixels, `NavigationWidth`, `ActionsWidth`, `DiagnosticsHeight`, `NavigationVisible`, `ActionsVisible`, `DiagnosticsVisible`, `HighContrast` |
| `ActiveView` | Optional reference with `SessionId`, `KitId`, `ResourceId`; current built-ins use `local` / `builtin.local-system` / the catalog's stable node ID |
| `Views` | User overrides keyed by a `Reference` of the same shape, ordered `Columns` (`Key`, `Visible`, `Width`), ordered `Sorting` (`Key`, `Descending`), and a non-executable text `Filter` |

References and column/sort keys must be nonempty, bounded identifiers. Duplicate view/column/sort keys, non-finite or out-of-range dimensions, null preference lists, and all-hidden saved columns are rejected before commit. Files are limited to 4 MiB, with at most 500 views, 200 columns/sort keys per view, and 4096 filter characters. Missing columns/sort keys retain their overrides; if no saved visible column is available, the first available column is shown with an explicit diagnostic.

## Current scope

The application currently uses built-in views. It does not load Console Kits or third-party extensions, manage remote computers, or provide a Hyper-V domain console, full docking system, chart/report designer, or scripting IDE. CI publish outputs are not installers.

Future work and release-readiness gates are tracked in [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24). Design proposals describe possible contracts, not currently available features.
