# Using Runspace

Runspace presents a resource tree on the left, typed object tables in the center, and grouped contextual actions on the right. PowerShell History and Diagnostics explain what ran and how it finished.

## Launching Runspace

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

Select a resource in the navigation tree to load its objects. Filter the displayed table, click column headers to sort typed values, and drag or resize columns. **Columns...** controls column visibility.

Select one or multiple rows to see applicable operations. The Actions pane and row context menu use the same selection rules. Double-click or press **Enter** to inspect properties or follow a related view; use **Back/Forward** to navigate between resources.

**Tools -> Go to provider path** accepts literal PowerShell provider paths, such as `Env:` or `C:\Windows`. Available providers and access permissions determine which paths can be browsed.

Provider-tree expansion loads only that location's containers, not their descendants. Flat providers such as Environment, Alias, Variable, and Function still display their items in Results without invented child containers or mutation actions. Use the built-in **Environment** view to edit environment variables. Failed expansion remains retryable; partial expansion keeps usable children and shows an incomplete-navigation warning. Collapse and expand to retry.

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

Selection is frozen when an action begins, before parameter or host prompts open. Changing the selected row while a prompt is open never retargets that action. Command-backed actions offer **Preview only (PowerShell WhatIf)**; commands receive typed `WhatIf`/`Confirm` values only when they support them. Preview reports what PowerShell would do without performing the change. Explicitly confirmed actions use the command's own choice prompt; a direct process-priority assignment uses the console's confirmation instead.

PowerShell host prompts support required/typed fields, single choices, credentials, ordinary input, and secure input. Type conversion runs on the execution side, not the Avalonia dispatcher. The prompt and main window remain responsive: **Stop**, **Stop invocation**, Cancel, closing the prompt, or closing the application releases a waiting host callback. Raw-terminal operations and nested terminal prompts are explicitly unsupported.

Removing a provider drive does **not** delete the underlying files or resources. Environment edits change the application process's environment, not persistent user or machine settings.

**Stop** requests cooperative PowerShell cancellation. It does not roll back side effects or guarantee interruption of blocked native calls. If an invocation remains active two seconds after Stop, the status shows **Stopping / unresponsive**. The serialized session stays occupied until that call returns; the application does not dispose an active pipeline or claim it was terminated. Application close also requests Stop and waits for active work to finish safely.

Completed, completed-with-errors, failed, and cancelled outcomes remain distinct; consult Diagnostics when results are incomplete or an action fails. Action definitions declare retain, refresh, replace, or related-view result handling. Built-in mutations refresh their source after execution, including failed/cancelled execution that may have left side effects; modules and threads open related results from the original retained process, with Back navigation to the source. Partial-failure diagnostics and the original action outcome remain visible even after a successful refresh.

## History and diagnostics

**PowerShell History** records command representations, timing, and outcomes. Selected-object actions may depend on retained objects in the live session, so history is not an arbitrary replay engine. The latest 200 invocations are retained in memory.

**Diagnostics** displays execution messages and errors separately from history. A query can return useful objects and still report errors; an empty successful query is different from a failed query.

Interactive host input is not recorded and its history is labeled **Non-replayable**. Credentials and secure values are disposed after execution, and password editors are cleared when their prompt closes. Because scripts can echo or embed secrets in arbitrary objects, an invocation using credential/secure host input does not retain returned objects and redacts all textual diagnostics and its command representation. This conservative policy is visible in Diagnostics; it is not a security sandbox or a guarantee that arbitrary PowerShell scripts cannot keep or disclose their own copies.

## Saved settings

Window and pane sizes are saved in `Runspace/layout.json` under the platform's local application-data directory. **View -> Reset layout** restores default proportions.

Live result objects and credentials are not persisted. PowerShell profiles are not automatically executed. The current layout file does not save full workspace, remote-session, or per-view column/filter state.

## Current scope

The application currently uses built-in views. It does not load Console Kits or third-party extensions, manage remote computers, or provide a Hyper-V domain console, full docking system, chart/report designer, or scripting IDE. CI publish outputs are not installers.

Future work and release-readiness gates are tracked in [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24). Design proposals describe possible contracts, not currently available features.
