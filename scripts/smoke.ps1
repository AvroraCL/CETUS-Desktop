#Requires -Version 7
<#
.SYNOPSIS
    Runs Cetus's repeatable lifecycle smoke suite.

.DESCRIPTION
    Exercises direct-node and dsh.cmd startup paths on ephemeral loopback ports,
    verifies reuse of a healthy external DSH-compatible service, and checks that
    cancellation cleans up an owned but unready sidecar.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $root "tests\Cetus.Desktop.Tests\Cetus.Desktop.Tests.csproj"
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "==> Cetus lifecycle smoke tests ($Configuration)"
& $dotnet test $testProject -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "Cetus lifecycle smoke tests failed"
}

Write-Host "PASS: Cetus lifecycle smoke tests"
