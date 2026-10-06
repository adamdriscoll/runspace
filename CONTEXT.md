# Runspace administration console

Runspace is an object-oriented administration console: administrators navigate resources, inspect PowerShell objects, and invoke contextual operations. This glossary uses **Console Kit** as the working replacement name for PowerGUI's PowerPack; branding is provisional.

## Language

**Console Kit**:
A distributable collection of navigation definitions, queries, views, and contextual actions for an administration domain. A Console Kit is not a PowerShell module, although it can require modules.
_Avoid_: PowerPack, plugin, module when referring to this collection.

**Workspace**:
An administrator's saved arrangement of enabled Console Kits, session references, navigation state, and view preferences.
_Avoid_: Console Kit, session when referring to the saved arrangement.

**Session**:
A connected PowerShell environment in which queries and actions operate. Its available drives, modules, variables, and resources belong to that environment, not to the workspace as a whole.
_Avoid_: Workspace, connection when the execution environment is meant.

**Provider Drive**:
A named resource root exposed by a PowerShell provider in a particular session. It can represent files, environment variables, registry keys, or another provider-defined resource; it is not necessarily a disk.
_Avoid_: Disk, volume when all provider drives are meant.

**Navigation Node**:
A selectable location in the console's resource hierarchy. It can organize other nodes, identify a provider location, or expose a query.
_Avoid_: Drive when the location is not a provider drive.

**Query**:
A named operation that returns objects for inspection in a result view.
_Avoid_: Action when describing object retrieval.

**Result Set**:
The objects produced by one query execution, together with the information needed to interpret their origin and completeness.
_Avoid_: Table, text output when referring to the underlying objects.

**Result View**:
A presentation of a result set, including its visible columns, sorting, filtering, and current selection. Different views can present the same result set.
_Avoid_: Result Set when only its presentation is meant.

**Selection**:
The explicit subset of a result set chosen as the input to an operation.
_Avoid_: Visible rows, all results unless those objects were explicitly selected.

**Action**:
A named operation offered for a navigation context or selected objects. An action declares its input expectations and outcome; it need not modify anything.
_Avoid_: Query when the operation is contextual rather than the source of a result view.

**Action Group**:
A named grouping of related actions in the contextual action pane.
_Avoid_: Menu when the grouping is not a menu.

**Related View**:
A result view opened by following a relationship from selected objects, while retaining the route back to the originating view.
_Avoid_: Child node when navigation-tree membership is not implied.

**Invocation**:
One execution of a query or action with a fixed session, input selection, and parameter values.
_Avoid_: History entry when referring to execution itself.

**History Entry**:
A record of an invocation and its PowerShell representation, outcome, and timing. It is distinct from diagnostic messages.
_Avoid_: Log when referring to reproducible command history.

**Diagnostics**:
Messages and progress explaining the behavior of the console or an invocation, including failures and PowerShell stream records.
_Avoid_: History when only diagnostic output is meant.
