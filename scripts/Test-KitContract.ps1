#requires -Version 7.6
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$schema = Join-Path $root 'schemas\console-kit-v1.schema.json'
$examplePaths = @(
    'examples\console-kits\processes\kit.json'
    'examples\console-kits\object-members\kit.json'
)

function Assert-Shape {
    param([string] $Name, [string] $Json, [bool] $Expected)

    $shapeErrors = @()
    $actual = Test-Json -Json $Json -SchemaFile $schema -ErrorAction SilentlyContinue -ErrorVariable shapeErrors
    if ($actual -ne $Expected) {
        $details = ($shapeErrors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "Shape case '$Name': expected $Expected, got $actual. $details"
    }
}

foreach ($relativePath in $examplePaths) {
    Assert-Shape $relativePath (Get-Content (Join-Path $root $relativePath) -Raw) $true
}

$baseline = Get-Content (Join-Path $root $examplePaths[0]) -Raw
$cases = @(
    @{ Name = 'unsupported schema'; Change = { param($kit) $kit.schemaVersion = 2 } }
    @{ Name = 'string schema'; Change = { param($kit) $kit.schemaVersion = '1' } }
    @{ Name = 'unknown root field'; Change = { param($kit) $kit.installHook = 'Start-Process anything' } }
    @{ Name = 'missing publisher'; Change = { param($kit) $kit.Remove('publisher') } }
    @{ Name = 'insecure publisher URL'; Change = { param($kit) $kit.publisher.url = 'http://example.com' } }
    @{ Name = 'uppercase kit ID'; Change = { param($kit) $kit.id = 'Runspace.processes' } }
    @{ Name = 'short kit ID'; Change = { param($kit) $kit.id = 'processes' } }
    @{ Name = 'leading-zero kit version'; Change = { param($kit) $kit.version = '01.0.0' } }
    @{ Name = 'newline kit version'; Change = { param($kit) $kit.version = "1.0.0`n" } }
    @{ Name = 'leading-zero prerelease'; Change = { param($kit) $kit.version = '1.0.0-01' } }
    @{ Name = 'string compatibility range'; Change = { param($kit) $kit.compatibility.runspace = '>=1 <2' } }
    @{ Name = 'open-ended compatibility'; Change = { param($kit) $kit.compatibility.runspace.Remove('maxExclusive') } }
    @{ Name = 'prerelease compatibility bound'; Change = { param($kit) $kit.compatibility.powerShell.minInclusive = '7.6.0-preview.1' } }
    @{ Name = 'newline compatibility bound'; Change = { param($kit) $kit.compatibility.powerShell.minInclusive = "7.6.0`n" } }
    @{ Name = 'unknown platform'; Change = { param($kit) $kit.compatibility.platforms = @('freebsd') } }
    @{ Name = 'duplicate platform'; Change = { param($kit) $kit.compatibility.platforms = @('windows', 'windows') } }
    @{ Name = 'five-component module version'; Change = { param($kit) $kit.dependencies.modules[0].version.minInclusive = '7.0.0.0.0' } }
    @{ Name = 'empty contributions'; Change = { param($kit) $kit.nodes = @(); $kit.actions = @() } }
    @{ Name = 'folder query reference'; Change = { param($kit) $kit.nodes[0].queryId = 'processes.list' } }
    @{ Name = 'query node without query'; Change = { param($kit) $kit.nodes[1].Remove('queryId') } }
    @{ Name = 'unknown executable kind'; Change = { param($kit) $kit.queries[0].kind = 'native' } }
    @{ Name = 'unqualified command'; Change = { param($kit) $kit.queries[0].command = 'Get-Process' } }
    @{ Name = 'dual query executable'; Change = { param($kit) $kit.queries[0].script = 'scripts/extra.ps1' } }
    @{ Name = 'inline script'; Change = { param($kit) $kit.actions[1].script = 'Get-Process | Stop-Process' } }
    @{ Name = 'traversal resource'; Change = { param($kit) $kit.resources[0].path = '../outside.ps1' } }
    @{ Name = 'absolute resource'; Change = { param($kit) $kit.resources[0].path = '/tmp/outside.ps1' } }
    @{ Name = 'backslash resource'; Change = { param($kit) $kit.resources[0].path = 'scripts\file.ps1' } }
    @{ Name = 'drive resource'; Change = { param($kit) $kit.resources[0].path = 'C:/file.ps1' } }
    @{ Name = 'empty path segment'; Change = { param($kit) $kit.resources[0].path = 'scripts//file.ps1' } }
    @{ Name = 'native payload'; Change = { param($kit) $kit.resources[0].path = 'scripts/library.dll' } }
    @{ Name = 'SVG icon'; Change = { param($kit) $kit.nodes[0].icon = 'icons/tree.svg' } }
    @{ Name = 'ambiguous kit license'; Change = { param($kit) $kit.license.spdx = 'MIT' } }
    @{ Name = 'empty columns'; Change = { param($kit) $kit.views[0].columns = @() } }
    @{ Name = 'executable property expression'; Change = { param($kit) $kit.views[0].columns[0].property = '$_.ProcessName' } }
    @{ Name = 'bytes string column'; Change = { param($kit) $kit.views[0].columns[0].format = 'bytes' } }
    @{ Name = 'unknown column field'; Change = { param($kit) $kit.views[0].columns[0].expression = 'Get-Process' } }
    @{ Name = 'wrong typed default'; Change = { param($kit) $kit.actions[1].parameters[0].type = 'integer' } }
    @{ Name = 'integer overflow'; Change = { param($kit) $p = $kit.actions[1].parameters[0]; $p.type = 'integer'; $p.default = [decimal]::Parse('9223372036854775808') } }
    @{ Name = 'integer underflow'; Change = { param($kit) $p = $kit.actions[1].parameters[0]; $p.type = 'integer'; $p.default = [decimal]::Parse('-9223372036854775809') } }
    @{ Name = 'fractional integer'; Change = { param($kit) $p = $kit.actions[1].parameters[0]; $p.type = 'integer'; $p.default = 1.5 } }
    @{ Name = 'timestamp without timezone'; Change = { param($kit) $p = $kit.actions[1].parameters[0]; $p.type = 'dateTime'; $p.default = '2026-10-07T10:00:00' } }
    @{ Name = 'invalid parameter name'; Change = { param($kit) $kit.actions[1].parameters[0].name = 'Bad-Name' } }
    @{ Name = 'empty choices'; Change = { param($kit) $kit.actions[1].parameters[0].choices = @() } }
    @{ Name = 'duplicate choices'; Change = { param($kit) $kit.actions[1].parameters[0].choices = @('A', 'A') } }
    @{ Name = 'secret default'; Change = { param($kit) $kit.actions[1].parameters[0].type = 'secureString' } }
    @{ Name = 'credential choices'; Change = { param($kit) $p = $kit.actions[1].parameters[0]; $p.type = 'credential'; $p.Remove('default'); $p.choices = @('secret') } }
    @{ Name = 'unknown input mode'; Change = { param($kit) $kit.actions[1].input.mode = 'parameter' } }
    @{ Name = 'unbounded selection'; Change = { param($kit) $kit.actions[1].input.selection.max = $null } }
    @{ Name = 'pipeline without types'; Change = { param($kit) $kit.actions[1].input.types = @() } }
    @{ Name = 'nonempty none input'; Change = { param($kit) $kit.actions[1].input.mode = 'none' } }
    @{ Name = 'script host input'; Change = { param($kit) $kit.actions[1].input.mode = 'host' } }
    @{ Name = 'unknown host handler'; Change = { param($kit) $kit.actions[0].handler = 'custom.ui' } }
    @{ Name = 'host multi-selection'; Change = { param($kit) $kit.actions[0].input.selection.max = 2 } }
    @{ Name = 'host replace result'; Change = { param($kit) $kit.actions[0].result = @{ policy = 'replace'; viewId = 'processes.table' } } }
    @{ Name = 'related without view'; Change = { param($kit) $kit.actions[1].result.Remove('viewId') } }
    @{ Name = 'retain with view'; Change = { param($kit) $kit.actions[1].result.policy = 'retain' } }
    @{ Name = 'mutation without confirmation'; Change = { param($kit) $kit.actions[1].mutates = $true } }
    @{ Name = 'empty confirmation'; Change = { param($kit) $kit.actions[1].mutates = $true; $kit.actions[1].confirmation = @{ message = ' ' } } }
    @{ Name = 'eligibility script'; Change = { param($kit) $kit.actions[1].eligibility = 'Test-Anything' } }
    @{ Name = 'unknown capability'; Change = { param($kit) $kit.actions[0].requirements.capabilities = @('custom.execute') } }
)

foreach ($case in $cases) {
    $kit = ConvertFrom-Json $baseline -AsHashtable
    $null = & $case.Change $kit
    Assert-Shape $case.Name ($kit | ConvertTo-Json -Depth 64) $false
}

$typedCases = @(
    @{ Type = 'boolean'; Value = $false }
    @{ Type = 'switch'; Value = $true }
    @{ Type = 'integer'; Value = [long]::MaxValue }
    @{ Type = 'number'; Value = 1.25 }
    @{ Type = 'dateTime'; Value = '2026-10-07T10:00:00-05:00' }
    @{ Type = 'guid'; Value = 'c9657d30-bce5-4b40-a5ee-2bb3092fc87a' }
    @{ Type = 'stringArray'; Value = @('literal', 'quotes; $(no execution)') }
)

foreach ($case in $typedCases) {
    $kit = ConvertFrom-Json $baseline -AsHashtable
    $kit.actions[1].parameters[0].type = $case.Type
    $kit.actions[1].parameters[0].default = $case.Value
    Assert-Shape "typed default: $($case.Type)" ($kit | ConvertTo-Json -Depth 64) $true
}

$kit = ConvertFrom-Json $baseline -AsHashtable
$kit.version = '1.0.0-beta.1+build.07'
Assert-Shape 'SemVer prerelease and build' ($kit | ConvertTo-Json -Depth 64) $true

Write-Output "Console Kit v1 schema: $($examplePaths.Count) examples, $($cases.Count) negative cases, and $($typedCases.Count + 1) positive variants passed. Semantic/package/runtime validation is not implemented."
