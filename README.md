# Runspace

A .NET 10 / PowerShell 7 / Avalonia administration console inspired by PowerGUI.

The foundation is a native desktop application, not a terminal wrapper: navigation tree on the left, typed object tables in the center, grouped contextual actions on the right, and PowerShell history/diagnostics. This iteration uses **built-in views only**. Console Kits, third-party extensions, and an installer are out of scope.

## Build and launch

Install the **.NET 10 SDK**. `global.json` pins 10.0.401, and NuGet package lock files pin the dependency graph. An external PowerShell installation is not required for the embedded engine.

From the repository root on Windows:

```powershell
dotnet restore Runspace.slnx --locked-mode
dotnet build Runspace.slnx
dotnet run --project .\src\Runspace.Desktop\Runspace.Desktop.csproj
```

On macOS/Linux, use `src/Runspace.Desktop/Runspace.Desktop.csproj` for the project path. Linux needs a graphical session and Avalonia's native dependencies, including X11/XWayland and fontconfig.

**VS Code:** open this folder, install the recommended C# extension, and press **F5** with **Runspace: launch console** selected. **Ctrl+Shift+B** builds the solution. The `launch`, `test`, and `publish` tasks are also available through **Terminal -> Run Task**.

```powershell
dotnet test Runspace.slnx
dotnet publish .\src\Runspace.Desktop\Runspace.Desktop.csproj -c Release -o .\artifacts\publish
```

GitHub Actions builds/tests on Windows, Ubuntu 24.04, and macOS, then uploads self-contained desktop outputs for `win-x64`, `linux-x64`, and `osx-arm64`. These are build artifacts, not installers.

## Built-in administration

| View | Experience |
| --- | --- |
| Processes | Resource-usage table, live properties, start/confirmed stop, Windows priority, related modules and threads |
| Services | Windows service table and confirmed start/stop/restart |
| Event Logs | Windows logs and related recent-event tables |
| Network Configuration | Local IP properties and network-interface objects |
| Registry | Windows provider roots, keys, and object properties |
| Drives | All session provider drives; create/remove drives and browse their resources |
| Shares | Windows share objects |
| Local Users and Groups | Windows accounts/groups and related membership |
| WMI Browser | Windows CIM namespaces, classes, and instances |
| Environment | Session environment entries and edit/remove actions |
| PowerShell Providers / Drives | Discover the actual embedded session's providers and browse available roots |
| Network / Managed Computers | Local computer information; remote targets are not implemented |

Windows-specific views remain identifiable on other platforms and explain their limitations. Runtime/module availability and account permissions determine whether an operation can run; missing capabilities and access failures are shown in Diagnostics, never disguised as successful empty tables.

## Using the console

Select a resource, filter the displayed table, click column headers to sort typed values, drag/resize columns, and choose one or multiple rows. **Columns...** controls visibility. The action pane and row context menu use the same selection rules.

Double-click or press Enter to inspect properties or follow a related view. Use **Back/Forward**, **F5**, **Ctrl/Command+F**, and **Ctrl/Command+C**. **Tools -> Go to provider path** accepts literal PowerShell provider paths.

Mutating operations ask for parameters/confirmation where appropriate and run with your current account's permissions. Removing a provider drive does not delete its underlying data. Environment edits affect the embedded session, not persistent machine settings.

**Stop** requests cooperative PowerShell cancellation; it does not roll back side effects or guarantee interruption of blocked native calls. Failures, partial errors, and cancelled results remain distinct.

PowerShell History records command representations and outcomes. Selected-object actions can depend on the live session; history is not an arbitrary replay engine. The most recent 200 invocations are retained in memory.

Window/pane sizes are saved under the platform's local application-data directory in `Runspace/layout.json`. **View -> Reset layout** restores default proportions. Live result objects and credentials are not persisted; profiles are not automatically executed.

## Scope and design reference

This is a working foundation, not full PowerGUI parity. There is no full docking system, chart/report designer, scripting IDE, remote-computer management, Hyper-V domain console, or extension/kit loader.

The current MIT DataGrid adapter was selected for virtualized rows, column reordering, resizing, typed sorting, and multi-selection after inspecting the available Avalonia controls. Avalonia's newer TableView does not expose the same complete interface; DataGrid's documented deprecation remains a future migration concern. No commercially licensed TreeDataGrid is required.

The supplied PowerGUI MSI was extracted and run in a session-local portable layout to observe its real process-table/navigation/action presentation. No original binaries, scripts, icons, or branding are dependencies or committed resources.

| Document | Purpose |
| --- | --- |
| [Implementation notes](docs/foundation.md) | Current architecture, constraints, and visual reference observations |
| [Domain glossary](CONTEXT.md) | Canonical resource/object/action vocabulary |
| [PowerGUI research](docs/research/powergui-3.8.md) | Original ILSpy source ledger |
| [Platform research](docs/research/modern-platform.md) | Runtime and platform constraints |
| [Original product/design proposals](docs/product-vision.md) | Historical planning foundation; broader kit/extensibility proposals are deferred |
