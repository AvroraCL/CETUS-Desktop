#Requires -Version 7
<#
.SYNOPSIS
    Cetus packaging pipeline (M2, .NET 10):
    self-contained publish + bundled, version-pinned node.exe and @deepseek-ai/dsh
    + portable zip + Inno Setup installer.

    Output:
      dist\app\           runnable folder (double-click Cetus.exe)
      dist\Cetus-<v>-win-x64-portable.zip
      dist\Cetus-Setup-<v>.exe
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"

$root       = Split-Path -Parent $PSScriptRoot          # F:\Cetus
$src        = Join-Path $root "src\Cetus.Desktop"
$dist       = Join-Path $root "dist"
$appDir     = Join-Path $dist "app"
$runtimeDir = Join-Path $dist "runtime"

# Version pins — bump deliberately, and re-verify the app against them.
$nodeVersion = "v24.14.0"
$dshVersion  = "0.1.0-rc.6"
$nodeSource  = "C:\Program Files\nodejs\node.exe"

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"          # .NET 10 (this machine)
if (-not (Test-Path $dotnet)) { $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "==> [1/6] publish (self-contained, $Runtime, $Configuration)"
Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue   # idempotent output
& $dotnet publish $src -c $Configuration -r $Runtime --self-contained true -o $appDir -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> [2/6] bundle node.exe ($nodeVersion)"
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
$bundledNode = Join-Path $runtimeDir "node.exe"
$needNode = $true
if (Test-Path $bundledNode) {
    $have = (Get-Item $bundledNode).VersionInfo.ProductVersion
    if ($have -eq $nodeVersion.TrimStart('v')) { $needNode = $false }
    else { Write-Host "      node version mismatch ($have), replacing" }
}
if ($needNode) {
    if (-not (Test-Path $nodeSource)) { throw "source node.exe not found: $nodeSource" }
    Copy-Item $nodeSource $bundledNode -Force
}

Write-Host "==> [3/6] bundle dsh ($dshVersion, --omit=dev)"
$dshPkgJson = Join-Path $runtimeDir "dsh\node_modules\@deepseek-ai\dsh\package.json"
$needDsh = $true
if (Test-Path $dshPkgJson) {
    $have = (Get-Content $dshPkgJson -Raw | ConvertFrom-Json).version
    if ($have -eq $dshVersion) { $needDsh = $false }
    else { Write-Host "      dsh version mismatch ($have), reinstalling" }
}
if ($needDsh) {
    Remove-Item (Join-Path $runtimeDir "dsh") -Recurse -Force -ErrorAction SilentlyContinue
    npm install --prefix (Join-Path $runtimeDir "dsh") "@deepseek-ai/dsh@$dshVersion" --omit=dev --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
}

Write-Host "==> [4/6] copy runtime into app output + manifest"
# Wipe the previous copy first: Copy-Item -Recurse onto an existing directory
# nests the source folder instead of replacing it (dsh\dsh\... duplication).
Remove-Item (Join-Path $appDir "runtime") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $appDir "runtime") | Out-Null
Copy-Item $bundledNode (Join-Path $appDir "runtime\node.exe") -Force
Copy-Item (Join-Path $runtimeDir "dsh") (Join-Path $appDir "runtime\dsh") -Recurse -Force
@(
    "cetus=$Version",
    "node=$nodeVersion",
    "dsh=$dshVersion",
    "built=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
) | Set-Content (Join-Path $appDir "runtime\VERSIONS.txt") -Encoding UTF8

Write-Host "==> [5/6] zip"
$zip = Join-Path $dist "Cetus-$Version-$Runtime-portable.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "==> [6/6] installer (Inno Setup)"
$iscc = $env:CETUS_ISCC
if (-not $iscc -or -not (Test-Path $iscc)) {
    $iscc = Join-Path $root "tools\innosetup\ISCC.exe"   # portable extract
    if (-not (Test-Path $iscc)) { $iscc = "" }
}
if ($iscc) {
    & $iscc (Join-Path $root "installer\Cetus.iss") /DVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }
} else {
    Write-Host "      ISCC not found — installer skipped (set CETUS_ISCC to enable)"
}

$appSize = [math]::Round((Get-ChildItem $appDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "DONE"
Write-Host "  app dir : $appDir   ($appSize MB)"
Write-Host "  zip     : $zip   ($zipSize MB)"
$setupExe = Join-Path $dist "Cetus-Setup-$Version.exe"
if (Test-Path $setupExe) {
    $setupSize = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host "  setup   : $setupExe   ($setupSize MB)"
}
Write-Host ""
Write-Host "Sanity: bundled bin.js exists: $(Test-Path (Join-Path $appDir 'runtime\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js'))"
