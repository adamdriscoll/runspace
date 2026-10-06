[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Payload,
    [Parameter(Mandatory)]
    [string] $Results
)

$ErrorActionPreference = 'Stop'
$payloadPath = (Resolve-Path -LiteralPath $Payload).Path
$resultsPath = [IO.Path]::GetFullPath($Results)
[IO.Directory]::CreateDirectory($resultsPath) | Out-Null
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('runspace-publish-' + [guid]::NewGuid().ToString('N'))
$copy = Join-Path $scratch 'payload'
$homePath = Join-Path $scratch 'home'
[IO.Directory]::CreateDirectory($homePath) | Out-Null
$emptyPath = Join-Path $scratch 'empty-path'
[IO.Directory]::CreateDirectory($emptyPath) | Out-Null
$profilePath = if ($IsWindows) { [IO.Path]::Combine($homePath, 'Documents', 'PowerShell') } else { [IO.Path]::Combine($homePath, '.config', 'powershell') }
[IO.Directory]::CreateDirectory($profilePath) | Out-Null
[IO.File]::WriteAllText((Join-Path $profilePath 'profile.ps1'), '$global:RunspaceProfileExecuted = $true; throw "Automatic profiles must not run."')
Copy-Item -LiteralPath $payloadPath -Destination $copy -Recurse
$executable = Join-Path $copy $(if ($IsWindows) { 'Runspace.Desktop.exe' } else { 'Runspace.Desktop' })
if (-not $IsWindows) { & chmod +x $executable; if ($LASTEXITCODE -ne 0) { throw 'chmod failed.' } }

function Invoke-Probe([string] $Name, [bool] $ExpectSuccess, [string] $ExpectedError = '') {
    $report = Join-Path $resultsPath ($Name + '.json')
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $homePath
    $start.ArgumentList.Add('--validate-publish')
    $start.ArgumentList.Add($report)
    $start.Environment['PATH'] = $emptyPath
    $start.Environment['PSModulePath'] = $emptyPath
    $start.Environment['HOME'] = $homePath
    $start.Environment['USERPROFILE'] = $homePath
    $start.Environment['DOTNET_ROOT'] = $emptyPath
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment.Remove('DOTNET_STARTUP_HOOKS') | Out-Null
    $start.Environment.Remove('DOTNET_ADDITIONAL_DEPS') | Out-Null
    $start.Environment.Remove('DOTNET_SHARED_STORE') | Out-Null
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(90000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Name exceeded 90 seconds."
        }
        if (-not (Test-Path -LiteralPath $report)) { throw "$Name did not write its validation report (exit $($process.ExitCode))." }
        $evidence = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($ExpectSuccess) {
            if ($process.ExitCode -ne 0 -or -not $evidence.Passed) { throw "$Name failed: $(Get-Content -LiteralPath $report -Raw)" }
        }
        elseif ($process.ExitCode -eq 0 -or $evidence.Passed -or
            -not ($evidence.Checks | Where-Object { -not $_.Passed -and $_.Detail.Contains($ExpectedError) })) {
            throw "$Name did not report the expected actionable failure: $ExpectedError"
        }
        Write-Host "$Name passed (exit $($process.ExitCode)); report: $report"
    }
    finally { $process.Dispose() }
}

try {
    Invoke-Probe 'published' $true
    $platform = if ($IsWindows) { 'win' } else { 'unix' }
    $manifest = [IO.Path]::Combine($copy, 'runtimes', $platform, 'lib', 'net10.0', 'Modules',
        'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Management.psd1')
    $dependencies = @($manifest, (Join-Path $copy 'Microsoft.PowerShell.Commands.Utility.dll'))
    foreach ($dependency in $dependencies) {
        Move-Item -LiteralPath $dependency -Destination ($dependency + '.removed')
        try {
            $name = if ($dependency -eq $manifest) { 'missing-manifest' } else { 'missing-assembly' }
            Invoke-Probe $name $false ([IO.Path]::GetFileName($dependency))
        }
        finally { Move-Item -LiteralPath ($dependency + '.removed') -Destination $dependency }
    }
    $nativeName = if ($IsWindows) { 'libSkiaSharp.dll' } elseif ($IsMacOS) { 'libSkiaSharp.dylib' } else { 'libSkiaSharp.so' }
    $native = Join-Path $copy $nativeName
    Move-Item -LiteralPath $native -Destination ($native + '.removed')
    try { Invoke-Probe 'missing-native' $false 'SkiaSharp' }
    finally { Move-Item -LiteralPath ($native + '.removed') -Destination $native }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
