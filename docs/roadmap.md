# Delivery roadmap

Status: proposed sequence. Each milestone should deliver an observable slice of the console, not a collection of disconnected projects.

## 0. Confirm the foundation and capture a visual reference

Review the [product direction](product-vision.md), [console design](ux/admin-console.md), and [architecture alternatives](architecture.md#alternatives-and-decision-gates). Confirm the working Console Kit name and first-release scope.

Create an isolated Windows reference environment for the original console rather than installing it on the developer's shared machine. Capture clean-profile startup, process results, row selection/action groups, filters, history, prompts, and pack management at known DPI/client sizes. Use benign data and keep copyrighted assets/reference captures out of distributable product resources.

**Exit gate:** each significant look/feel requirement has either an observed baseline or an explicitly accepted modern deviation. Current binary observations remain useful even if executing the historical product is not feasible; document that limitation rather than inventing parity.

## 1. Runtime and publish spike

Scaffold the smallest .NET 10 desktop host and pin compatible PowerShell/Avalonia package versions using the [platform research](research/modern-platform.md). Approve the initial execution topology first.

Prove local embedded execution, module discovery, provider-drive enumeration, typed objects, streams, and stop handling. Publish and run outside the development checkout on Windows, macOS, and Linux; verify required PowerShell modules/native assets rather than assuming a successful build means a complete payload.

**Exit gate:** one published application per target platform can run a benign process query, enumerate available providers/drives, display a real runtime version, and show a useful error when a dependency cannot load. Document unsupported OS/architecture combinations.

## 2. Recognizable shell with deterministic data

Build menu/toolbar/status chrome, left tree, center result document, right grouped actions, diagnostics region, splitters, reset layout, and an object-properties dialog.

Use original fixture objects and the same execution interface planned for the live adapter. Establish keyboard focus and compact visual styles before adding broad domain functionality.

Evaluate core `TreeView`/`TableView` against the grid contract; current Avalonia docs deprecate DataGrid and license TreeDataGrid separately. Do not let a table-control choice silently remove column reordering, selection semantics, or accessibility.

**Exit gate:** the declared [layout targets](ux/admin-console.md#proposed-layout-targets) work at 1000 x 680 and 1200 x 800 client areas. At least 20 rows fit at the latter size with diagnostics collapsed. 150%/200% DPI does not clip commands. Selection updates action eligibility without running scripts.

## 3. Local System: objects and provider drives

Connect the real runtime to original Processes, Provider Drives, and Environment definitions. Implement lazy provider navigation, typed column values, sorting, client filtering, multi-selection, and cached property inspection.

Add/remove temporary provider drives in the owning session using literal inputs. Capability-gate container navigation and explain unavailable operations. Do not conflate provider drives with disks.

**Exit gate:** an administrator can browse a filesystem drive and inspect environment entries on all supported platforms. A newly created test drive appears without restart; removing it does not delete its target directory. A foreign-session/stale object reference cannot be invoked.

## 4. Contextual operations, prompts, history, and cancellation

Implement one safe action and one explicitly confirmed mutating action against a disposable child process. Wire fixed selection input, parameter/credential/choice prompt behavior, declared result transitions, diagnostics, and PowerShell history.

Handle overlapping navigation by generations and session execution by serialization. Keep failures, partial results, and cancellation distinguishable.

**Exit gate:** action-pane and context-menu eligibility agree for zero/one/multiple/mixed-type selections. Changing selection during a prompt does not retarget the invocation. Cancelling a cooperative test script closes its prompt, unblocks the session, and leaves a cancelled outcome. History never contains credentials and labels non-replayable input honestly.

## 5. Console Kit lifecycle and workspace persistence

Implement the reviewed manifest schema, validation, safe installation, enable/disable/remove, content-based trust, a development folder, and workspace/layout persistence.

Test both a navigation-and-actions kit and an action-only kit. Add export after installation and ownership rules are stable.

**Exit gate:** importing a kit does not execute its code. Changed content invalidates trust. Missing dependencies and unsupported schema versions produce useful errors. A failed upgrade leaves the prior kit intact. Restart restores preferences but does not silently execute queries, recreate drives, or reconnect sessions.

## 6. Fidelity and distribution

Compare the modern shell against reference captures or explicitly documented static-only baselines. Refine compact chrome, grid density, action grouping, focus/hover states, and prompt behavior without reusing historical artwork.

Finish platform-specific packaging, dependency/license notices, crash reporting policy, explicit storage locations, and clean-machine smoke tests.

**Exit gate:** the defining administrator walkthrough works on each supported platform using packaged builds; limitations and intentional deviations are recorded. Do not market full PowerGUI parity.

## Later expansion

Windows-specific local views; remote sessions and reconnect semantics; Console Kit authoring forms; package repositories and reviewed update flows; charts/reports/snapshot comparison; a separate scripting workspace; an opt-in legacy importer; domain kits for directory services, virtualization, and cloud modules.

Each expands an already validated object/tree/action model rather than changing it into a different product.

## Acceptance scenarios

These are proposed acceptance requirements for implementation, not test results from this documentation task.

| Scenario | Expected outcome |
| --- | --- |
| Query returns no objects | Clear empty state, successful invocation, no error banner |
| Query returns objects and a non-terminating error | Usable partial results, error count, completed-with-errors state |
| Query throws after producing objects | Failed outcome; any retained objects marked incomplete |
| Rapid navigation A -> B | A cannot replace B's results or action context |
| Select 0, 1, several, then mixed types | Identical eligibility in pane/context menu; reasons for known disabled actions |
| Filter hides a selected row | Selection reconciled visibly; no hidden targets acted on accidentally |
| Property getter fails or blocks | No UI-thread evaluation; explicit pending/error presentation |
| Action targets an exited process | Useful stale-target error; no action on a reused row/index |
| Path contains quotes, brackets, or wildcard characters | Literal resource operation; no text interpolation into scripts |
| Provider lacks container navigation | Honest item view or explained unsupported operation |
| Cancel while awaiting host input | Prompt dismissed, execution unblocked, no success-shaped outcome |
| Native call ignores cooperative stop | Stopping/unresponsive state; no claim of guaranteed hard termination |
| Import traversal/oversized/colliding archive | Rejected before activation; installed kits remain intact |
| Kit code changes after trust | Disabled pending a new trust decision |
| Corrupt workspace | Explicit error/recovery; original file not overwritten silently |
| Close result/related view | Runtime object handles released according to the approved retention policy |
| Restart with missing kit/session | Unresolved references visible; no silent network/code execution |
| Keyboard-only/high-contrast operation | All essential controls reachable; state not conveyed by color alone |

## Performance targets to validate

Use a documented reference machine (at least four logical CPU cores and 16 GB RAM), a release build, and 20,000 deterministic in-memory fixture rows with ten already evaluated scalar columns. This is a test workload, not a limit on real query results.

Target cached selection/action-pane updates within 100 ms at the 95th percentile, with responsive scrolling and no full-grid rebuilding on each stream item. Record hardware, OS, DPI, runtime/package versions, samples, and measurement method.

For a cooperative `Start-Sleep`-based cancellation fixture, target a terminal cancelled state within two seconds of Stop. That target does not apply to blocked native code or unsupported interrupt behavior.

Measure first-result latency, retained memory, queue depth, and stream flooding before setting result/stream/history limits. Any cap must have visible truncation and an intentional export/requery strategy.

## Decisions still requiring agreement

| Decision | Starting proposal |
| --- | --- |
| Replacement name and package extension | Console Kit / `.rskit` |
| First-release focus | Local processes, provider drives, environment; administration console before script IDE |
| PowerShell topology | In-process local session first; worker isolation later if warranted |
| Pane flexibility | Splitters/tabs first; full docking after behavior is validated |
| Kit trust | Explicit activation; content changes require renewed trust |
| Profiles/remoting | No automatic profiles or reconnection |
| Retention/archive limits | Measure first; define concrete limits before production |
| Supported OS/architectures | Choose from verified .NET/PowerShell/Avalonia intersection and publish results |

Resolve these before implementation fixes them into persisted formats or public interfaces.
