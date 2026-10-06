# Proposed architecture

Status: original design proposal. The current built-in-only implementation is described in [foundation notes](foundation.md); the kit/workspace expansion below is not implemented. See [official-source platform research](research/modern-platform.md) for package constraints.

## Constraints and direction

Target `net10.0`, PowerShell 7, and Avalonia desktop on Windows, macOS, and Linux. Select a mutually compatible PowerShell SDK/runtime line; "PowerShell 7" alone is not a compatibility specification.

The researched starting combination is .NET SDK 10.0.401/runtime 10.0.12, Microsoft.PowerShell.SDK 7.6.6, and Avalonia 12.1.3. Recheck servicing releases before scaffolding; this is a verified published combination of framework targets, not yet a tested application build.

Pin a .NET 10 SDK through `global.json` when scaffolding. The inspection machine also has a .NET 11 preview installed, so relying on the ambient newest SDK would not express the intended baseline.

Start with one local, application-owned PowerShell environment and **in-process hosting** as a proposed first adapter. This preserves live object identity and avoids prematurely inventing an object transport. Make execution a deep module whose interface covers invocation, object inspection, host prompts, cancellation, and lifetime.

An out-of-process worker offers crash containment and a path to hard termination, but changes object identity, serialization, prompt transport, and deployment. It is a later alternative, not a promised transparent switch. Neither topology is an OS security sandbox.

## Proposed repository shape

```text
src\
  Runspace.Desktop\          Avalonia shell, views, view models, UI adapters
  Runspace.Core\             Workspace, definitions, action resolution, result policies
  Runspace.PowerShell\       Hosting, live objects, streams, prompts, provider operations
  Runspace.Kits\             Manifest validation, package installation and catalog
tests\
  Runspace.Core.Tests\
  Runspace.PowerShell.Tests\
  Runspace.Kits.Tests\
  Runspace.Desktop.Tests\
kits\
  Runspace.LocalSystem\      Original built-in definitions and scripts
docs\
```

This is a starting shape, not a mandate to create empty projects. Add a project only when a milestone supplies behavior for it. PowerShell-specific types stay out of desktop bindings and core definitions.

## Deep modules and seams

| Module | Small caller-facing interface | Behavior hidden behind it |
| --- | --- | --- |
| Workspace | Open/save workspace; apply view preferences | Stable identifiers, validation, version migration, atomic persistence, unresolved references |
| Execution | Start an invocation; observe events; respond to prompts; request stop; release results | Runspace scheduling, parameter binding, streams, object ownership, host callbacks, shutdown |
| Object Inspection | Inspect object references and requested properties | Extended PowerShell properties, type hierarchy, getter errors, caching, safe display conversion |
| Navigation | Resolve roots/children; activate a node | Kit composition, provider capabilities, lazy expansion, context and request generations |
| Action Resolution | Resolve actions for a fixed context/selection | Type matching, cardinality, capabilities, dependency availability, disabled reasons |
| Result Presentation | Apply a view definition to a result set | Typed sorting/filtering, columns, heterogeneous rows, selection reconciliation |
| Kit Catalog | Inspect/install/enable/disable/export a kit | Schema checks, content hashes, safe extraction, dependency checks, trust and version state |

Do not add a public interface around every class. Introduce a seam where behavior actually varies: live PowerShell versus deterministic fixture execution; filesystem versus test storage; Avalonia prompts versus a test prompt adapter. Internal implementation seams can isolate complex runtime behavior without expanding the caller's interface.

### Dependency direction

```text
Desktop --------------------> Core
   |                            ^
   +----> PowerShell adapter ----+
   +----> Kit Catalog adapter ---+

Core must not reference Avalonia or System.Management.Automation.
PowerShell hosting must not reference Avalonia controls.
Desktop is the composition root; dependencies are supplied, not globally located.
```

Domain definitions describe object types, property descriptors, references, view definitions, and invocation outcomes. Opaque object references cross the execution seam; live `PSObject` instances remain owned by the PowerShell implementation.

Avoid spreading singleton state, stringly typed message buses, or provider-specific scripts through view models. Those would recreate coupling rather than the useful behavior observed in PowerGUI.

### Desktop control choices

Evaluate Avalonia `TreeView` for navigation and the core `TableView` introduced in 12.1 for object results. Current documentation deprecates the separate `DataGrid` package and identifies `TreeDataGrid` as a commercial offering; do not adopt either by assuming historical licensing/availability still applies.

Prove row virtualization, multi-selection, typed sorting/filtering, movable/resizable columns, context menus, focus, and accessibility before settling the table adapter. `TableView` does not virtualize columns, and its presence alone does not prove the complete result-view contract. Keep these behaviors in the result-presentation module rather than leaking control-specific assumptions into kit definitions.

## Runtime model

### Session ownership and serialization

A session owns its runspace, loaded modules, provider drives, runtime variables, and object handles. A workspace owns saved preferences and references to sessions, not their live state.

For the initial adapter, use **one serialized invocation queue per session**. Importing a module, loading a profile, enumerating a provider, evaluating a script property, querying objects, and executing an action all respect the same scheduler. A runspace or mutable `PowerShell` instance is never shared by concurrent pipelines.

Create a fresh invocation object for each operation. Dispose it after completion and detach stream handlers. Retain result-object handles separately until the view releases the result set.

Do not use a runspace pool to accelerate browsing without a defined session-state/drive synchronization model. Adding a drive in one pool member would not automatically make it available in another.

Multiple future sessions may run independently, but their in-process runtimes do not isolate them from malicious code.

### Invocation contract

An invocation has a unique identifier, session identifier, originating node/action identifier, request generation, fixed input references, typed parameters, and an expected result policy.

Events carry invocation and session identifiers. Emit lifecycle, object-added, diagnostic-stream, progress, and prompt events. Preserve ordering within each recorded stream; cross-stream callbacks do not establish a perfect original chronological ordering.

Proposed lifecycle:

```text
Queued -> Running -> AwaitingInput -> Running
                   -> Stopping -> Cancelled
                   -> Completed
                   -> CompletedWithErrors
                   -> Failed
```

Also allow cancellation before start and failures during session startup. A requested stop is not completion. Non-terminating errors can coexist with valid results; a terminating failure may still leave partial results.

Navigation generations prevent late results from replacing the currently selected view. Completed results from superseded requests must be released or attached only to their own history; they must not mutate the new view.

### Object identity and display

Return live PowerShell objects internally, not `Format-Table` output or strings. Preserve extended type names, property metadata, and the original input objects needed by actions.

An object reference contains session/result/handle identity. Selection is a snapshot of references, not row positions or formatted cell values. Validate it when beginning an invocation; reject stale or foreign-session references with a useful error.

Cached display values and property descriptors can cross into the UI. Property access, especially script properties and arbitrary getters, must not happen on the Avalonia dispatcher. Evaluate through the execution/inspection module, with explicit pending and failure states.

Avoid holding a runtime lock while posting events or waiting for a prompt. Convert safe scalar values for display; bound recursion when inspecting arrays/nested properties and detect cycles.

Release object graphs when a result view closes, is replaced, or is evicted. History stores a redacted description, not permanent references to every returned object. The built-in implementation now has a [measured retention policy](result-performance.md): one current result, no related-result cache, 25,000 rows per invocation, four adapter result slots, bounded streams/navigation and the existing 200-entry history. Back navigation requeries; overflow/loss is visible and releases handles. These are cardinality limits, not a hard process-memory ceiling.

### Parameters and action input

Build command invocations with typed parameter binding. Pass selected objects through pipeline input where the action declares it. Scripts have a documented parameter/input contract rather than substituted strings.

Never concatenate provider paths, resource names, credentials, or filter input into executable PowerShell text. Use literal-path parameters where available. Process the original typed objects, not a command regenerated from the visible grid.

Resolve action eligibility using declared types/capabilities/cardinality without running arbitrary eligibility scripts on every selection change. An action freezes its inputs before prompting and declares whether to retain, refresh, replace, or open a related view.

### Host, streams, and cancellation

Supply a PowerShell host adapter for parameter/choice/credential/secure-input prompts and progress/output behavior. Host callbacks that require synchronous answers wait on the execution side; the UI presents/answers them asynchronously without blocking its dispatcher.

Prompt cancellation, window closing, and invocation stop unblock the waiting host callback. Do not let a modal credential dialog survive its invocation.

Capture output, errors, warnings, verbose, debug, information, and progress distinctly. Coalesce high-frequency progress/UI notifications without losing errors or misreporting completion. Show unsupported raw-terminal operations explicitly; a GUI host is not automatically a full interactive terminal.

Use caller-owned incremental output buffers and explicitly complete supplied input buffers. The verified SDK has task-based invocation but callback-based asynchronous stop, not a cancellation-token stop overload. SDK-only hosting also does not supply `pwsh` for `Start-Job`; declare that limitation unless an independently validated executable deployment is added.

Cancellation is cooperative through the supported PowerShell stop facilities. A blocked native operation may not stop promptly. Do not use thread abortion, claim guaranteed interruption, or dispose the runspace under an active pipeline. If it becomes unresponsive, mark the session accordingly; an isolated worker is the later option for hard termination.

## Provider-drive administration

The execution module enumerates session drives and provider capabilities. Navigation-capable providers expose lazy children; item-only providers expose an appropriate item view. A Provider Drive is not assumed to map to a filesystem path.

Drive creation/removal is serialized with queries in its owning session. Successful creation refreshes navigation and results. Failed creation leaves the catalog unchanged and exposes the underlying error.

Persist an optional drive definition only after explicit user choice. Credentials and secret parameters are never persisted. Do not silently re-create drives at launch; a saved definition and an active drive are different states.

Availability is determined by the installed runtime, loaded provider/modules, OS, and target. Cross-platform FileSystem/Environment examples do not justify enabling Registry or Windows-only administration everywhere.

## Storage and trust

Use platform-standard per-user configuration/data locations. Separate versioned workspace JSON, installed kit content, kit trust state, and redacted diagnostic/history storage.

Write atomically, validate before replacing, and report corrupt/unsupported files with an actionable recovery choice. Do not silently overwrite them with defaults.

Imports inspect data without executing scripts. Enabling a third-party kit or importing its dependencies requires explicit trust/compatibility checks. Updates change content identity and invalidate prior trust decisions. PowerShell execution policy is not a security boundary.

Profiles are disabled by default; offer explicit inspection/opt-in later. Do not elevate the process automatically. Persist credential references only if a later credential-store design is approved, never secret values.

See [Console Kits](console-kits.md) for package lifecycle and limits still to decide.

## Testing through the interfaces

Core tests exercise fixture objects with deterministic identities and action definitions. Runtime tests cross the same invocation/inspection seam with a real PowerShell host. Do not replace runtime behavior with a mock and call that provider compatibility.

Cover module bootstrap, provider discovery, unusual literal paths, mixed result types, getter failures, non-terminating errors, terminating errors, cancellation, prompts, action selection, stale generations, and object release.

Use disposable directories/drives and child processes; never test destructive actions against arbitrary machine resources. Give temporary drives unique names and remove only the drives created by the test.

Desktop tests verify the visible tree/grid/action contract, keyboard focus, dispatcher confinement, and layout persistence with a fixture adapter. A separate smoke pass uses the real runtime on all supported OS/architecture combinations.

## Alternatives and decision gates

| Question | Proposed first step | Trade-off / gate |
| --- | --- | --- |
| In-process vs. worker process | In-process local adapter | Live objects and simpler prompts vs. crash containment/hard stop; approve before scaffolding |
| Full docking library vs. splitters/tabs | Splitters and document tabs | Faithful spatial layout now; floating/docking fidelity later |
| Native Avalonia views vs. embedded browser | Native Avalonia | Consistent object interaction/accessibility; no legacy HTML start-page dependency |
| Legacy XML vs. new kit format | New manifest and original scripts | Clear contracts/trust; no immediate PowerPack compatibility |
| Reflection-heavy runtime vs. Native AOT | Ordinary .NET publish | PowerShell/module dynamism needs proof before trimming/AOT is considered |
| TableView vs. legacy DataGrid/commercial TreeDataGrid | Core TableView spike | Validate the complete grid contract and licenses before choosing an adapter |
| SDK-only vs. redistributable runtime payload | Validate a publish spike | A NuGet reference alone does not prove required modules/native assets are shipped |
| Unbounded results vs. retention limits | [Release reference workload and bounded policy](result-performance.md) | Implemented built-in limits; arbitrary graphs, native interruption and additional native platform/DPI performance need separate qualification |

Record durable decisions as ADRs only after their trade-offs are actually agreed. These proposals intentionally remain easy to revise.
