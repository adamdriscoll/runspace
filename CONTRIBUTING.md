# Contributing to Runspace

Use [GitHub issues](https://github.com/adamdriscoll/runspace/issues) to report bugs and propose changes. The [roadmap tracker](https://github.com/adamdriscoll/runspace/issues/24) groups remaining work, dependencies, and acceptance gates; keep task status there rather than adding Markdown roadmaps.

The current implementation is the built-in administration console. Discuss changes to public contracts, persisted formats, dependencies, or product scope before implementing them.

## Development setup

Install the **.NET 10 SDK** matching `global.json` (10.0.401, with patch roll-forward). NuGet lock files pin the dependency graph, and `NuGet.Config` defines the package source. The embedded engine does not require a separate PowerShell installation.

From the repository root in PowerShell:

```powershell
dotnet restore Runspace.slnx --locked-mode
dotnet build Runspace.slnx
dotnet run --project .\src\Runspace.Desktop\Runspace.Desktop.csproj
```

For macOS/Linux paths and graphical dependencies, see [Launching Runspace](docs/usage.md#launching-runspace).

### VS Code

Open the repository folder, install the recommended C# extension, and press **F5** with **Runspace: launch console** selected. **Ctrl+Shift+B** builds the solution. The `launch`, `test`, and `publish` tasks are also available through **Terminal -> Run Task**.

## Project structure

| Project | Responsibility |
| --- | --- |
| `src\Runspace.Core` | Resource/action definitions, cached display cells, typed filtering/sorting, validated versioned workspace storage |
| `src\Runspace.PowerShell` | Local session, serialized execution, live object ownership, provider/CIM operations, streams and cancellation |
| `src\Runspace.Desktop` | Avalonia shell, dialogs, history, diagnostics, clipboard/CSV, and workspace preferences/recovery |
| `tests\Runspace.Tests` | Core behavior and real embedded-runtime tests |
| `tests\Runspace.Desktop.Tests` | Headless Avalonia interaction tests through a fixture session |

Keep PowerShell-specific types out of Core and desktop bindings, and Avalonia controls out of the execution adapter. Use `IConsoleSession` for deterministic desktop fixtures and the real runtime for provider/execution compatibility tests.

## Test changes

Run the smallest relevant tests while developing, then the solution suite before submitting:

```powershell
dotnet test .\tests\Runspace.Tests\Runspace.Tests.csproj --filter FullyQualifiedName~ResultViewTests
dotnet test .\tests\Runspace.Desktop.Tests\Runspace.Desktop.Tests.csproj
dotnet test Runspace.slnx
```

Add regression coverage for behavior changes. Use original fixture objects, uniquely named temporary drives/directories, and disposable child processes. Tests must never perform destructive actions against arbitrary machine resources or require production credentials.

Desktop tests use Skia software rendering as well as headless input. Minimal Linux SDK containers need `libfontconfig1` and `fonts-dejavu-core`; no display server is needed for these tests. Set `RUNSPACE_UI_CAPTURE_DIR` to a scratch output directory to retain benign fixture PNGs and size/scale/row-count JSON. See [shell validation](docs/shell-validation.md) for the exact capture gate and its limits.

Result/stream/navigation limits have focused regression coverage in `RetentionTests`. Run the reference-hardware Release responsiveness gate with `pwsh -NoProfile -File .\scripts\Measure-Results.ps1 -Results .\artifacts\result-benchmarks`. It records the 20,000-row/ten-scalar fixture, native/headless selection and scrolling, engine-first-output, managed retention, flood buffer depth and cooperative cancellation. See [measurement methods, reference results and policy](docs/result-performance.md); use `-SkipNative` only when a graphical session is unavailable.

Nullable analysis and warnings-as-errors are enabled in `Directory.Build.props`. Preserve typed values, fixed object selection, literal parameter binding, and explicit failure/partial/cancelled states. Never evaluate arbitrary object getters on the UI thread, hide execution failures as empty successes, or store secrets in history or settings.

## Publish and CI

The desktop project has separate Debug and Release lock files because its inspection bridge is development-only. Restore the configuration you intend to build.

To follow the Release CI sequence and publish a self-contained Windows output:

```powershell
dotnet restore Runspace.slnx --locked-mode --property:Configuration=Release
dotnet build Runspace.slnx --configuration Release --no-restore
dotnet test Runspace.slnx --configuration Release --no-build
dotnet publish .\src\Runspace.Desktop\Runspace.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\publish
```

For another platform, use its project-path syntax and runtime identifier. [GitHub Actions](.github/workflows/build.yml) builds/tests on Windows, Ubuntu 24.04, and macOS, then publishes `win-x64`, `linux-x64`, and `osx-arm64` artifacts. Separate **Published payload** jobs download and launch those artifacts outside the checkout, including deliberately missing manifest, assembly, and native-library probes. Linux runs in a clean Ubuntu container with Xvfb and no installed PowerShell/.NET; Windows/macOS use isolated application search paths on hosted runners.

Run the Windows published gate locally after publishing:

```powershell
pwsh -NoProfile -File .\scripts\Validate-Publish.ps1 -Payload .\artifacts\publish -Results .\artifacts\validation-windows
```

See [published-build validation](docs/publish-validation.md) for exact probes, results, the clean Linux recipe, and hosted-runner limitations. A build/test success does not replace this gate, and passing a hosted-runner gate does not certify every pristine OS installation or GPU/display configuration. These outputs are not installers.

### Release packaging

The [Release packages workflow](.github/workflows/release.yml) runs when a version tag such as `v1.2.3`, `1.2.3`, or `v1.2.3-preview.1` is pushed, and when a GitHub release is published. It reuses the three-platform build/test and published-payload gates, then packages those exact versioned payloads. ZIPs for `win-x64`, `linux-x64`, and `osx-arm64`, a Windows MSI, and a macOS DMG are attached to the matching release. A tag push creates the release with generated notes if necessary; versions containing `-` are marked as prereleases. Reruns replace matching assets.

Packages are unsigned, with no macOS notarization or NuGet publishing. Windows installs per-machine under Program Files with a Start menu shortcut and MSI upgrade/uninstall support. macOS packages contain `Runspace.app`; the DMG includes an Applications shortcut. Numeric versions must fit MSI limits (major/minor <= 255, patch <= 65535). Prerelease suffixes are retained in asset names and application versions; MSI upgrade ordering uses only the numeric version, so stable/prerelease packages with the same numeric version replace each other.

Build packages locally on the target OS with PowerShell 7 and the .NET SDK:

```powershell
pwsh -NoProfile -File .\packaging\Package.ps1 -Runtime win-x64 -Version 1.2.3
pwsh -NoProfile -File .\packaging\Test-Package.ps1 -Runtime win-x64 -Version 1.2.3
```

Use `linux-x64` or `osx-arm64` and the corresponding path syntax on those hosts. Linux/macOS require `zip` and `unzip`; macOS also uses the built-in `plutil` and `hdiutil`. Windows restores the pinned WiX tool from `.config/dotnet-tools.json`. Packaging writes under `artifacts/publish`, `artifacts/staging`, and `artifacts/packages`, partitioned by RID/version; remove those specific output directories before repeating the same build. Supply `-Payload <published-directory>` to package an existing self-contained Release payload rather than publishing again, as CI does; its runtime and embedded application version must match the requested package.

`Test-Package.ps1` extracts the ZIP and administratively extracts the MSI (without installing it), or mounts the DMG read-only. It compares every packaged payload file against the publish directory and checks Unix executable permissions. WiX MSI validation, plist linting, and DMG integrity checks also run during packaging. Local packaging alone does not replace the published-payload gate.

## Submit a pull request

Keep changes focused and link the issue they address. Explain the behavior change, how it was checked, and any platform or compatibility limitations. Update directly affected documentation, and distinguish implemented behavior from proposals.

Do not commit credentials, private result data, machine-specific reference captures, or proprietary binaries/scripts/artwork. Do not automatically elevate, install modules, load profiles, activate untrusted code, or reconnect sessions.

## Design references

Start with the [implemented foundation](docs/foundation.md) and [domain glossary](CONTEXT.md). The [product direction](docs/product-vision.md), [architecture alternatives](docs/architecture.md), [console interaction specification](docs/ux/admin-console.md), and [kit contract proposal](docs/console-kits.md) provide design context, not a second backlog.

The [platform research](docs/research/modern-platform.md) records runtime constraints. Detailed [reference research](docs/research/powergui-3.8.md) is kept separate from user-facing guidance; historical assets are not product dependencies.
