[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [System.Diagnostics.Process] $InputObject,

    [string] $Prefix = 'Process'
)

process {
    [pscustomobject]@{
        PSTypeName = 'Runspace.ProcessSummary'
        ProcessName = $InputObject.ProcessName
        Id = [long] $InputObject.Id
        Summary = '{0} {1} ({2})' -f $Prefix, $InputObject.ProcessName, $InputObject.Id
    }
}
