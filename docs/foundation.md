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

### Result retention and pending inspection

The desktop retains **one current live result set**, not a cache of previous result sets. While a replacement query is pending, the previous cached rows remain visible with actions disabled and an explicit query/display-property pending message. The old result is released when the replacement is accepted, when an overview evicts it, or when the window closes. A late result from a superseded navigation/action is released without replacing the current rows, actions, prompts, result banner, or status. Its redacted invocation description/outcome and diagnostics can still be recorded.

| Action result policy | Live-object retention |
| --- | --- |
| Retain | Keep the current result; release the action output after recording its outcome. |
| Refresh | Requery the current node, release the replaced result, and release the action output. Keep the action's partial/failed/cancelled outcome visible unless a newer navigation supersedes the refresh. |
| Replace | Release the current result and retain the action output in its place. |
| Related | Like Replace, but retain a navigation route back, not the source objects. Back/Forward requery the node and obtain fresh handles; leaving the related view releases its result. |

Navigation history contains node definitions; invocation history contains strings with descriptions, outcomes, and timing, bounded to the latest **200 invocations**. Neither history owns result sets or selected live objects. Parameter redaction is performed by the execution adapter before recording; cached cells and object handles are not replayable commands. There is no separate related-result cache to evict.

`ReleaseResult` immediately makes the handles unavailable to new actions/inspections. An already-running reader holds a lease until it exits, so release cannot dispose an object underneath a getter. Then the adapter drops its graph references and disposes each distinct disposable base object once within that result set. Session shutdown cancels queued work and waits for the serialized active operation before disposing retained results and the runspace. Results held by callers must be explicitly released; a live session variable or another external owner can still retain an object after its result is released.

Display getters run on the execution worker, never the Avalonia dispatcher. The table receives completed cached scalar cells, not incrementally evaluated live properties. Failing getters display `(unavailable)` with an error tooltip and diagnostics; successful empty queries display a neutral "No objects were returned" message without opening Diagnostics. Queries with non-terminating errors keep usable rows and `CompletedWithErrors`; terminating failures retain their rows with an explicit incomplete-result warning. Banners count captured error **records**, not a guaranteed total of underlying getter failures: display diagnostics are bounded and summarize omitted failures.

Property inspection has its own visible pending state and cancellation scope. Navigation, Stop, or closing cancels it and prevents a late properties dialog from opening. Blocking native getters remain cooperative-cancellation limits: they retain the session queue and reader lease until they return, without blocking the dispatcher or allowing another pipeline to use the runspace. Stop remains visible as stopping/unresponsive after two seconds if the same operation has not finished. Cancellation during display materialization is reflected in the final outcome even if the pipeline itself had already completed.

`InvocationLifecycleTests` exercises the headless window/fixture seam, including controlled late completions, all four result policies, history eviction, and a real-runtime getter on the dispatcher boundary. `SessionLifetimeTests` uses the embedded runtime for empty/partial outcomes, weak-reference graph collection while cached rows survive, deferred reader disposal, queued cancellation, overlapping action/navigation/inspection, and shutdown. Gates signal from inside getters rather than using sleeps to guess when an operation started.

### Cancellation reference gate

The real-runtime `InvocationHostTests.StopSleepReachesCancelledWithinTwoSecondsAndNextQuerySucceeds` measures from cancellation request to terminal Cancelled, not from invocation startup. It retains partial output and then verifies a subsequent provider query. The two-second threshold is enforced by both the test timeout and elapsed-time assertion.

Reference host (2026-10-06): Windows 11 Business 10.0.26200, Intel Core Ultra 9 285HX, 24 cores/logical processors, .NET SDK 10.0.401/runtime 10.0.12, embedded PowerShell 7.6.6, Debug tests. A solution-suite run recorded **9.1 ms** from Stop request to terminal Cancelled for Start-Sleep and **21.7 ms** for prompt cancellation followed by a queued query. This is a cooperative gate, not a guarantee for other hardware or native calls. A separate native-wait fixture signals from inside its non-cooperative call and remains blocked until the test explicitly releases it. Both immediate and delayed host responses verify the unresponsive state, continued queue ownership, safe eventual cancellation, and session reuse without relying on a fixed sleep or pre-call readiness signal. Headless Avalonia tests cover Stop/application-close dismissal, dispatcher-safe validation, masked/cleared secrets, and selection changes during prompting; the disposable-child fixture covers WhatIf and command-confirmed Stop-Process.

### Local-resource regression gate

The provider-drive and stale-object gate extends the existing embedded-runtime and headless-window tests rather than adding a parallel harness.

| Scenario | Coverage |
| --- | --- |
| Lazy navigation and provider limits | Runtime container/leaf metadata and actual flat providers; headless expansion loads one level, ignores duplicate expansion while pending, and releases temporary results. Flat providers expose no fabricated Browse or mutation actions. Failed/partial expansion remains retryable, including partial output with no usable containers. Successful retry restores the previous result message instead of leaving a stale incomplete-navigation warning. |
| Literal paths and retained data | Uniquely named temporary FileSystem drives use quoted/bracketed roots, child paths, and drive names. Wildcard-looking names have decoys to detect expansion. Removing a drive leaves the root and file contents intact; another session never sees that drive. Environment queries/edits/removals bind quotes, brackets, `*`, `?`, and script-looking text literally. |
| Owning-session ordering | An inspection getter signals while holding the session queue. Creation, duplicate creation, discovery, removal, failed repeated removal, and subsequent discovery execute in order after explicit release. Another session remains independently usable. Headless Add/Remove refresh both the tree and table without restart while preserving failed outcomes. |
| Object ownership and exited processes | Unknown, foreign-session, wrong-result, mixed valid/unknown, and released handles fail before mutation or confirmation. A stopped disposable child cannot be targeted through its retained handle, including process-related views and Windows priority changes; a new child remains alive. A refreshed result rejects the old handle instead of resolving a row index. |
| Explicit failures | Missing provider creation reports an error without introducing a drive. Flat-provider child-path errors are not empty successes. Windows service tests inject a session-local permission denial and disable module autoload to verify unavailable-command errors, then restore the session capabilities. Non-Windows tests reject Windows-only built-in and related resource kinds explicitly. |

On 2026-10-06, the solution suite passed on Windows (Debug) and in an isolated Linux container using `mcr.microsoft.com/dotnet/sdk:10.0.401` (Release): **59 runtime/core and 53 headless tests** on each host. Windows-specific tests return without exercising their Windows branches on Linux; these counts do not mean every Windows scenario was executed there. The existing CI matrix also runs the suite on macOS, but macOS was not locally exercised for this gate.

Limits: Windows forbids `*` and `?` in FileSystem names, so those physical-path cases run only on Unix; environment-name cases run on both platforms. Permission failures are injected at the engine command boundary, not produced by changing host ACLs or requiring elevation. Registry/certificate/service/CIM reads remain Windows-only and depend on host capabilities. OS PID reuse is not forced: the regression verifies rejection after exit and across refreshed handles, while production also checks the retained PID/start-time identity before using the original Process object. No test targets arbitrary processes, drives, or persistent environment settings.

## Development targets

`global.json`, public NuGet configuration, and package lock files express the local/CI build baseline. `.vscode/tasks.json` includes build, test, launch, and publish targets; `.vscode/launch.json` launches the desktop DLL through the C# debugger.

The desktop project has separate Debug/Release lock files because its development-only inspection bridge is excluded from Release. The supported publish runtime identifiers are declared together so locked restores remain valid for the three CI outputs.

GitHub Actions uses a three-OS matrix, runs tests, publishes self-contained runtime-specific outputs, and uploads build/test artifacts. Separate published-payload jobs launch downloaded artifacts, exercise the real embedded engine/desktop, and retain positive and missing-dependency reports. Linux validation uses a clean Ubuntu/Xvfb container; hosted Windows/macOS checks isolate the application's search paths but are not pristine-machine certification. See [recorded deployment evidence](publish-validation.md), not workflow configuration alone, for verified results.

See [Contributing](../CONTRIBUTING.md) for development commands and the [usage guide](usage.md) for console interactions.
