#Requires -Version 7
<#
.SYNOPSIS
    Runs Cetus's repeatable regression suite.

.DESCRIPTION
    Builds the complete solution and runs runtime lifecycle, desktop state
    machine, browser policy, configuration and single-instance tests.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "Cetus.slnx"
. (Join-Path $PSScriptRoot "common.ps1")
$dotnet = Resolve-CetusDotNet

Write-Host "==> Cetus regression suite ($Configuration)"
& $dotnet test $solution -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "Cetus regression suite failed"
}

Write-Host "PASS: Cetus regression suite"
