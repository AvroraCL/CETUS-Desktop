#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ApplicationPath,

    [ValidateRange(10, 180)]
    [int]$TimeoutSeconds = 60,

    [int]$PreserveProcessId
)

$arguments = @{
    ApplicationPath = $ApplicationPath
    RuntimeMode = "Bundled"
    TimeoutSeconds = $TimeoutSeconds
}
if ($PreserveProcessId) { $arguments.PreserveProcessId = $PreserveProcessId }

& (Join-Path $PSScriptRoot "app-smoke.ps1") @arguments
