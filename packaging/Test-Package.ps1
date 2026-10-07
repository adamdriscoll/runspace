[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string] $Runtime,
    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts' 'publish' $Runtime $Version
$output = Join-Path $root 'artifacts' 'packages' $Runtime $Version
$name = "Runspace-$Version-$Runtime"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('runspace-package-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null

function Assert-Payload([string] $Actual) {
    $expectedFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Force)
    $actualFiles = @(Get-ChildItem -LiteralPath $Actual -Recurse -File -Force)
    if ($expectedFiles.Count -eq 0 -or $actualFiles.Count -ne $expectedFiles.Count) {
        throw "Package file count differs from the published payload: $Actual."
    }
    foreach ($file in $expectedFiles) {
        $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
        $packaged = Join-Path $Actual $relative
        if (-not (Test-Path -LiteralPath $packaged -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $packaged).Hash) {
            throw "Package file missing or changed: $relative."
        }
    }
    if (-not $IsWindows) {
        test -x (Join-Path $Actual 'Runspace.Desktop')
    }
    Write-Host "Verified complete payload: $Actual"
}

try {
    $zip = Join-Path $output "$name.zip"
    $extracted = Join-Path $scratch 'zip'
    if ($IsWindows) {
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
    }
    else {
        unzip -q $zip -d $extracted
    }
    $zipPayload = if ($IsMacOS) { Join-Path $extracted 'Runspace.app' 'Contents' 'MacOS' } else { $extracted }
    Assert-Payload $zipPayload

    if ($IsWindows) {
        $msi = Join-Path $output "$name.msi"
        $adminImage = Join-Path $scratch 'msi'
        $log = Join-Path $scratch 'msi.log'
        $process = Start-Process msiexec -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$adminImage`" /l*v `"$log`"" -PassThru -Wait
        try {
            if ($process.ExitCode -ne 0) {
                Get-Content -LiteralPath $log | Write-Host
                throw "MSI administrative extraction failed with exit $($process.ExitCode)."
            }
        }
        finally { $process.Dispose() }
        $executables = @(Get-ChildItem -LiteralPath $adminImage -Filter Runspace.Desktop.exe -Recurse -File)
        if ($executables.Count -ne 1) { throw 'MSI must contain exactly one Runspace.Desktop.exe.' }
        Assert-Payload $executables[0].DirectoryName
    }
    elseif ($IsMacOS) {
        $mount = Join-Path $scratch 'dmg'
        hdiutil attach (Join-Path $output "$name.dmg") -readonly -nobrowse -mountpoint $mount
        try {
            Assert-Payload (Join-Path $mount 'Runspace.app' 'Contents' 'MacOS')
            $zipPlist = Join-Path $extracted 'Runspace.app' 'Contents' 'Info.plist'
            $dmgPlist = Join-Path $mount 'Runspace.app' 'Contents' 'Info.plist'
            plutil -lint $zipPlist $dmgPlist
            if ((Get-FileHash -LiteralPath $zipPlist).Hash -ne (Get-FileHash -LiteralPath $dmgPlist).Hash) {
                throw 'ZIP and DMG bundle metadata differs.'
            }
            if ((Get-Item -LiteralPath (Join-Path $mount 'Applications')).Target -ne '/Applications') {
                throw 'DMG must include the Applications shortcut.'
            }
        }
        finally { hdiutil detach $mount }
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
