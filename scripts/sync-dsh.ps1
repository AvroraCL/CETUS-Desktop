#Requires -Version 7
<#
.SYNOPSIS
    One-command DSH sync: check npm for a newer @deepseek-ai/dsh, update the
    pinned runtime manifest + lockfile, refresh the dev runtime, optionally
    commit+push.

.EXAMPLE
    scripts\sync-dsh.ps1                     # follow the alpha channel
    scripts\sync-dsh.ps1 -Channel next      # follow the next/RC channel
    scripts\sync-dsh.ps1 -Channel latest    # follow the stable channel
    scripts\sync-dsh.ps1 -Push              # sync + git commit & push
#>
[CmdletBinding()]
param(
    [ValidateSet("alpha", "next", "latest")]
    [string]$Channel = "alpha",

    # Use registry.npmmirror.com (China-friendly) instead of npmjs.
    [switch]$UseMirror,

    # Re-sync even when the channel already matches the pinned version.
    [switch]$Force,

    # git add + commit + push the manifest/lockfile changes when done.
    [switch]$Push,

    # Skip the dev-runtime bootstrap refresh (metadata only).
    [switch]$NoBootstrap
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

$registry = if ($UseMirror) { "https://registry.npmmirror.com" } else { "https://registry.npmjs.org" }
$pkg = [uri]::EscapeDataString("@deepseek-ai/dsh")
$repoRoot = Get-CetusRepositoryRoot
$manifestPath = Join-Path $repoRoot "eng\runtime.json"
$packageJsonPath = Join-Path $repoRoot "eng\dsh-runtime\package.json"
$lockPath = Join-Path $repoRoot "eng\dsh-runtime\package-lock.json"

Write-Host "DSH sync (channel: $Channel, registry: $registry)"
$tags = Invoke-RestMethod -Uri "$registry/-/package/$pkg/dist-tags" -TimeoutSec 30
$target = [string]$tags.$Channel
if (-not $target) { throw "npm dist-tag '$Channel' not found (available: $(($tags.PSObject.Properties.Name) -join ', '))" }

$manifest = Get-CetusRuntimeManifest
$current = [string]$manifest.dsh.version
Write-Host "pinned: $current | channel head: $target"

if (-not $Force -and $target -eq $current) {
    Write-Host "Already synced. Nothing to do." -ForegroundColor Green
    return
}

# 1) fetch the tarball for the target version and compute its SRI
$tarball = "$registry/@deepseek-ai/dsh/-/dsh-$target.tgz"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("dsh-sync-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$tgz = Join-Path $tmp "dsh.tgz"
try {
    Write-Host "downloading $tarball"
    Invoke-WebRequest -Uri $tarball -OutFile $tgz -UseBasicParsing -TimeoutSec 300
    $sha512 = (Get-FileHash -LiteralPath $tgz -Algorithm SHA512).Hash.ToLowerInvariant()
    $integrity = "sha512-" + [Convert]::ToBase64String([Convert]::FromHexString($sha512))
}
catch {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

# 2) update eng/runtime.json (dsh.version + dsh.integrity)
$manifestJson = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifestJson.dsh.version = $target
$manifestJson.dsh.integrity = $integrity
$manifestJson | ConvertTo-Json -Depth 20 | Set-Content $manifestPath -Encoding utf8
Write-Host "runtime.json -> dsh=$target"

# 3) update package.json dependency
$pkg = Get-Content $packageJsonPath -Raw | ConvertFrom-Json
$pkg.dependencies.'@deepseek-ai/dsh' = $target
$pkg | ConvertTo-Json -Depth 10 | Set-Content $packageJsonPath -Encoding utf8
Write-Host "package.json -> @deepseek-ai/dsh@$target"

# regenerate the lockfile so npm ci stays consistent with the new pin
Write-Host "regenerating package-lock.json ..."
npm install --package-lock-only --prefix (Split-Path $packageJsonPath) --omit=dev --no-audit --no-fund 2>&1 | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { throw "npm lockfile regeneration failed with exit code $LASTEXITCODE." }

# 4) refresh the dev runtime (npm ci installs the new locked tree + validates)
if (-not $NoBootstrap) {
    Initialize-CetusRuntime -Force | Out-Null
}

# 5) optional git commit+push
if ($Push) {
    git add eng/runtime.json eng/dsh-runtime/package.json eng/dsh-runtime/package-lock.json
    git commit -m "chore: sync DSH to $target"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed with exit code $LASTEXITCODE." }
    git push origin main
    if ($LASTEXITCODE -ne 0) { throw "git push origin failed with exit code $LASTEXITCODE." }
    git push gitcode main
    if ($LASTEXITCODE -ne 0) { throw "git push gitcode failed with exit code $LASTEXITCODE." }
    Write-Host "pushed DSH $target sync commit." -ForegroundColor Green
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "DONE. Pinned DSH is now $target (was $current)." -ForegroundColor Green
