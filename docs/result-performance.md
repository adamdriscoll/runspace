# Result responsiveness and retention

Status: implemented defaults and reproducible reference measurements for [issue #4](https://github.com/adamdriscoll/runspace/issues/4). These are cardinality/text limits, not a sandbox or a hard process-memory ceiling.

## Reproduce the Release gate

Use the pinned SDK and dependencies, a graphical Windows desktop, at least four logical CPUs and 16 GiB RAM, and an otherwise idle machine:

```powershell
pwsh -NoProfile -File .\scripts\Measure-Results.ps1 -Results .\artifacts\result-benchmarks
```

The script builds Release and runs the engine, headless desktop, and native desktop measurements **sequentially**, with no additional benchmark packages. Reports contain SDK/runtime/package versions, OS, architecture, DPI, client size, sample counts and methods. `environment.json` records CPU, usable physical RAM, source commit and whether the measured checkout had uncommitted changes. Retain all four JSON reports with any comparison; do not compare a dirty checkout as if it were the unmodified recorded commit.

On a host without a graphical session, use `-SkipNative`; that is only the headless gate, not native desktop qualification. PowerShell 7 can run the script on other platforms with their graphical/native prerequisites, but CPU model and physical RAM must be recorded separately there. Native measurements can also be launched with:

```powershell
dotnet .\src\Runspace.Desktop\bin\Release\net10.0\Runspace.Desktop.dll --benchmark-results .\artifacts\result-benchmarks\native-desktop.json
```

Create the destination directory first. This mode uses only benign in-memory fixture rows, disables layout persistence, never executes an action or property getter, writes pass/failure evidence, closes its window, and returns a nonzero exit code on failure. Debug builds reject benchmark mode.

The fixture has exactly **20,000 rows and ten already evaluated scalar columns** with deterministic handles and integer values. This is a measurement workload, **not** the production query limit. Selection changes use a deterministic permutation of rows, exercising the real selection handler, action resolution and Actions pane. Twenty selection/scroll warmups precede 200 measured samples of each operation. The percentile is nearest rank, `sorted[ceil(0.95 * n) - 1]`.

The native method includes layout, the next animation-frame callback and a background-priority dispatcher barrier. The headless method uses Skia layout and a dispatcher barrier. Each scroll verifies that the requested row is realized, no more than 60 rows are realized, the grid's source/columns are unchanged, and the entire result is published only once. Neither method measures physical scan-out/input-to-photon latency; neither substitutes for every OS/GPU/DPI configuration.

The real embedded-engine fixture produces the same ten scalar properties on 20,000 PowerShell objects. After one cold startup and one workload warmup, ten samples record first output, terminal cached-result latency and full-GC managed-heap retention. The flood fixture issues 20,000 iterations of warning, verbose, debug, information and progress commands, then one non-terminating error and usable output. SDK stream and host callbacks can both observe a message: callback counts are not counts of unique PowerShell commands.

Ten cooperative-stop samples retain a partial row, signal readiness immediately before `Start-Sleep -Seconds 30`, allow 100 ms to enter the sleep, then measure **Stop request to terminal Cancelled**, enforcing two seconds on every sample. Startup is excluded. Existing native-wait/getter tests separately prove continued queue/lease ownership until a non-cooperative operation returns. No native interrupt latency guarantee is inferred from Start-Sleep.

## Reference results

Measured 2026-10-06 on the issue branch based on `390091a128273589810fda5a7cbea02dee7cfc72`, with the implementation changes uncommitted at measurement time. Hardware: Intel Core Ultra 9 285HX, 24 physical/logical cores, 68,137,205,760 bytes usable RAM (63.46 GiB). Windows 11 Business build 26200, x64; SDK 10.0.401, runtime 10.0.12, Microsoft.PowerShell.SDK/engine 7.6.6, Avalonia 12.1.3, DataGrid 12.1.2. Both desktop runs used a 1200 x 800 client area, scale 1.0 / 96 DPI. Native run used Avalonia's Windows backend; headless run used Skia software rendering.

| Measurement | Observed | Gate / interpretation |
| --- | ---: | --- |
| Native cached selection/action-pane p95, 200 samples | 18.55 ms | <=100 ms |
| Native scroll/layout/next-frame p95, 200 samples | 36.72 ms | <=100 ms regression budget |
| Headless cached selection/action-pane p95, 200 samples | 28.35 ms | <=100 ms |
| Headless scroll/layout p95, 200 samples | 47.22 ms | <=100 ms regression budget |
| Maximum realized rows, both desktop runs | 27 | <=60 for 20,000 rows |
| Grid publications / per-object dispatcher notifications | 1 / 0 | No full-grid rebuild per emitted object |
| Native fixture cold time to UI-ready | 1,867.74 ms | One startup observation; includes fixture/native bootstrap |
| Headless fixture time to UI-ready | 811.44 ms | One observation; already initialized test application, not comparable to native cold startup |
| Warm engine first-output p95, ten samples | 12.00 ms | From invocation entry, including scheduler wait |
| Warm engine completed cached-result p95, ten samples | 774.42 ms | This is when the current completion-only UI can accept the result |
| Cold engine warmup | 799.72 ms | One observation, not a percentile |
| Native / headless retained managed heap delta | 60.56 / 60.67 MiB | <=128 MiB scalar-fixture regression budget |
| Engine retained managed heap delta, median of ten | 36.36 MiB | <=128 MiB scalar-fixture regression budget |
| Peak SDK output buffer depth | 1 object | Incremental synchronous drainage; no per-object dispatcher queue |
| Flood callback records / duration | 200,001 / 1,721.40 ms | Includes duplicate host/SDK observations |
| Flood peak retained / final diagnostic records | 2,002 / 2,003 | 2,000 ordinary + one error + one progress; final record is the loss summary |
| Cooperative Stop p95 / maximum, ten samples | 12.11 / 12.11 ms | Every sample <=2,000 ms and terminal Cancelled |

The first engine object is **not** immediately displayed. `IConsoleSession` returns a completed result; getters and safe scalar caching remain on the worker, and the dispatcher receives one terminal table. Previous cached rows stay usable for scrolling while queries run, with actions disabled. There is no application per-object dispatcher queue or incremental full-grid rebuild. A future streaming UI must establish its own bounded batch queue and first-visible-result gate; these numbers do not certify it.

Managed-heap deltas are steady retained allocations after forced collection, not peak allocation, working set, native resource size or an arbitrary object graph's size. They include engine objects/cached rows or the fixture/window/control graph, respectively, and should not be added as if measured within one process. A ten-sample nearest-rank p95 is the maximum; startup and flood durations each have one observation.

## Implemented retention policy

`RetentionPolicy` is the shared source of constants; these defaults are not user-configurable yet.

| Surface | Default | Visible behavior / ownership |
| --- | --- | --- |
| Output per invocation, including multi-object actions | 25,000 objects | The first excess object requests cooperative pipeline stop. Retain the first 25,000; mark Failed with an incomplete-output/retention banner and export/requery guidance. A caller-requested cancellation remains Cancelled. Dispose overflow objects not already retained by identity. |
| Live results in the adapter | Four nonempty result sets, each <=25,000 rows | A fifth result's output is released, with a Failed retention notice; existing handles are **not** silently evicted. Empty results retain no handles and consume no slot. Callers must release results. A just-completed rejected result can transiently exist before this admission check. |
| Desktop current / related-result cache | One current / **zero** cached related results | A replacement releases the old handles; Back/Forward stores routes and requeries, never preserves old source objects. Current + action + refresh/expansion normally fits within the adapter budget. Superseded results are released. |
| Ordinary diagnostic streams | Latest 2,000 records per invocation and separately in the desktop pane | All ordinary streams share this budget. Older records are evicted with a persistent count/strategy notice; outcomes and the independent error budget survive ordinary flooding. SDK stream buffers are drained, not left growing behind the bounded presentation. |
| Errors | Latest 1,000 error records, separately budgeted | An invocation reaching 1,000 captured errors requests cooperative stop and finishes Failed with a retention notice. The desktop keeps the latest 1,000 across invocations; eviction is counted and visible. This preserves errors as errors and the original outcome, not an unlimited complete error transcript. Errors after stop/native return can still replace older records; that loss is reported. |
| Progress | One latest update | Earlier updates are coalesced and counted, including multiple activities. The present UI is a textual snapshot, not an activity-tree progress monitor. No dispatcher notification is posted per progress update. |
| Diagnostic message / history command text | 16,384 characters plus a shortening marker | Shortening is explicit, including error text; full original messages are not secretly retained elsewhere. A history entry always retains its outcome/timing header. |
| Loaded navigation | 200 children per location; 1,000 loaded nodes globally | Omitted children get a visible placeholder and diagnostics. Lazy placeholders do not count as loaded nodes. Collapse unloads provider/registry descendants; expand requeries. Temporary navigation result handles are always released. |
| Back/Forward routes | Latest 100 node definitions | Older routes are evicted with a persistent Back tooltip and query-status notice; use Location/tree to requery an evicted route. Routes own no object handles. |
| Invocation history | Latest **200** descriptions/outcomes | Retains the existing 200-entry policy, with a persistent eviction header after any loss. No selected-object graph is stored in history. |

Why these limits: the 20,000-row scalar load meets the 100 ms gate and retains about 61 MiB in the desktop measurement. A 25,000-row default allows modest headroom rather than extrapolating to hundreds of thousands of objects; four adapter slots accommodate the current view plus transient action/refresh/navigation results without a permanent back-view cache. The scalar-only reference stays below the separate 128 MiB regression budget. The 200,001-callback flood preserves its error with roughly 2,000 retained records instead of an ever-growing SDK buffer, queue and repeatedly concatenated UI transcript. The remaining count limits constrain navigation/history metadata independently of live-object lifetime. They are conservative product defaults, not a claim that every live object has the scalar fixture's memory cost.

Object release preserves the existing reader lease: an active getter owns its graph until it returns; new inspection/action requests reject released handles. Distinct disposable base objects are disposed on release. Retention stop does not roll back mutations or guarantee native interruption. A non-cooperative pipeline may keep working after a limit requests stop, although captured buffers remain bounded; arbitrary runspace variables, command internals, huge individual objects and external owners can still retain memory. No hard RSS limit or worker-process isolation is promised.

## Intentional export and requery

Before leaving a view, **Export table...** saves only the retained, currently filtered visible cached rows/columns. It does not recover discarded rows, reevaluate getters, or export the original full object graph. Clear the filter first to export every retained row. Copy the diagnostic/history text into a file before it is evicted or the window closes.

For complete output, deliberately requery a narrower literal provider location or run a suitable **read-only, replayable** command in a separate PowerShell with file-directed `Export-Csv` and stream logging. History is a description, not a replay engine: selected-handle actions, redacted commands and interactive invocations cannot be blindly copied to reconstruct their inputs. Never automatically repeat a mutation to recover output; partial side effects can remain even when retention finishes Failed. Use a fresh query to inspect actual state.

## Regression coverage and remaining qualification

`RetentionTests` exercises output overflow, cooperative stop on error flooding, SDK output-buffer depth, separate error/progress budgets, shortened text, explicit result-admission failure, disposal and stale handles. Desktop tests cover visible notices, global/per-location navigation limits, collapse/requery, the 100-route Back boundary, the existing 200-history boundary, related-result release and the 20,000-row virtualization/source-stability contract. A real-engine desktop fixture emits 20,000 objects plus warning/progress flooding while the previous table remains selectable/scrollable, then verifies exactly one terminal grid replacement and visible stream coalescing.

Timing and scalar-memory gates run when `RUNSPACE_BENCHMARK_DIR` is set, or through `Measure-Results.ps1`/native benchmark mode. Ordinary CI runs deterministic count/lifetime/UI assertions without imposing reference-hardware timing on shared hosted runners. Reference thresholds must be rerun when changing display templates, selection/action resolution, SDK capture or limits.

Only the Windows x64 / 96-DPI reference was measured natively here. Native macOS/Linux, additional DPI/GPU configurations, first-visible streamed output and hard termination/RSS containment remain separate qualification work. The reference gate is not a cross-platform performance certification.
