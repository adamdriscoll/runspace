# Implemented administration foundation

This iteration implements the built-in administration console. The architecture, UX, and kit-format proposals remain design context, not a statement that all their proposals are implemented. Extensibility, Console Kits, and installer development are deferred; remaining work and acceptance gates are tracked in [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24).

## Project layout

| Project | Responsibility |
| --- | --- |
| Runspace.Core | Built-in resource/action definitions, safe display cells, typed filtering/sorting contracts |
| Runspace.PowerShell | Persistent local runspace, serialized operations, original object handles, provider/CIM queries, actions, streams and cancellation |
| Runspace.Desktop | Native Avalonia tree/table/action shell, dialogs, history, diagnostics, clipboard/CSV, layout |
| Runspace.Tests | Core behavior and real embedded-runtime tests |
| Runspace.Desktop.Tests | Headless Avalonia interaction checks using a fixture session through the same interface |

The required runtime is .NET 10, with Microsoft.PowerShell.SDK 7.6.6 and Avalonia 12.1.3. DataGrid 12.1.2 is a deliberate MIT adapter choice for this foundation; its complete column/selection/sorting interface was inspected with ILSpy. Core TableView was inspected too, but moving/resorting columns and the full existing grid interaction contract would require additional implementation. No proprietary UI toolkit was reused.

The PowerShell module owns live objects; desktop bindings use cached cells rather than invoking arbitrary getters. Invocation and object-inspection work is serialized in the owning session. Provider paths are passed as literal parameters, not interpolated into executable code.

## Actual PowerGUI reference run

On 2026-10-05, the supplied installer was extracted as data into session artifacts. An original portable marker and the distribution's directory conventions were used to run the managed AdminConsole directly without installing PowerGUI.

The native portable launcher rejected the modern machine using obsolete Windows Management Framework detection. Directly launching the managed console in portable mode succeeded. Portable mode disables its usage-collection setting and keeps configuration/cache under the session-local reference directory.

The original console was observed at startup and with **Local System -> Processes** selected. Session-local screenshots captured the real window; they are not application resources or part of the repository.

Observed presentation:

- Compact File/View/Tools/Help menu and a narrow toolbar.
- A gray-captioned left navigation tree, central document-style Results tab, and gray-captioned right Actions pane.
- A thin query/breadcrumb strip and separate Filters strip above a dense read-only object table.
- Whole-row blue selection; process actions in General, DLLs/Threads in Related information, and separate Export/Reporting groups.
- Local System contains Processes, Services, Event Logs, Network Configuration, Registry, Drives, Shares, Local Users and Groups, and WMI Browser.
- Network contributes a Managed Computers view; the legacy installation also exposes a Hyper-V group.

Runspace follows the observed gray/neutral pane chrome and compact tree/table/grouped-action layout rather than the earlier unverified blue-pane proposal. It intentionally adds safe object-properties inspection, explicit errors/partial-result outcomes, a visible action pane, a provider-path entry, and a simpler splitter/tab layout.

The reference required a lengthy initial WMI/type initialization and logged incompatibilities with modern formatting data and obsolete web content. Those problems are not reproduced as features.

## Intentional limits

All actual PowerShell providers/drives are discovered in the embedded session. Available provider navigation is not synonymous with physical disks, and Windows-specific resources are not represented as portable.

Local-system domain queries are built in. Windows CIM, service, event-log, registry, and account views depend on the target OS, available modules, and permissions; failures are displayed. Managed Computers currently describes the local computer only. Hyper-V, domain modules, and remote-target management are not implemented.

Network Configuration uses real local network objects on all platforms. Its result table includes DHCP scope and WINS proxy fields only on Windows, where those property getters are supported.

The shell has splitters and tabs rather than a floating dock manager. CSV export is supported; legacy XML/HTML reporting and charts are not. Layout stores only window/pane sizes, not arbitrary live session state or credentials.

Mutating actions bind parameters and act on fixed selected handles. UI confirmation does not grant elevated rights. Cancellation is cooperative, and a refreshed table after partial errors is not reported as proof that every action succeeded.

The embedded custom host binds each parameter, choice, credential, and secure-input request to session/invocation/prompt identifiers. The engine thread waits for an asynchronous owned-window response; the dispatcher never waits synchronously. Stop, prompt dismissal, and session/application shutdown release host input and produce Cancelled. Unsupported raw-terminal operations fail explicitly. Delayed native calls stay visibly stopping/unresponsive without active-pipeline disposal.

Interactive history is non-replayable. Secure-input invocations discard returned object graphs and redact command/diagnostic text rather than attempting unreliable secret detection. The invocation owns and disposes accepted secure values. This does not prevent arbitrary scripts from copying values into session state or sending them elsewhere.

### Cancellation reference gate

The real-runtime `InvocationHostTests.StopSleepReachesCancelledWithinTwoSecondsAndNextQuerySucceeds` measures from cancellation request to terminal Cancelled, not from invocation startup. It retains partial output and then verifies a subsequent provider query. The two-second threshold is enforced by both the test timeout and elapsed-time assertion.

Reference host (2026-10-06): Windows 11 Business 10.0.26200, Intel Core Ultra 9 285HX, 24 cores/logical processors, .NET SDK 10.0.401/runtime 10.0.12, embedded PowerShell 7.6.6, Debug tests. A solution-suite run recorded **9.1 ms** from Stop request to terminal Cancelled for Start-Sleep and **21.7 ms** for prompt cancellation followed by a queued query. This is a cooperative gate, not a guarantee for other hardware or native calls. A separate native-wait fixture signals from inside its non-cooperative call and remains blocked until the test explicitly releases it. Both immediate and delayed host responses verify the unresponsive state, continued queue ownership, safe eventual cancellation, and session reuse without relying on a fixed sleep or pre-call readiness signal. Headless Avalonia tests cover Stop/application-close dismissal, dispatcher-safe validation, masked/cleared secrets, and selection changes during prompting; the disposable-child fixture covers WhatIf and command-confirmed Stop-Process.

## Development targets

`global.json`, public NuGet configuration, and package lock files express the local/CI build baseline. `.vscode/tasks.json` includes build, test, launch, and publish targets; `.vscode/launch.json` launches the desktop DLL through the C# debugger.

The desktop project has separate Debug/Release lock files because its development-only inspection bridge is excluded from Release. The supported publish runtime identifiers are declared together so locked restores remain valid for the three CI outputs.

GitHub Actions uses a three-OS matrix, runs tests, publishes self-contained runtime-specific outputs, and uploads build/test artifacts. Separate published-payload jobs launch downloaded artifacts, exercise the real embedded engine/desktop, and retain positive and missing-dependency reports. Linux validation uses a clean Ubuntu/Xvfb container; hosted Windows/macOS checks isolate the application's search paths but are not pristine-machine certification. See [recorded deployment evidence](publish-validation.md), not workflow configuration alone, for verified results.

See [Contributing](../CONTRIBUTING.md) for development commands and the [usage guide](usage.md) for console interactions.
