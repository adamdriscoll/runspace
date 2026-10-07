# Object members Console Kit

Original MIT-licensed action-only contract example, not an installable/shipped extension. See the repository's [Console Kit v1 contract](../../../docs/console-kits.md).

The future loader would offer this global action for 1-128 selected objects from any result set. It passes those original objects once through the owning session's pipeline to `Microsoft.PowerShell.Utility\Get-Member` and replaces the result view with the returned member descriptions. No node, query, cross-kit reference, executable eligibility rule, or separate execution adapter is needed.

`kit.json`, this file, and `LICENSE` are the complete package content. The external Utility module is required. Module availability and compatibility are checked after trust before execution; inspection alone never imports or runs the kit. The desktop does not load it yet.
