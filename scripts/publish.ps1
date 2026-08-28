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

. (Join-Path $PSScriptRoot "common.ps1")
$dotnet = Resolve-CetusDotNet
$runtimeManifest = Get-CetusRuntimeManifest
$runtimeLayout = Initialize-CetusRuntime
$nodeVersion = [string]$runtimeManifest.node.version
$dshVersion = [string]$runtimeManifest.dsh.version

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

Write-Host "==> [1/7] locked restore + regression suite ($Configuration)"
& $dotnet restore $solution --locked-mode -v minimal
if ($LASTEXITCODE -ne 0) { throw "locked restore failed" }
& $dotnet test $solution -c $Configuration --no-restore -v minimal
if ($LASTEXITCODE -ne 0) { throw "regression suite failed" }

Write-Host "==> [2/7] publish (self-contained, $Runtime, $Configuration)"
if (Test-Path $appDir) {
    throw "Release app directory already exists: $appDir. Bump Version instead of replacing an existing release."
}
& $dotnet restore $src -r $Runtime --locked-mode -v minimal
if ($LASTEXITCODE -ne 0) { throw "runtime-specific locked restore failed" }
& $dotnet publish $src -c $Configuration -r $Runtime --self-contained true --no-restore -o $appDir -v q `
    "-p:Version=$Version" "-p:FileVersion=$fileVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> [3/7] verified runtime ($nodeVersion)"
if (-not (Test-CetusRuntime)) { throw "central development runtime failed validation" }

Write-Host "==> [4/7] locked DSH runtime ($dshVersion)"
$bundledNode = $runtimeLayout.NodeExe

Write-Host "==> [5/7] copy runtime into app output + manifest"
# Wipe the previous copy first: Copy-Item -Recurse onto an existing directory
# nests the source folder instead of replacing it (dsh\dsh\... duplication).
Remove-Item (Join-Path $appDir "runtime") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $appDir "runtime") | Out-Null
Copy-Item $bundledNode (Join-Path $appDir "runtime\node.exe") -Force
Copy-Item $runtimeLayout.DshRoot (Join-Path $appDir "runtime\dsh") -Recurse -Force
@(
    "cetus=$Version",
    "node=$nodeVersion",
    "dsh=$dshVersion",
    "node.sha256=$($runtimeManifest.node.executableSha256)",
    "dsh.integrity=$($runtimeManifest.dsh.integrity)",
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

Write-Host "==> [7/8] installer (Inno Setup)"
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

Write-Host "==> [8/8] SHA256SUMS (upload with the release; the updater verifies it)"
$sumsPath = Join-Path $dist "SHA256SUMS.txt"
$sumArtifacts = @($zip)
if ($iscc -and (Test-Path $setupExe)) { $sumArtifacts += $setupExe }
$sums = foreach ($artifact in $sumArtifacts) {
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $artifact)"
}
Set-Content -LiteralPath $sumsPath -Value $sums -Encoding ascii
Write-Host "      wrote $sumsPath"

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
