#Requires -Version 7
<#
.SYNOPSIS
    Cetus packaging pipeline (M2, .NET 10):
    self-contained publish + bundled, version-pinned node.exe and @deepseek-ai/dsh
    + portable zip + Inno Setup installer.

    Output:
      dist\app-<v>\       runnable folder (double-click Cetus.exe)
      dist\Cetus-<v>-win-x64-portable.zip
      dist\Cetus-Setup-<v>.exe
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$root       = Split-Path -Parent $PSScriptRoot          # F:\Cetus
$src        = Join-Path $root "src\Cetus.Desktop"
$solution   = Join-Path $root "Cetus.slnx"
$dist       = Join-Path $root "dist"
$runtimeDir = Join-Path $dist "runtime"

# Version pins — bump deliberately, and re-verify the app against them.
$nodeVersion = "v24.14.0"
$dshVersion  = "0.1.0-rc.6"
$nodeSource  = "C:\Program Files\nodejs\node.exe"

. (Join-Path $PSScriptRoot "common.ps1")
$dotnet = Resolve-CetusDotNet

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& $dotnet msbuild (Join-Path $src "Cetus.Desktop.csproj") `
        -nologo -getProperty:Version | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0) { throw "failed to read the project version" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use the semantic major.minor.patch form (for example, 0.1.8)."
}
$fileVersion = "0.$Version"
$appDir = Join-Path $dist "app-$Version"

Write-Host "==> [1/7] regression suite ($Configuration)"
& $dotnet test $solution -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "regression suite failed" }

Write-Host "==> [2/7] publish (self-contained, $Runtime, $Configuration)"
if (Test-Path $appDir) {
    throw "Release app directory already exists: $appDir. Bump Version instead of replacing an existing release."
}
& $dotnet publish $src -c $Configuration -r $Runtime --self-contained true -o $appDir -v q `
    "-p:Version=$Version" "-p:FileVersion=$fileVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> [3/7] bundle node.exe ($nodeVersion)"
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

Write-Host "==> [4/7] bundle dsh ($dshVersion, --omit=dev)"
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

Write-Host "==> [5/7] copy runtime into app output + manifest"
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

$defaultInstallRoot = Join-Path $env:LOCALAPPDATA "Cetus"
$longestInstalledPath = Get-ChildItem -LiteralPath $appDir -Recurse -File |
    ForEach-Object {
        Join-Path $defaultInstallRoot $_.FullName.Substring($appDir.Length + 1)
    } |
    Sort-Object Length -Descending |
    Select-Object -First 1
if ($longestInstalledPath.Length -ge 240) {
    throw "Packaged runtime path is too long for safe installation ($($longestInstalledPath.Length) characters): $longestInstalledPath"
}
Write-Host "      longest default install path: $($longestInstalledPath.Length) characters"

Write-Host "==> [6/7] zip"
$zip = Join-Path $dist "Cetus-$Version-$Runtime-portable.zip"
if (Test-Path $zip) {
    throw "Portable release already exists: $zip. Bump Version instead of replacing an existing release."
}
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "==> [7/7] installer (Inno Setup)"
$setupExe = Join-Path $dist "Cetus-Setup-$Version.exe"
$iscc = $env:CETUS_ISCC
if (-not $iscc -or -not (Test-Path $iscc)) {
    $iscc = Join-Path $root "tools\innosetup\ISCC.exe"   # portable extract
    if (-not (Test-Path $iscc)) { $iscc = "" }
}
if ($iscc) {
    if (Test-Path $setupExe) {
        throw "Installer release already exists: $setupExe. Bump Version instead of replacing an existing release."
    }
    & $iscc (Join-Path $root "installer\Cetus.iss") "/DVersion=$Version" `
        "/DFileVersion=$fileVersion" "/DAppSourceDir=$appDir"
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
if (Test-Path $setupExe) {
    $setupSize = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host "  setup   : $setupExe   ($setupSize MB)"
}
Write-Host ""
Write-Host "Sanity: bundled bin.js exists: $(Test-Path (Join-Path $appDir 'runtime\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js'))"
