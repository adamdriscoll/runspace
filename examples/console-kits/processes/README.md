# Process inspection Console Kit

Original MIT-licensed contract example, not an installable/shipped extension. See the repository's [Console Kit v1 contract](../../../docs/console-kits.md).

The future loader would query `Microsoft.PowerShell.Management\Get-Process`, display process objects, inspect one object through the host, or pass 1-128 original selected `Process` objects to `scripts/Summarize-Process.ps1` in the same session. The optional string `Prefix` is bound literally. The scripted action returns benign summaries to a related view; it does not modify or terminate processes.

`kit.json`, this file, `LICENSE`, and the declared script are the complete package content. The sample requires the external Management module, claims compatibility requirements rather than platform certification, and must never be enabled or executed merely by inspecting it. The desktop does not load it yet.
