# Console Kit contract v1

Status: **agreed format and compatibility contract, not a shipped extension feature** (2026-10-07). The desktop still uses built-in definitions and does not load kits. [Issue #8](https://github.com/adamdriscoll/runspace/issues/8) also requires semantic diagnostics and execution integration; those gates remain open. This agreement deliberately delivers the contract, schema, and original examples before the loader, installer, and shared execution adapter.

The normative manifest shape is [JSON Schema Draft 2020-12](../schemas/console-kit-v1.schema.json). The rules below are also normative: schema validation alone cannot establish reference integrity, resource safety, runtime compatibility, trust, or executable behavior. See the [versioning decision](adr/0001-console-kit-versioning.md).

## Identity and package layout

**Console Kit** is the canonical name; **`.rskit`** is a ZIP archive, not a renamed PowerShell module or legacy `.powerpack`. An authoring directory has exactly the same logical contents:

```text
runspace.processes\
  kit.json
  scripts\
    Summarize-Process.ps1
  icons\
  README.md
  LICENSE
```

`kit.json` must be at the root: no enclosing directory, multiple manifests, comments, trailing commas, or duplicate JSON member names. Use UTF-8 without a byte-order mark. Manifest paths use `/` as the portable separator; native authoring/install paths use the host filesystem's separator.

Required metadata: `schemaVersion`, `id`, `name`, `version`, `publisher` (name and absolute HTTPS URL), `description`, `license`, `compatibility`, `dependencies`, `resources`, `nodes`, `queries`, `views`, `actionGroups`, and `actions`. Empty arrays are explicit. At least one node or action is required. An action-only kit has empty `nodes` and `queries`; it is not a special execution mode.

| Identity | Rule |
| --- | --- |
| Kit ID | Lowercase dotted segments, each containing lowercase ASCII letters/digits and internal hyphens; for example `runspace.processes`. Publisher ownership is a human/provenance check, not established by the spelling of an ID. |
| Definition ID | Lowercase ASCII letters/digits with internal dots/hyphens. Unique across all nodes, queries, views, action groups, and actions in a kit. |
| Saved reference | `{ "kitId": "runspace.processes", "definitionId": "processes.table" }`; no version, display label, row number, or resource path in identity. |
| Nested IDs | Column IDs are unique within their view; parameter names are unique within their invocation, case-insensitively. They are not kit-wide definition IDs. |
| Release version | Strict SemVer 2.0.0, including optional prerelease/build identifiers. Build metadata does not establish precedence or trust. |
| License | A nonempty SPDX expression in `license.spdx`, or an owned package license resource in `license.file`, exclusively. A loader must validate the SPDX expression, not treat any nonempty string as a valid license. |

IDs survive display renames and compatible upgrades. Removing a definition or changing its meaning/input incompatibly requires a kit major release; references are not silently retargeted. Only one installed release of a kit ID may be enabled per session. ID conflicts require an explicit replace/update decision. Removed-kit references remain visibly unresolved or are cleaned up only by explicit user choice.

Definition references are kit-local and kind-checked: a `queryId` cannot resolve to a view just because its ID exists. V1 has no cross-kit references, kit dependencies, overrides of another publisher's definitions, or scripts that find objects by saved row position. Action-only kits can target any result set by object type without naming its source kit.

## Three independent versions

| Field | Meaning and comparison |
| --- | --- |
| `schemaVersion: 1` | Exact integer manifest-shape revision. Unsupported revisions are rejected, never guessed or coerced from strings. |
| `version` | The kit publisher's SemVer release identity. Changes do not automatically change the host contract. |
| `compatibility.runspace` | Bounds on the **Kit Contract** implemented by the host, initially `1.0.0`, independent of desktop application release numbers. |
| `compatibility.powerShell` | Bounds on the actual embedded engine, not an installed `pwsh`, Windows PowerShell, or the .NET SDK. |

Ranges are objects containing **both** `minInclusive` and `maxExclusive`. For Runspace and PowerShell these are stable `major.minor.patch` numeric versions, compared numerically, with `minInclusive < maxExclusive`. No wildcards, caret/tilde expressions, open-ended ranges, prerelease bounds, or implicit future-major compatibility. Prerelease host/engine versions cannot enable a v1 kit. Future host contracts may explicitly support older contract semantics; passing numeric bounds alone never authorizes interpreting an old manifest with new semantics.

```json
{
  "runspace": { "minInclusive": "1.0.0", "maxExclusive": "2.0.0" },
  "powerShell": { "minInclusive": "7.6.0", "maxExclusive": "8.0.0" },
  "platforms": ["windows", "macos", "linux"]
}
```

`compatibility.platforms` is a nonempty subset of these three lowercase tokens. The platform is the owning execution session's platform, not a label supplied by a kit. A range expresses an author's requirement, **not certification of every enclosed release**. The host must also support the exact schema/contract semantics and a qualified engine/platform combination in its release matrix. Current built-in PowerShell 7.6.6 deployment evidence does not prove kit support.

Unsupported schema is a validation failure. An otherwise valid kit with an incompatible host/engine/platform is inspectable and installable disabled, with the exact required/actual versions and disabled reason. Do not silently broaden bounds or partially enable an incompatible kit.

Schema v1 is frozen. Corrections that clarify existing behavior may update this document, but new fields, kinds, parameter types, handlers, or capability vocabulary require a new schema revision and an appropriately versioned host contract. Breaking semantics require a host-contract major change; additive semantics require at least a minor change. Hosts explicitly advertise the schema revisions and contract versions they implement; no automatic JSON migrations.

## Dependencies and capability discovery

`dependencies.modules` lists external installed module names and numeric version bounds. Module versions are PowerShell/.NET numeric versions with two to four components, compared as four-component tuples padded with zeros; they are **not kit SemVer**. Both bounds are required and must be ordered. Module names are unique case-insensitively. V1 does not support prerelease modules, bundled modules, module downloads, or Windows PowerShell compatibility imports.

`dependencies.providers` lists case-insensitively unique required provider names. A missing provider may become available after importing a declared module; discovering a provider does not create a drive or execute a query.

Inspection reads manifests/package metadata only. It must not execute scripts, import modules, load profiles, or run initialization hooks. After explicit trust, enabling may import a compatible installed dependency into the owning session's serialized queue. Reuse a single compatible already-loaded release; otherwise select the highest stable installed version satisfying the range. Conflicting origins for the same selected name/version require an explicit decision. If an already-loaded module of that name is incompatible or ambiguous, report the conflict; do not unload/replace it behind an administrator's back. Verify the actual loaded version and providers after import. Failed imports leave the kit disabled with their diagnostics; imports can change session state and cannot be promised transactional rollback.

No automatic module installation, elevation, profile loading, or dependency isolation. All declared dependencies are kit-wide and required; a definition-level restriction cannot make one optional. Use separate kits when platform-specific dependencies would otherwise disable a portable contribution. Modules and providers belong to the session, not to a kit or workspace. Disabling a kit withdraws its contributions but does not unload shared modules, undo script changes, or remove user drives.

Nodes, queries, and actions may have `requirements` with narrower `platforms` and/or `capabilities`. Effective requirements are the conjunction of kit, node/query (when navigating), and action requirements. A definition's platforms must be a subset of its kit platforms. Unknown capability tokens fail schema validation; recognized but unavailable capabilities disable the contribution with a reason, without executing it.

The v1 capability vocabulary is:

- `host.object-properties`: the versioned host inspection handler is available.
- `provider:<name>:item` or `provider:<name>:navigation`: cached provider interface support.
- `provider:<name>:filter`, `include`, `exclude`, `expandWildcards`, `shouldProcess`, `credentials`, or `transactions`: actual cached PowerShell provider capability flags.

Provider names in capability tokens compare case-insensitively; the other tokens are exact. Provider capabilities are not OS privileges, filesystem paths, or authorization grants. Providers used in tokens must be declared dependencies. Capture capability/type metadata on the execution worker and publish an immutable snapshot; selection updates must not trigger discovery/imports/getters.

## Nodes, queries, and views

V1 node kinds are `folder` and `query`. A folder has no query. A query node requires a `queryId`; that query owns its `viewId`. `parentId`, if present, must refer to a folder in the same kit. Roots omit it. Siblings and action groups sort by ascending `order` (default zero), then ordinal ID; actions within a group keep manifest order. An `icon` refers to a declared resource whose bytes must validate as PNG. No embedded image data, SVG/HTML, dynamic child scripts, or provider-tree contribution in this revision.

Queries declare `kind: command` with a module-qualified `command` (`ModuleName\Verb-Noun`), or `kind: script` with a declared `.ps1` resource path. Commands must name a declared dependency, resolve to that loaded module, and resolve to a cmdlet/function rather than an alias or native executable. Scripts run with local scope in the owning session. There is no inline executable text, interpolated command template, script entry point, or install/uninstall hook.

A query has declared `parameters`, optional `outputTypes` hints, and a required `viewId`. Selecting its node prompts for missing required parameters and binds values by name. Navigation queries consume no selected pipeline objects. Queries are author-declared read-only; intentional mutations belong in actions with confirmation, but this distinction is not an enforced security boundary. Type hints help interpretation but never establish action eligibility without inspecting actual returned objects.

Views declare ordered columns with a local `id`, `property` path, `label`, and `type`. A property path is a nonempty array of literal property-name segments, for example `["FileVersionInfo", "FileVersion"]`; it is not a PowerShell expression, wildcard, or method call. Dots inside a segment are literal. Types are `string`, `integer`, `number`, `boolean`, or `dateTime`; optional `format: bytes` is limited to numeric columns. Optional widths are positive display hints, not an accessibility constraint.

`defaultSort` refers to declared column IDs with `ascending`/`descending` direction; sort keys cannot repeat. `identityProperties` are optional display/confirmation hints, not replacements for session/result/object handles. Missing, null, pending, and failing properties remain distinguishable; conversion failures produce visible diagnostics rather than silently converting to zero/false/empty text. Follow the host's typed sorting/filtering and retention rules.

Property getters, including extended/script properties supplied by an object, run only on the owning execution worker under existing inspection/lifetime rules. V1 has no kit-defined computed-column expressions. Neither rows nor type hints carry executable callbacks into desktop bindings.

## Typed parameters and selected input

Every query/action declares a `parameters` array, even when empty. Each declaration supplies `name`, `label`, `type`, and `required`; it may supply a non-secret typed `default` and nonempty unique `choices`. Names are ASCII PowerShell parameter identifiers, matched case-insensitively. Undeclared, duplicate, unsupported, or malformed values fail binding before execution. Default values must satisfy choices. No common PowerShell parameter redeclaration (`Verbose`, `Debug`, `ErrorAction`, `WarningAction`, `InformationAction`, `ProgressAction`, `ErrorVariable`, `WarningVariable`, `InformationVariable`, `OutVariable`, `OutBuffer`, `PipelineVariable`, `WhatIf`, or `Confirm`).

| Parameter type | Bound value |
| --- | --- |
| `string` | Literal string; no evaluation or interpolation. |
| `boolean` | Boolean value, not the text `"false"`. |
| `switch` | Explicit `SwitchParameter` true/false; omission is not confused with false. |
| `integer` | Signed 64-bit integral value, checked for overflow. |
| `number` | Finite double-precision number; no NaN/infinity. |
| `dateTime` | RFC 3339 timestamp with timezone, bound as `DateTimeOffset`. |
| `guid` | Canonical hyphenated GUID, bound as `Guid`. |
| `stringArray` | Array of literal strings; no delimiter splitting. |
| `secureString` / `credential` | Host-acquired secure value / `PSCredential`; no JSON defaults or choices. |

Defaults/choices describe inputs, not arbitrary validation scripts. The loader validates executable parameter metadata after trust, on the execution queue, for contributions whose platform/capability requirements are met; unavailable contributions remain disabled and are checked before any later use. Declarations must agree with command/script parameters and supported conversion. Scripts use an explicit `param(...)` block. The host binds by name, never regenerates executable text from the form or table. Required values without defaults prompt; optional omitted values are not bound, allowing the executable's own default. Cancelling a prompt cancels the invocation.

Each action declares `input`:

- `mode: none`: `selection` is exactly `{ "min": 0, "max": 0 }`, and `types` is empty. Existing highlighted rows are not implicit inputs.
- `mode: pipeline`: `types` is nonempty; `selection.min` is nonnegative and `selection.max` is a positive finite integer, with `min <= max`. Pass the frozen original selected objects once, in selection order, to one invocation. A zero minimum permits an empty pipeline.
- `mode: host`: reserved for the declared host handler, not a public object transport.

For pipeline input, scripts accept `ValueFromPipeline`; commands must have a compatible pipeline parameter. No JSON object values, selected-row stringification, property-to-parameter substitution, per-row implicit invocations, or foreign-session object transfer. All selected rows must independently match at least one declared type; mixed selection with even one incompatible row disables the action. Type matching is exact, case-insensitive against cached PowerShell extended type names plus CLR base/interface names, not a wildcard or arbitrary script. `System.Object` intentionally matches any object.

The invocation freezes the owning session, definition/content identity, selection handles, and bound parameters **before prompting or confirmation**. Revalidate handles, content/trust, requirements, and eligibility immediately before execution. Stale, released, duplicate, or foreign-session handles fail explicitly, never retarget by display ID. Live `PSObject`/CLR objects remain in their session; the desktop receives opaque references, cached scalar cells, and diagnostics.

## Actions, related views, and outcomes

Actions declare a stable `groupId` referring to `actionGroups`, label/description, executable kind, parameter/input contracts, `mutates`, `supportsShouldProcess`, and `result`. An optional nonempty `nodeIds` limits visibility to those nodes in the same kit; omission is type/capability-based global scope, including action-only kits. It does not override requirements or type/cardinality checks.

Command/script actions use the same declaration and typed execution path as queries. `kind: host` currently permits only `handler: object.properties`, `mode: host`, `types: ["System.Object"]`, exactly one selected object, no parameters, no mutation/ShouldProcess, and `result.policy: retain`. It invokes scheduled object inspection, not a kit callback. This is a **future contract requirement**, not a handler registration already shipped by the desktop.

`mutates: true` requires nonempty `confirmation.message`. The host adds session, selection count, and representative target identities; confirmation is not interpolation of executable code. A read-only declaration is not enforced sandboxing. A host may require confirmation even if the author marked an operation non-mutating. Host `WhatIf`/`Confirm` handling is separate from UI confirmation: `supportsShouldProcess: true` must agree with executable metadata before exposing preview. Without it the host must not promise a meaningful dry run.

| `result.policy` | Required behavior |
| --- | --- |
| `retain` | Keep the active result view; discard/release action output after recording diagnostics/outcome. |
| `refresh` | Requery the active view using its owning query and frozen, non-secret query bindings. Release action output and the replaced result. If the active view is not replayable (for example sensitive bindings or an action-produced view), disable with a reason rather than guessing a query. |
| `replace` | Require `result.viewId`; present action output using that view and release the replaced result. |
| `related` | Require `result.viewId`; present action output as a related view with a back route, not retained source objects. Back requeries a replayable source; otherwise make unavailable navigation explicit. |

Retain/refresh forbid `viewId`. The result policy is an explicit selection-independent presentation instruction, not inferred from output types or action name. Related views are ordinary view definitions reached through an action; they do not imply navigation-tree children.

Completed and `CompletedWithErrors` invocations apply the declared policy; partial output is labeled incomplete as appropriate. Failed invocations may show partial output with replace/related; they must not discard the failure. Refresh after an attempted action (including partial/failed outcomes) preserves the action's outcome even if requery succeeds, as in the built-in console. Cancellation must not initiate a new refresh; retain the previous view and release cancelled action output. A superseded navigation generation cannot replace/refresh a newer view. Existing built-in behavior is unchanged by this document.

Execution uses the same owning-session scheduler, host prompts, diagnostic streams, cooperative cancellation, retention, and handle-release rules for both examples and future loaded kits. No arbitrary eligibility scripts run on selection changes. History stores a redacted, possibly non-replayable description, not permanent object graphs or secret values. Credential/secure-input handling follows the host's stricter redaction and output-retention policy; result policy cannot override it.

## Resources, ownership, and trust

`resources` is the complete inventory of package files other than `kit.json`. Each entry declares `path`, `kind` (`script`, `icon`, `documentation`, or `license`), `owner`, and an SPDX `license` expression. Paths are unique under portable, case-insensitive normalization. Script/icon/license references must match both a declared entry and its kind. Script extensions are `.ps1`; icons are `.png`; documentation/license files are `.md` or `.txt`, or root `README`/`LICENSE`/`NOTICE`. V1 packages contain no DLLs, modules, executables, UI plugins, hidden payloads, or undeclared files.

Authors own or have redistribution rights to every resource. File license declarations may differ from the kit license and must preserve required notices. A publisher name, URL, or content hash is not a verified signature. Exports include only licensed content and deliberate non-secret definitions; never trust grants, object data, credentials, session variables, or machine-specific paths. Workspace aliases, disabled actions, and view preferences are separate from immutable publisher content.

```text
Selected -> Inspected -> Validated -> Installed disabled
                                      |
                         Explicit trust + compatibility
                                      |
                                    Enabled
                                      |
                         Disabled / upgraded / removed
```

Installation is data-only. Enabling authorizes arbitrary PowerShell/module execution with current process privileges; it is not a least-privilege permission grant or OS sandbox. No hooks, drive creation, query execution, or auto-elevation on installation/enablement. Queries run on explicit navigation/action requests after enablement.

Trust is keyed by kit ID, release version, and content identity, not just publisher name. Content identity is SHA-256 over all validated files, including `kit.json`: sort exact portable paths ordinally and hash concatenated records consisting of little-endian UInt32 UTF-8 path-byte length, path bytes, little-endian UInt64 content-byte length, and the raw 32-byte SHA-256 of that file. ZIP timestamps, entry order, compression, and directory entries do not affect identity. Paths themselves are not lowercased for hashing; case-colliding paths are rejected first.

Any changed bytes/path, same-version replacement, or upgraded version invalidates prior trust. Revalidate/re-hash a development folder immediately before execution; no content edits behind an existing trust grant. Installed/staged execution must protect against time-of-check/time-of-use replacement; development execution must use the validated snapshot, not reread an arbitrarily changed script. Reviewed application-owned content still exposes provenance/version.

## Package safety and limits

Reject absolute, drive-qualified, UNC, empty-segment, `.`/`..`, backslash, NUL/control-character, colon/alternate-stream, trailing-dot/space, and Windows-reserved-name paths on **all** platforms. Names use portable ASCII letters/digits/dots/underscores/hyphens; each segment begins and ends with a letter, digit, underscore, or hyphen. Reject case collisions, duplicate destinations, file/directory collisions, file ancestors, symbolic links, hard links, reparse points, and unsupported ZIP entry types. Resolve containment after normalization and again when opening/writing content. ZIP entry names use `/`; a single directory entry ending in `/` is allowed and contributes to entry count.

V1 accepts unencrypted single-volume ZIP with stored/deflated regular files; no executable extraction or archive chaining. Validate CRC, declared sizes, and actual expanded byte counts. Reject malformed/truncated archives and unsupported compression/encryption. Authoring folders reject links/reparse points too; they are not a safety bypass.

| Ceiling | V1 value |
| --- | --- |
| Archive entries / directory entries plus files in an authoring folder | 1,024 (including `kit.json` and explicit directory entries; excluding the authoring root) |
| Expanded bytes per file | 16 MiB (16,777,216 bytes) |
| Total expanded file bytes | 64 MiB (67,108,864 bytes) |
| Manifest file bytes | 1 MiB (1,048,576 bytes) |
| Expansion ratio | At most 100:1 per nonempty file and across all file payloads |

For ratios use expanded file bytes divided by `max(1, compressed payload bytes)`, not archive size padded with headers/comments/directory entries. Zero-byte files have ratio zero. Equality at each ceiling is accepted. Enforce actual counters while streaming, not just ZIP header claims, and stop before exceeding a ceiling. Authoring directories obey entry/expanded/manifest ceilings; a compression ratio is inapplicable.

These limits support small manifest/script/icon/documentation packages without modules/native binaries. The per-file cap bounds one allocation, total size bounds disk expansion, entry count bounds tiny-entry work, and ratio limits bound unusually compressed payloads. They are not a memory ceiling or malware detector. Trust cannot bypass limits; larger legitimate content requires a reviewed revision of this policy. An installer must also bound compressed input, CPU/time, and staging resources; none exists yet.

Install to staging under the per-user kit catalog, validate every file and reference, then atomically publish immutable content. Failed imports/updates preserve the previously installed release; failed enablement is visible and does not imply rollback of module/script side effects. Removing files or trust records never silently rewrites saved workspace references.

## Required validation and diagnostics

Validation has three distinct layers: JSON/schema shape, semantic/package checks, and trusted owning-session compatibility/executable checks. Do not run code to answer a schema/semantic validation question. Every diagnostic must include `code`, `message`, and an RFC 6901 JSON `pointer`; resource/archive diagnostics also identify `path`/`entry`. Use `""` for the document root. Report both locations for collisions and required/actual values for compatibility. Do not leak secret values into diagnostics.

| Code | Required detection |
| --- | --- |
| `KIT_JSON_INVALID` | Malformed JSON/UTF-8, duplicate member names, non-object root. |
| `KIT_SCHEMA_UNSUPPORTED` | Unsupported integer schema revision; malformed revision remains a shape failure. |
| `KIT_SCHEMA_INVALID` | Shape/type/enum/required-property failure, including unknown properties. |
| `KIT_VERSION_INVALID` / `KIT_RANGE_INVALID` | Invalid SemVer/numeric versions or unordered/equal bounds. |
| `KIT_ID_DUPLICATE` | Kit-wide definition ID collision or duplicate nested column ID, with both pointers. |
| `KIT_REFERENCE_UNRESOLVED` / `KIT_REFERENCE_KIND` | Missing or wrong-kind parent/query/view/group/node/resource reference. Cross-kit references are unsupported. |
| `KIT_HIERARCHY_CYCLE` | Parent cycle with the involved IDs/pointers; no silent node dropping. |
| `KIT_PARAMETER_INVALID` | Duplicate/reserved name, type/default/choice mismatch, undeclared binding, overflow, or trusted executable metadata mismatch. |
| `KIT_SELECTION_INVALID` | Unordered bounds, invalid input mode/type contract, stale/duplicate/foreign handles. |
| `KIT_RESOURCE_PATH_INVALID` / `KIT_RESOURCE_COLLISION` | Unsafe/nonportable path, normalized collision, link, or escaping resolution. |
| `KIT_RESOURCE_INVALID` / `KIT_LICENSE_INVALID` | Missing/undeclared/wrong-kind file, unsupported payload, invalid SPDX expression. |
| `KIT_ARCHIVE_INVALID` / `KIT_LIMIT_EXCEEDED` | Unsupported/corrupt archive or entry/file/manifest/total/ratio ceiling violation, with observed/allowed values. |
| `KIT_REQUIREMENT_INVALID` | Undeclared module/provider use or definition platform outside kit platforms. |
| `KIT_INCOMPATIBLE` | Unsupported host contract/engine/platform; include all relevant required/actual values. |
| `KIT_DEPENDENCY_UNAVAILABLE` / `KIT_DEPENDENCY_CONFLICT` | Missing/incompatible dependency, failed import, or loaded-version conflict. |
| `KIT_CAPABILITY_UNAVAILABLE` / `KIT_EXECUTABLE_INVALID` | Missing capability/handler, unresolved or wrong-module command, wrong script parameter/pipeline/ShouldProcess metadata. |
| `KIT_TRUST_REQUIRED` / `KIT_CONTENT_CHANGED` | Absent trust or changed validated content identity before execution. |

Example semantic diagnostic (the schema alone cannot produce it):

```json
{
  "code": "KIT_REFERENCE_UNRESOLVED",
  "pointer": "/nodes/1/queryId",
  "message": "Query 'processes.missing' does not exist in kit 'runspace.processes'."
}
```

Schema tools may emit native shape errors; a future loader must map them to these diagnostics consistently. JSON Schema `format` annotations are not necessarily asserted by a tool: PowerShell `Test-Json` does not reject every invalid calendar timestamp. The semantic/binding validator must check real RFC 3339 dates/timezones, absolute HTTPS URLs, SPDX expressions, exact numeric bounds, and all cross-field rules. These codes describe required behavior, **not an implemented validator API**.

## Original examples and verification boundary

- [Navigation and actions](../examples/console-kits/processes/kit.json): a process query, typed table, host inspection action, and benign scripted related summary.
- [Action only](../examples/console-kits/object-members/kit.json): `Get-Member` on selected original objects, with a replacement member table and no navigation/query definitions.

Both use the same command/script/input/result declarations. No example is enabled automatically or claims ownership of historical PowerGUI assets. Verify manifest shapes and negative shape cases from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\Test-KitContract.ps1
```

This script needs PowerShell 7.6's `Test-Json` and validates against the local schema; it does not import dependencies or execute sample scripts. Semantic integrity, executable metadata, resource/archive attacks and exact limits, mixed/stale selection, secret redaction, and both examples through the **same real owning-session execution seam** remain required integration gates for #8/follow-up work. A shape pass is not permission to load a kit.

## Non-goals

No desktop loader/manager, installation support, graphical authoring, automatic module installation, bundled modules, cross-kit dependency resolution, native DLL/UI plugins, dynamic provider-tree definitions, computed-column scripts, executable eligibility, remoting/foreign-session object transport, OS sandboxing, forced interruption, automatic migration, legacy `.powerpack` compatibility, or PowerPack parity in this first contract. The [reference research](research/powergui-3.8.md#source-ledger) informs concepts, not serialization or redistribution rights.
