# Console Kits

Status: proposed extension contract, not an implemented feature. **Console Kit** and the `.rskit` extension are working names; neither is an implemented compatibility promise. Implementation and decision work are tracked in [GitHub issues](https://github.com/adamdriscoll/runspace/issues/24).

## Purpose and relationship to PowerGUI

A Console Kit packages an administration experience: resource navigation, PowerShell queries, result views, related views, and contextual actions. It can contribute navigation, actions, or both.

The reference packs contain XML-backed metadata, navigation, scripts, actions, type definitions, view properties, icons, and chart presets. The pack manager exposes import/export/uninstall/properties/update operations. [Reference: PG-14 through PG-17](research/powergui-3.8.md#source-ledger)

Retain that extensibility concept, not the legacy XML serialization or bundled scripts. Console Kits are **not** PowerShell modules; their dependencies are ordinary PowerShell modules.

## Proposed package

Use an authoring directory and a ZIP-based distributable with the same contents:

```text
runspace.local-system\
  kit.json
  scripts\
  icons\
  README.md
  LICENSE
```

`kit.json` is UTF-8 JSON with a versioned schema. Define a schema when the loader is implemented; until then the following example is illustrative, not an accepted file format.

Scripts and icons are optional. Native DLL/UI plugins are out of scope for the initial format. A development-folder workflow should not require rebuilding the desktop application to edit original kit content.

## Definition concepts

| Definition | Required meaning |
| --- | --- |
| Identity | Stable kit ID, human-readable name, semantic version, publisher, description, license |
| Compatibility | Manifest schema, application contract range, PowerShell range, target OS/capabilities |
| Dependencies | Module names/version constraints and required providers; checking does not install them |
| Nodes | Stable IDs, names, kind, parent/order, icon, query/view reference, optional children |
| Queries | Command/script declaration, parameter contract, output type hints, view reference |
| Views | Column property paths/labels/types, ordering, default sort, optional identity hints |
| Actions | Stable ID/group, compatible types, selection range, parameters, confirmation and output policy |
| Resources | Original scripts/icons and relative paths restricted to package content |

Use stable identifiers for workspace references and overrides. Never identify an action by its display label or result row number.

Parameters are passed as typed values. Script queries/actions use declared parameters and pipeline input, not template substitution of raw text.

### Original illustrative manifest

This example intentionally contains only a benign process query and a built-in inspection action. `object.properties` is a proposed host-provided handler, not a currently available plugin entry point.

```json
{
  "schemaVersion": 1,
  "id": "runspace.local-system",
  "name": "Local System",
  "version": "0.1.0",
  "publisher": "Runspace",
  "description": "Original local object-inspection views.",
  "compatibility": {
    "powerShell": ">=7.6 <8.0",
    "platforms": ["windows", "macos", "linux"]
  },
  "dependencies": {
    "modules": [
      { "name": "Microsoft.PowerShell.Management" }
    ]
  },
  "nodes": [
    { "id": "local", "name": "Local System", "kind": "folder" },
    {
      "id": "processes",
      "parentId": "local",
      "name": "Processes",
      "kind": "query",
      "queryId": "processes.list"
    }
  ],
  "queries": [
    {
      "id": "processes.list",
      "kind": "command",
      "command": "Get-Process",
      "parameters": {},
      "viewId": "processes.table"
    }
  ],
  "views": [
    {
      "id": "processes.table",
      "columns": [
        { "property": "ProcessName", "label": "Name", "type": "string" },
        { "property": "Id", "label": "Id", "type": "integer" },
        { "property": "WorkingSet64", "label": "Memory", "type": "integer", "format": "bytes" }
      ]
    }
  ],
  "actions": [
    {
      "id": "processes.properties",
      "name": "Properties",
      "group": "General",
      "inputTypes": ["System.Diagnostics.Process"],
      "selection": { "min": 1, "max": 1 },
      "kind": "builtIn",
      "handler": "object.properties",
      "result": "retain"
    }
  ]
}
```

Compatibility here describes the proposed runtime family, not proof that every future 7.x/8.x version works. The loader should compare runtime and kit contracts explicitly; exact supported versions come from the release matrix.

## Action contract

Actions specify whether they consume no objects, exactly one, or multiple selected objects. Multi-type selections must satisfy the whole declared contract; one eligible row cannot authorize an action on the entire selection.

Supported result policies:

- **Retain:** leave the current result view unchanged.
- **Refresh:** rerun its owning query after the action finishes.
- **Replace:** show the action's returned objects as the active result view.
- **Related:** open a related result view retaining its originating route.

A refresh after partial failure must not erase the error status. Results and non-terminating errors can coexist.

Declare mutating actions and confirmation text. The console adds session, selection count, and representative target identities. Commands supporting `ShouldProcess` can expose meaningful `WhatIf`; others must not pretend to support preview.

Display the PowerShell representation before/after an invocation, but execute the bound invocation itself. Credentials and secure parameters are redacted. Arbitrary object selection may make history session-dependent and non-replayable.

Kit-defined computed columns are executed work and subject to the execution scheduler, trust, and visible getter errors. No arbitrary scripts run during action-eligibility calculation.

## Installation and trust lifecycle

```text
Selected package -> Inspected -> Validated -> Installed disabled
                                            |
                                  Explicit trust + compatibility
                                            |
                                         Enabled
                                            |
                              Disabled / upgraded / removed
```

Importing/installing does not activate scripts, import dependencies, run hooks, or automatically create drives.

Before enabling, show publisher, version, content identity, permissions warning, modules/providers, and platform compatibility. Trust authorizes arbitrary PowerShell execution with the current process privileges; it is not an enforced least-privilege grant.

Treat scripts declaring themselves read-only as untrusted code until explicitly trusted. Execution policy, constrained runspaces, or manifest flags alone must not be advertised as a complete sandbox.

A changed folder/package or upgraded version invalidates the content-hash-based trust record. Original built-in kits can ship as reviewed application content; their provenance and version still need to be visible.

Dependency checks report missing/incompatible modules. Do not silently install modules, bypass execution policy, auto-elevate, or import an arbitrary profile.

## Validation and package safety

Validate schema/version, IDs, references, semantic version ranges, parameter declarations, supported kinds, and node hierarchy cycles. Reject duplicate IDs and invalid cross-kit references with precise messages.

For ZIP extraction, reject absolute/drive-qualified paths, traversal, escaping symbolic links, duplicate normalized destinations, and platform-incompatible names. Resolve every resource inside the install directory. Account for case collisions on case-insensitive filesystems.

Bound archive entry count, per-entry size, total expanded size, and expansion ratio before shipping; concrete limits are an implementation decision gate, not yet a settled product restriction.

Use staging and atomic installation so a failed import cannot corrupt an enabled kit. Preserve a previous version on a failed upgrade and expose the failure.

Separate immutable installed content from workspace overrides. Column choices, node aliases, and disabled actions should not rewrite publisher content. Removing a kit leaves its saved references visibly unresolved or offers explicit cleanup; it must not silently rewrite the workspace.

Exports omit secrets, trust grants, object data, machine-specific paths, and live session state. Include only owned/licensed kit content and deliberate non-secret definitions.

## Authoring scope

Development folders and graphical authoring should use the same schema, validation, trust, and ownership rules as distributable packages. Feature sequencing and completion status belong in the issue tracker.

Keep the classic administrator-friendly experience: a node's properties explain its query, output columns, related views, and actions. Editing a query reveals PowerShell rather than imposing a proprietary visual workflow language.

Any legacy `.powerpack` import must preview unsupported features, never execute imported scripts automatically, and require rights to the imported content. Do not ship the supplied historical packs as Runspace kits.
