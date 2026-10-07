[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string] $Runtime,
    [Parameter(Mandatory)]
    [string] $Version,
    [string] $Payload
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Expected a version such as 1.2.3 or 1.2.3-preview.1, not '$Version'."
}
$numericVersion = ($Version -split '-')[0]
$parts = $numericVersion.Split('.')
if ([decimal]$parts[0] -gt 255 -or [decimal]$parts[1] -gt 255 -or [decimal]$parts[2] -gt 65535) {
    throw 'Package versions must fit MSI limits: major/minor <= 255 and patch <= 65535.'
}
if (($Runtime.StartsWith('win-') -and -not $IsWindows) -or
    ($Runtime.StartsWith('osx-') -and -not $IsMacOS) -or
    ($Runtime.StartsWith('linux-') -and -not $IsLinux)) {
    throw "Package $Runtime on its corresponding operating system."
}

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts' 'publish' $Runtime $Version
$output = Join-Path $root 'artifacts' 'packages' $Runtime $Version
$staging = Join-Path $root 'artifacts' 'staging' $Runtime $Version
foreach ($path in @($publish, $output, $staging)) {
    if (Test-Path -LiteralPath $path) { throw "Packaging output already exists: $path. Remove it before rebuilding." }
}
$payloadPath = if ($Payload) { (Resolve-Path -LiteralPath $Payload).Path }
foreach ($path in @($publish, $output, $staging)) {
    [IO.Directory]::CreateDirectory($path) | Out-Null
}
if ($payloadPath) {
    Get-ChildItem -LiteralPath $payloadPath -Force | Copy-Item -Destination $publish -Recurse -Force
}
else {
    dotnet publish (Join-Path $root 'src' 'Runspace.Desktop' 'Runspace.Desktop.csproj') `
        --configuration Release --runtime $Runtime --self-contained true `
        "-p:Version=$Version" -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true `
        -p:PublishTrimmed=false -p:PublishAot=false -p:PublishSingleFile=false --output $publish --verbosity minimal
}
$executable = if ($IsWindows) { 'Runspace.Desktop.exe' } else { 'Runspace.Desktop' }
foreach ($required in @($executable, 'Runspace.Desktop.dll', 'Runspace.Desktop.deps.json',
    'Runspace.Desktop.runtimeconfig.json', 'System.Management.Automation.dll', 'runtimes', 'ref')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) {
        throw "Incomplete self-contained payload: missing $required."
    }
}
$dependencies = Get-Content -LiteralPath (Join-Path $publish 'Runspace.Desktop.deps.json') -Raw | ConvertFrom-Json
if (-not $dependencies.runtimeTarget.name.EndsWith("/$Runtime", [StringComparison]::Ordinal)) {
    throw "Payload runtime does not match $Runtime."
}
$configuration = Get-Content -LiteralPath (Join-Path $publish 'Runspace.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($configuration.runtimeOptions.includedFrameworks.name -notcontains 'Microsoft.NETCore.App') {
    throw 'Payload must include the self-contained .NET runtime.'
}
$applicationVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'Runspace.Desktop.dll')).ProductVersion
if ($applicationVersion -ne $Version -and -not $applicationVersion.StartsWith("$Version+", [StringComparison]::Ordinal)) {
    throw "Payload application version '$applicationVersion' does not match $Version."
}
$name = "Runspace-$Version-$Runtime"

if ($IsWindows) {
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, (Join-Path $output "$name.zip"))
    Push-Location $root
    try {
        dotnet tool restore
        dotnet tool run wix -- build (Join-Path $PSScriptRoot 'Windows.wxs') -arch x64 `
            -d "PublishDir=$publish" -d "Version=$numericVersion" -o (Join-Path $output "$name.msi")
        dotnet tool run wix -- msi validate (Join-Path $output "$name.msi")
    }
    finally { Pop-Location }
}
elseif ($IsMacOS) {
    $contents = Join-Path $staging 'Runspace.app' 'Contents'
    $macos = Join-Path $contents 'MacOS'
    [IO.Directory]::CreateDirectory($macos) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $contents 'Resources')) | Out-Null
    Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $macos -Recurse -Force
    $plist = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Info.plist') -Raw).
        Replace('@VERSION@', $Version).Replace('@NUMERIC_VERSION@', $numericVersion)
    Set-Content -LiteralPath (Join-Path $contents 'Info.plist') -Value $plist -Encoding utf8NoBOM
    chmod +x (Join-Path $macos $executable)
    plutil -lint (Join-Path $contents 'Info.plist')
    New-Item -ItemType SymbolicLink -Path (Join-Path $staging 'Applications') -Target '/Applications' | Out-Null
    hdiutil create -volname Runspace -srcfolder $staging -format UDZO (Join-Path $output "$name.dmg")
    hdiutil verify (Join-Path $output "$name.dmg")
    Push-Location $staging
    try { zip -q -r -y (Join-Path $output "$name.zip") 'Runspace.app' }
    finally { Pop-Location }
}
else {
    chmod +x (Join-Path $publish $executable)
    Push-Location $publish
    try { zip -q -r -y (Join-Path $output "$name.zip") '.' }
    finally { Pop-Location }
}
Write-Host "Unsigned packages: $output"
