# Using Runspace

Runspace presents a resource tree on the left, typed object tables in the center, and grouped contextual actions on the right. PowerShell History and Diagnostics explain what ran and how it finished.

## Launching Runspace

To run from source, install the .NET 10 SDK matching `global.json` and follow the [README quick start](../README.md#quick-start). The PowerShell engine is embedded; a separate PowerShell installation is not required.

On macOS/Linux, use this project path after restoring and building the solution:

```sh
dotnet run --project src/Runspace.Desktop/Runspace.Desktop.csproj
```

Linux requires a graphical session and Avalonia's native dependencies, including X11/XWayland and fontconfig.

The [Build and test workflow](https://github.com/adamdriscoll/runspace/actions/workflows/build.yml) produces self-contained outputs for `win-x64`, `linux-x64`, and `osx-arm64`. Download the matching `runspace-<rid>` artifact from a successful run and extract the entire payload before launching it. These are build artifacts, not installers or a claim of clean-machine certification for every OS version.

For build, test, debugger, and publish instructions, see [Contributing](../CONTRIBUTING.md).

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

Select a resource in the navigation tree to load its objects. Filter the displayed table, click column headers to sort typed values, and drag or resize columns. **Columns...** controls column visibility.

Select one or multiple rows to see applicable operations. The Actions pane and row context menu use the same selection rules. Double-click or press **Enter** to inspect properties or follow a related view; use **Back/Forward** to navigate between resources.

**Tools -> Go to provider path** accepts literal PowerShell provider paths, such as `Env:` or `C:\Windows`. Available providers and access permissions determine which paths can be browsed.

| Shortcut | Operation |
| --- | --- |
| F5 | Refresh the active resource |
| Alt+Left / Alt+Right | Navigate back/forward |
| Ctrl/Command+F | Focus the result filter |
| Ctrl/Command+C | Copy selected rows |
| Enter | Inspect properties or follow the row's related view |

Use **Export table...** to save the visible table as CSV. Filtering changes displayed rows without rerunning the query.

## Run actions deliberately

Operations run with your current account's permissions; UI confirmation does not grant elevated rights. Actions that change resources ask for parameters or confirmation where appropriate.

Removing a provider drive does **not** delete the underlying files or resources. Environment edits change the application process's environment, not persistent user or machine settings.

**Stop** requests cooperative PowerShell cancellation. It does not roll back side effects or guarantee interruption of blocked native calls. Completed, completed-with-errors, failed, and cancelled outcomes remain distinct; consult Diagnostics when results are incomplete or an action fails.

## History and diagnostics

**PowerShell History** records command representations, timing, and outcomes. Selected-object actions may depend on retained objects in the live session, so history is not an arbitrary replay engine. The latest 200 invocations are retained in memory.

**Diagnostics** displays execution messages and errors separately from history. A query can return useful objects and still report errors; an empty successful query is different from a failed query.

## Saved settings

Window and pane sizes are saved in `Runspace/layout.json` under the platform's local application-data directory. **View -> Reset layout** restores default proportions.

Live result objects and credentials are not persisted. PowerShell profiles are not automatically executed. The current layout file does not save full workspace, remote-session, or per-view column/filter state.

## Current scope

The application currently uses built-in views. It does not load Console Kits or third-party extensions, manage remote computers, or provide a Hyper-V domain console, full docking system, chart/report designer, or scripting IDE. CI publish outputs are not installers.

Future work and release-readiness gates are tracked in [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24). Design proposals describe possible contracts, not currently available features.
