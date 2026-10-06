# Modern platform research

Research date: 2026-10-05. Scope: official primary sources for a .NET 10 / PowerShell 7 / Avalonia administration console. Versions below were verified in published metadata/source; no application restore, build, publish, or runtime validation was performed.

## Recommended baseline

**Proposal:** target `net10.0`, pin SDK **10.0.401**, and initially evaluate **Microsoft.PowerShell.SDK 7.6.6** with **Avalonia 12.1.3**. .NET runtime **10.0.12** is the verified servicing baseline. Recheck servicing versions when implementation starts and before release.

| Technology | Verified association | Consequence |
| --- | --- | --- |
| .NET 10 | LTS; support ends 2028-11-14 | Required target and suitable long-lived baseline |
| PowerShell 7.6.6 | SDK has `net10.0` dependency group; 7.6 uses .NET 10 and is LTS through 2028-11-14 | Preferred PowerShell 7 family |
| PowerShell 7.5.11 | SDK targets `net9.0`; support ends 2026-11-10 | Poor starting point for a new application |
| PowerShell 7.4.20 | SDK targets `net8.0`; support ends 2026-11-10 | Compatibility candidate, not the new baseline |
| PowerShell 7.7 preview | Associated with .NET 11 in lifecycle documentation | Exclude from the .NET 10 production baseline |
| Avalonia 12.1.3 | NuGet groups include `net8.0` and `net10.0` | Compatible declared target; application combination still needs validation |
| Windows PowerShell 5.1 | .NET Framework environment | Separate Windows-only compatibility path, not the primary host |

Sources: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), [PowerShell lifecycle snapshot](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/0c1f3090b1f33a1bfb99510ffbfa2b724f4911d8/reference/docs-conceptual/install/PowerShell-Support-Lifecycle.md#L124-L132), [PowerShell v7.6.6](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.6), [PowerShell target framework](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/PowerShell.Common.props#L147), [SDK 7.6.6 manifest](https://api.nuget.org/v3-flatcontainer/microsoft.powershell.sdk/7.6.6/microsoft.powershell.sdk.nuspec), [SDK 7.5.11 manifest](https://api.nuget.org/v3-flatcontainer/microsoft.powershell.sdk/7.5.11/microsoft.powershell.sdk.nuspec), [SDK 7.4.20 manifest](https://api.nuget.org/v3-flatcontainer/microsoft.powershell.sdk/7.4.20/microsoft.powershell.sdk.nuspec), [Avalonia 12.1.3 release](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.3), [Avalonia manifest](https://api.nuget.org/v3-flatcontainer/avalonia/12.1.3/avalonia.nuspec).

A lower target framework does not prove that a package is impossible to reference from .NET 10, nor prove a supported engine/runtime pairing. Prefer the matching PowerShell 7.6/.NET 10 line. Microsoft supports current servicing updates within supported release lines; distribution needs a patch policy, not permanently frozen vulnerable dependencies.

## Platform support is an intersection

Support must intersect .NET, PowerShell, Avalonia, OS lifecycle, native dependencies, and the actual published application.

| Proposed validation target | Qualification |
| --- | --- |
| Supported Windows 11 builds, x64/ARM64 | Windows 11 appears in .NET/Avalonia support tables; PowerShell publishes these architectures |
| macOS 26, ARM64/x64 | Listed by the three stacks; Avalonia Tier 1 |
| macOS 15, ARM64/x64 | Listed by .NET/PowerShell; Avalonia Tier 2 |
| Debian 13, initially x64 | Listed by .NET/PowerShell; Avalonia Tier 1 |
| Ubuntu 24.04 LTS, initially x64 | Listed by .NET/PowerShell; Avalonia Tier 2 |

Sources: [.NET 10 supported OS snapshot](https://github.com/dotnet/core/blob/44927bc821d37f596e317c4316335632bd38c79b/release-notes/10.0/supported-os.md), [Avalonia platform snapshot](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/docs/supported-platforms.mdx), PowerShell [macOS](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/live/reference/includes/macos-support.md), [Debian](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/live/reference/includes/debian-support.md), [Ubuntu](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/live/reference/includes/ubuntu-support.md) support includes, [release assets](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.6). The `live` links are mutable; these are candidate targets, not a tested Runspace support matrix.

Avalonia's default Linux desktop path uses X11/XWayland. Native Wayland became available in 12.1 as an opt-in, early-stage backend; `UsePlatformDetect()` does not select it automatically and explicit `UseWayland()` has no automatic fallback. Initially retain default detection. [Official Linux guidance](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/docs/platform-specific-guides/linux.md#L8-L40).

Linux native prerequisites include X11-related libraries and fontconfig; a self-contained .NET publish does not eliminate them. musl distributions have additional qualifications. Avalonia's Tier 1 Ubuntu 25.x entry does not override PowerShell's exclusion of interim Ubuntu releases. Debian versions also differ between individual tables: use the stricter full-stack intersection. [Avalonia platform details](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/docs/supported-platforms.mdx#L78-L97), [PowerShell Ubuntu exclusions](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/live/reference/includes/ubuntu-support.md#L29-L32).

## PowerShell hosting

### Concrete engine and published payload

Use `Microsoft.PowerShell.SDK`, not `PowerShellStandard.Library`, for hosting. The latter is reference-only; the SDK supplies concrete engine/standard implementation assemblies, module manifests, and reference assets for `Add-Type`. Ordinary self-contained embedded execution does not require an external PowerShell installation. [Official package-selection guidance](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/cafbd94787c577de074d4b1db64a77d2c7e9da1a/reference/docs-conceptual/dev-cross-plat/choosing-the-right-nuget-package.md#L179-L209).

Embedding is not identical to launching `pwsh`. In particular, `Start-Job` requires a `pwsh` executable and is not supported by an SDK-only host by default. Declare this capability honestly; test module discovery, `Import-Module`, and `Add-Type` in the published payload rather than assuming development output proves deployment completeness. [SDK limitations and assets](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/cafbd94787c577de074d4b1db64a77d2c7e9da1a/reference/docs-conceptual/dev-cross-plat/choosing-the-right-nuget-package.md#L190-L203).

### State, objects, and concurrency

`InitialSessionState` defines runspace commands/variables/modules. `CreateDefault2()` supplies core hosting commands; `CreateDefault()` supplies the built-in command set. Select initialization deliberately and validate the modules needed by the built-in kit. [Hosting initialization documentation](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/5e1c81199ca2e2ead3fdc724796d978802c03f2a/reference/docs-conceptual/developer/hosting/creating-an-initialsessionstate.md#L8-L16).

**Proposal:** persistent runspace ownership per stateful session, one serialized queue, and a separate invocation object per operation. Command construction via `AddCommand`/`AddParameter` is documented as non-thread-safe. A runspace pool limits active runspaces and queues excess requests, but is not one shared persistent interactive session. [Command construction](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L947-L975), [parameter construction](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L1198-L1210), [pool behavior](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/RunspacePool.cs#L1020-L1047).

`PSObject` exposes properties, the underlying object, and ordered type names. Preserve those objects inside the runtime for actions while exposing safe cached projections/reference handles to the UI. A `PSScriptProperty` getter executes code and can throw or require a runspace; UI binding must not repeatedly evaluate arbitrary live properties. [Object structure](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/MshObject.cs#L751-L820), [script-property evaluation](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/MshMemberInfo.cs#L1784-L1805).

### Streams, prompts, and stop

The verified engine offers task-based `InvokeAsync()` and callback-based `StopAsync(AsyncCallback, object)`; do not invent a cancellation-token overload. [Invocation source](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L3063-L3084), [stop source](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L3824-L3856).

`PSDataCollection<PSObject>` is a thread-safe incremental buffer with notifications. `ReadAll()` drains current data; choose a single draining strategy. Complete supplied input buffers when no more input is expected, or an asynchronous pipeline may keep waiting. Caller-supplied output buffers permit retaining partial output after a stopped pipeline. [Collection implementation](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PSDataCollection.cs), [input completion requirement](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L3193-L3206), [partial-output semantics](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L3678-L3686).

Errors, warnings, verbose, debug, information, and progress are distinct typed streams alongside success output. Preserve that distinction and report partial failures. Stop does not imply rollback or a hard deadline for arbitrary native code. [Stream definitions](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/PowerShell.cs#L5984-L6115).

`PSHostUserInterface` includes line input, credentials, field/choice prompts, and progress. Implement a host adapter with visible prompts, cancellation, and explicit rejection of unsupported raw-console interactions; never allow an invisible prompt to hang an invocation. [Host interface source](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/src/System.Management.Automation/engine/hostifaces/MshHostUserInterface.cs).

## Providers and administration portability

Discover actual session drives/providers with `Get-PSDrive`/`Get-PSProvider` and browse with provider-aware commands. Providers return different object types and can supply dynamic parameters. Preserve provider paths rather than unconditionally normalizing them as filesystem paths. [Provider documentation](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/f09ce0b678c2da1ae0fa70f0b4789a607183cc63/reference/7.6/Microsoft.PowerShell.Core/About/about_Providers.md#L18-L27), [provider navigation](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/f09ce0b678c2da1ae0fa70f0b4789a607183cc63/reference/7.6/Microsoft.PowerShell.Core/About/about_Providers.md#L184-L208).

FileSystem, Environment, Alias, Function, and Variable are distinct resources. Certificate, Registry, and WSMan are Windows-only providers, not universal roots. Gate actions on actual session capabilities. [Built-in provider/platform list](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/f09ce0b678c2da1ae0fa70f0b4789a607183cc63/reference/7.6/Microsoft.PowerShell.Core/About/about_Providers.md#L30-L70).

Windows PowerShell compatibility requires Windows/Windows PowerShell 5.1 and uses serialized parameters/results. It cannot provide the same live-object action guarantees as the primary in-process engine, nor rescue Windows-only modules on Linux/macOS. Independently shipped modules have their own support/licensing lifecycle. [Compatibility qualifications](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/f09ce0b678c2da1ae0fa70f0b4789a607183cc63/reference/7.6/Microsoft.PowerShell.Core/About/about_Windows_PowerShell_Compatibility.md#L136-L145), [module lifecycle qualification](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/0c1f3090b1f33a1bfb99510ffbfa2b724f4911d8/reference/docs-conceptual/install/PowerShell-Support-Lifecycle.md#L105-L110).

## Avalonia UI and table choices

Avalonia documents MVVM and a single-threaded UI model. Controls and bound collections must be updated on the UI thread; publish batched runtime events through the dispatcher. PowerShell callbacks must not mutate bound collections directly. [MVVM](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/docs/fundamentals/the-mvvm-pattern.md#L16-L32), [threading](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/docs/app-development/threading.md#L8-L38), [collection threading](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/docs/app-development/threading.md#L295-L320).

| Control | Verified position | Proposal |
| --- | --- | --- |
| TreeView | Core hierarchical control | Navigation/provider tree |
| TableView | Core, introduced in 12.1; read-only, row-virtualized | Evaluate first for object results |
| DataGrid | Separate MIT package; deprecated in current docs | Only if a proven requirement is unmet by TableView |
| TreeDataGrid | Current docs require Avalonia Pro or higher and a license key | Separate commercial decision; do not assume core MIT terms |

Sources: [TreeView](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/controls/data-display/structured-data/treeview.md), [TableView](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/controls/data-display/structured-data/tableview.md), [DataGrid deprecation/styles](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/controls/data-display/structured-data/datagrid.md#L14-L70), [TreeDataGrid licensing](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/controls/data-display/structured-data/treedatagrid/index.md#L18-L37).

TableView virtualizes rows, not columns. Do not assume it provides all required sorting, filtering, selection, reordering, export, or accessibility behavior; prove the console's contract in a spike. [Virtualization details](https://github.com/AvaloniaUI/avalonia-docs/blob/a0a3666ecc32b9031eace19da0b045aba0e09af8/controls/data-display/structured-data/tableview.md#L183-L188).

The verified DataGrid package is **12.1.2**, not an invented 12.1.3 matching the core release; its manifest depends on Avalonia 12.1.0. If selected, establish a tested version combination and include its styles. [DataGrid manifest](https://api.nuget.org/v3-flatcontainer/avalonia.controls.datagrid/12.1.2/avalonia.controls.datagrid.nuspec).

## Deployment, trust, and licenses

Runspaces do not isolate conflicting module assemblies. Microsoft documents dependency conflicts and advanced `AssemblyLoadContext` strategies; avoid promising that a new session prevents all dependency collisions. [Official dependency guidance](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/2a4b9570939a97008d5039b9b318f6e8585338bb/reference/docs-conceptual/dev-cross-plat/resolving-dependency-conflicts.md).

Start with ordinary JIT deployment without trimming/Native AOT. Native AOT restricts dynamic loading/code generation and requires trimming; Avalonia support for a deployment mode does not establish PowerShell/module compatibility. [Native AOT limitations](https://github.com/dotnet/docs/blob/45d22543cf002fb88caf8938c2580740c69d3f25/docs/core/deploying/native-aot/index.md#L166-L174).

Self-contained applications must service their bundled runtime. Execution policy is not a sandbox and is Windows-specific; imported kits/modules are executable software. [Patching policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core#automatic-patching), [PowerShell security guidance](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/77b22dfe57dc159b6398474e5bade6c3f4d8498f/reference/docs-conceptual/security/security-features.md#L10-L15).

The following are engineering observations, not legal advice:

- .NET runtime, PowerShell, and Avalonia core use MIT licenses with required notices. [Runtime license](https://github.com/dotnet/runtime/blob/v10.0.12/LICENSE.TXT), [PowerShell license](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/LICENSE.txt), [Avalonia license](https://github.com/AvaloniaUI/Avalonia/blob/8eeda4f6f546165b3f72e63c9f42247abb306905/licence.md).
- Preserve/review third-party notices for the exact dependency/native-asset graph, not only the three top-level licenses. [PowerShell third-party notices](https://github.com/PowerShell/PowerShell/blob/f260eb9c31ec72c5282f98e5ea24d9be4f8d7536/ThirdPartyNotices.txt).
- Review commercial terms separately before using current TreeDataGrid. Core MIT terms do not authorize every Avalonia-branded product.
- Generate an application-specific SBOM/notices bundle. PowerShell installer SBOMs do not prove that a NuGet-based application publish already has one. [PowerShell SBOM guidance](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/77b22dfe57dc159b6398474e5bade6c3f4d8498f/reference/docs-conceptual/security/security-features.md#L95-L104).

No conclusion is made about redistributing PowerGUI materials or third-party administration modules.

## Source identity and remaining validation

| Source | Recorded identity/date |
| --- | --- |
| .NET release metadata | Verified servicing release 2026-09-08 |
| .NET 10 OS table | Commit `44927bc821d37f596e317c4316335632bd38c79b`, updated 2026-09-28 |
| PowerShell | Tag `v7.6.6`, commit `f260eb9c31ec72c5282f98e5ea24d9be4f8d7536`, published 2026-09-08 |
| Avalonia | Release 12.1.3, commit `8eeda4f6f546165b3f72e63c9f42247abb306905`, published 2026-09-22 |
| Avalonia documentation | Commit `a0a3666ecc32b9031eace19da0b045aba0e09af8`, dated 2026-10-02 |

Before claiming support, restore/build the selected combination, publish on each OS/architecture, verify modules/native assets and `Add-Type`, exercise all streams/prompts/cancellation, validate property projection and live action input, test provider/platform gating, measure buffering/virtualization, and review licenses. These are future acceptance checks, not completed results.
