# Published-build validation

Recorded 2026-10-06 for [issue #5](https://github.com/adamdriscoll/runspace/issues/5). This is deployment evidence, not a claim that build configuration alone establishes support. See the [usage guide](usage.md#supported-platform-matrix) for the user-facing matrix.

## Gate and evidence

`Runspace.Desktop --validate-publish <report.json>` runs the embedded runtime probes, then opens the **real desktop console**, checks its current-process table and displayed PowerShell version, and renders the window using the native backend. It closes without saving layout. Exit `0` and report `Passed: true` require both runtime and desktop success; exit `1` indicates failure, and invalid validation arguments return `2`. Report-write failures are not treated as success.

The editor integration extends that desktop gate to render the shipped **PoshTools.Iseberg.Editor 0.0.4 / AvaloniaEdit 12.0.0** controls in the actual Fluent/native tree. It checks synthetic text input/undo/find, independent read-only preview/Value automation, unavailable diagnostics, high-contrast palette disabling, F5/F8 isolation, preserved administration results/history, runtime-free editor references, and shipped license notices. It also requests the real editable IME client, queries surrounding text/selection during ten focused tab detach/reattach cycles and disposal, and verifies read-only controls do not advertise an IME client. It discards only its benign in-memory fixture and writes no script file. The original smoke sequence remains intact: 0.0.3 failed there on native macOS and failed the added actual-client regression on Windows; official 0.0.4 corrects producer cleanup rather than bypassing input methods. Physical keyboard hardware and real screen readers remain separate qualifications.

On 2026-10-07 the corrected 0.0.4 package passed the relocated Windows 11 x64 build 26200 native gate, including the added IME lifecycle probes and all three missing-dependency cases. Locked Debug/Release builds and 99 desktop regressions in each configuration also passed. The original native macOS failure must be checked on a fresh consumer CI head; upstream producer tests or these Windows results are not macOS consumer evidence.

The probe queries real processes, providers and drives, browses a temporary filesystem directory, inspects a synthetic environment entry, imports required SDK modules, and compiles/executes an `Add-Type` marker. Every module/command assembly must resolve beneath the payload directory. The report includes commands, outcomes, module paths/versions, OS/version/architecture, .NET/PowerShell/Avalonia/DataGrid versions, and bootstrap exceptions. Environment values and arbitrary process names are not recorded. Access-denied property errors for unrelated processes remain recorded as `CompletedWithErrors`; the current benign process must have readable cells.

The harness copies the payload outside the checkout, starts it from a temporary home with empty `PATH`, `PSModulePath`, and `DOTNET_ROOT`, and writes a profile that would fail if executed. No profile marker may appear. Three fresh-process negative runs remove, individually, the Management manifest, Utility implementation assembly, and Skia native library. Each must exit nonzero and identify the missing dependency. The copied payload is restored between probes and removed afterward; the original artifact is never modified.

The CI **Published payload** jobs run on separate runners after downloading the published artifacts. Windows/macOS use the PowerShell harness (PowerShell drives the harness but is inaccessible through the application's `PATH`). Linux mounts only the payload and results in a clean Ubuntu container, not the source checkout, with networking disabled and a non-root user. Its image contains no `dotnet` or `pwsh`; Xvfb provides the graphical session. CI retains JSON reports and Linux OS-package versions in `publish-validation-<rid>` artifacts, including failures.

Hosted Windows/macOS images contain development tools. Empty search paths and verified module locations prove the exercised operations do not use those tools; they do **not** certify a pristine installation or every physical display/GPU configuration.

## Recorded runs

| Environment | Versions and isolation | Result |
| --- | --- | --- |
| Windows 11 x64, build 26200 (`10.0.26200.0`) | Self-contained `win-x64`, relocated temporary payload/home, empty application search paths; graphical desktop on a development host | Runtime, providers/drives/files/environment, module imports, `Add-Type`, desktop rendering/version display, and all three missing-dependency cases passed. Not pristine-machine certification. |
| Ubuntu 24.04.5 LTS x64 | Clean `ubuntu:24.04` container; self-contained `linux-x64`; non-root, network disabled; no installed .NET/PowerShell; Xvfb/X11 | All positive probes and all three missing-dependency cases passed. Physical X11/XWayland/GPU coverage is not established. |
| macOS 15.7.9 ARM64 | Self-contained `osx-arm64`; fresh hosted macOS 15 runner, relocated temporary payload/home, empty application search paths; native desktop backend | All positive probes and all three missing-dependency cases passed. Not pristine-install/GPU certification. |
| Windows Server 2025 x64, build 26100 (`10.0.26100.0`) | Self-contained `win-x64`; fresh hosted runner image `windows-2025-vs2026` version `20260925.250.1`; relocated payload/home, empty application search paths | All positive probes and all three missing-dependency cases passed. Validation-only server coverage; not a desktop-product support commitment. |

The successful [three-platform CI run 37493267268](https://github.com/adamdriscoll/runspace/actions/runs/37493267268) executed commit [`b78ec35`](https://github.com/adamdriscoll/runspace/commit/b78ec35b372e41ddd23dfdd60a76428edb8ae90e). Its downloaded `publish-validation-win-x64`, `publish-validation-linux-x64`, and `publish-validation-osx-arm64` reports were inspected: all positive reports passed, and all nine deliberately damaged-payload reports failed with the expected dependency-specific error.

Completed runs used SDK **10.0.401** to publish, and reported bundled **.NET 10.0.12**, **PowerShell 7.6.6**, **Avalonia 12.1.3**, and **DataGrid 12.1.2**. Imported SDK manifests report module version **7.0.0.0**; that is not the engine/package version.

Ubuntu native package evidence included ICU `74.2-1ubuntu3.1`, OpenSSL `3.0.13-0ubuntu3.16`, fontconfig `2.15.0-1.1ubuntu2`, X11 `2:1.8.7-1build1`, GLib `2.80.0-6ubuntu3.9`, and Xvfb `2:21.1.12-1ubuntu1.8`. CI records the complete current package list rather than assuming this servicing snapshot remains unchanged.

Actual portable providers were Alias, Environment, FileSystem, Function, and Variable, with discovered filesystem, `Temp`, `Env`, Alias, Function, and Variable drives. Windows also exposed Registry and Certificate providers. These checks do not manufacture universal Windows roots on Unix.

The first CI attempt caught an incorrect assumption about macOS development-output native paths. The SDK's universal `libpsl-native.dylib` lives under `runtimes/osx/native` before publish and is flattened into the payload when published. The resolver now covers both verified layouts, with a regression test that still rejects missing assets; the successful run above includes that correction.

## Reproduce

Publish the configuration/RID being checked, without trimming or single-file changes:

```powershell
dotnet publish .\src\Runspace.Desktop\Runspace.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\publish
pwsh -NoProfile -File .\scripts\Validate-Publish.ps1 -Payload .\artifacts\publish -Results .\artifacts\validation-windows
```

On macOS use `--runtime osx-arm64`, the platform's path syntax, and the same `Validate-Publish.ps1` harness. The harness requires PowerShell 7; the application does not.

For a clean Ubuntu check from the Windows development host:

```powershell
dotnet publish .\src\Runspace.Desktop\Runspace.Desktop.csproj --configuration Release --runtime linux-x64 --self-contained true --output .\artifacts\linux
docker build --quiet --tag runspace-publish-validation --file .\scripts\publish-validation.Dockerfile .\scripts
$payload = (Resolve-Path .\artifacts\linux).Path
$results = [IO.Path]::GetFullPath('.\artifacts\validation-linux')
[IO.Directory]::CreateDirectory($results) | Out-Null
docker run --rm --network none --user 1000:1000 `
  --mount "type=bind,source=$payload,target=/payload,readonly" `
  --mount "type=bind,source=$results,target=/results" `
  runspace-publish-validation
```

The repository Dockerfile is the tested native-prerequisite manifest. Rebuild it when package servicing changes. No container, application process, or test drive is left running after the harness completes.

Keep raw local JSON reports in `artifacts` or session artifacts, not source control: exception traces and payload paths can identify the local development directory. CI evidence is associated with the exact commit/run and is the authority for subsequent builds.

## Qualifications

`Start-Job` requires a `pwsh` executable beside the engine and is outside the SDK-only capability contract. Profiles are never loaded automatically. Windows-only modules/views, permissions, additional module dependencies, actual GPU/display combinations, and unsupported OS/architecture combinations are not made portable by self-contained publication.

The remaining deployment qualifications are pristine Windows/macOS installs, physical X11/XWayland/GPU sessions, and any additional OS versions/architectures before expanding the support matrix. Failed or missing reports must not be counted as successful clean-machine execution.
