[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Results,
    [switch] $SkipNative
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($Results)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$metadata = [ordered]@{
    TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
    OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    LogicalProcessors = [Environment]::ProcessorCount
    SDK = (& dotnet --version)
    Commit = (& git -C $root rev-parse HEAD)
    WorkingTreeDirty = [bool](& git -C $root status --porcelain)
}
if ($IsWindows) {
    $cpu = Get-CimInstance Win32_Processor
    $system = Get-CimInstance Win32_ComputerSystem
    $metadata.CPU = ($cpu.Name -join '; ')
    $metadata.PhysicalMemoryBytes = $system.TotalPhysicalMemory
    if ($system.TotalPhysicalMemory -lt 16GB -or [Environment]::ProcessorCount -lt 4) {
        throw 'The reference gate requires at least four logical CPUs and 16 GiB RAM.'
    }
} else {
    $metadata.HardwareNote = 'Record CPU model and physical RAM with the report; reference gate requires >=4 logical CPUs and >=16 GiB RAM.'
}
$metadata | ConvertTo-Json | Set-Content (Join-Path $output 'environment.json') -Encoding utf8

Push-Location $root
$previous = $env:RUNSPACE_BENCHMARK_DIR
try {
    & dotnet build Runspace.slnx --configuration Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $env:RUNSPACE_BENCHMARK_DIR = $output
    # Run separately so memory samples do not overlap another test process.
    & dotnet test .\tests\Runspace.Tests\Runspace.Tests.csproj --configuration Release --no-build `
        --filter FullyQualifiedName~RetentionTests --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Runtime measurements or retention regression failed.' }
    & dotnet test .\tests\Runspace.Desktop.Tests\Runspace.Desktop.Tests.csproj --configuration Release --no-build `
        --filter FullyQualifiedName~CachedTwentyThousandRows --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cached desktop responsiveness gate failed.' }
    if (!$SkipNative) {
        & dotnet .\src\Runspace.Desktop\bin\Release\net10.0\Runspace.Desktop.dll `
            --benchmark-results (Join-Path $output 'native-desktop.json')
        if ($LASTEXITCODE -ne 0) { throw 'Native desktop benchmark failed; inspect native-desktop.json.' }
    }
} finally {
    $env:RUNSPACE_BENCHMARK_DIR = $previous
    Pop-Location
}
