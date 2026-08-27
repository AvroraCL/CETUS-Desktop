#Requires -Version 7
<#
.SYNOPSIS
    Installs and uninstalls a Cetus setup executable in an isolated directory.

.DESCRIPTION
    Waits for both Cetus.exe and its uninstaller after Setup exits. This avoids
    a false result when Inno Setup's worker is still finalizing files after the
    launcher process has returned.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$InstallerPath,

    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 300,

    [string]$ExpectedVersion,

    # Keep the root deliberately short: third-party Node packages contain
    # relative paths near 180 characters and Inno's file replacement path must
    # remain below the legacy Windows path ceiling.
    [string]$InstallDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) (
        "CS-" + [guid]::NewGuid().ToString("N").Substring(0, 8)))
)

$ErrorActionPreference = "Stop"

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if (Test-Path -LiteralPath $InstallDirectory) {
    throw "InstallDirectory must not already exist: $InstallDirectory"
}

function Wait-ForFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [System.Diagnostics.Stopwatch]$Timer,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string]$Description
    )

    while ($true) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }
        if ($Timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            break
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for ${Description}: $Path"
}

function Wait-ForLogMarker {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Pattern,
        [Parameter(Mandatory)] [System.Diagnostics.Stopwatch]$Timer,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string]$Description
    )

    while ($true) {
        if ((Test-Path -LiteralPath $Path -PathType Leaf) -and
            (Select-String -LiteralPath $Path -Pattern $Pattern -Quiet `
                -ErrorAction SilentlyContinue)) {
            return
        }
        if ($Timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            break
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for ${Description}. Log: $Path"
}

$cetusExe = Join-Path $InstallDirectory "Cetus.exe"
$uninstaller = Join-Path $InstallDirectory "unins000.exe"
$installLog = "$InstallDirectory-install.log"
$uninstallLog = "$InstallDirectory-uninstall.log"
$installed = $false
$validated = $false

try {
    & $installer "/VERYSILENT" "/SUPPRESSMSGBOXES" "/NORESTART" "/SP-" "/DIR=$InstallDirectory" "/LOG=$installLog"
    if ($LASTEXITCODE -ne 0) {
        throw "Installer exited with code $LASTEXITCODE."
    }

    $installTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-ForFile -Path $cetusExe -Timer $installTimer -TimeoutSeconds $TimeoutSeconds -Description "Cetus.exe"
    Wait-ForFile -Path $uninstaller -Timer $installTimer -TimeoutSeconds $TimeoutSeconds -Description "the Cetus uninstaller"
    $installed = $true
    Wait-ForLogMarker -Path $installLog -Pattern '\bInstallation process succeeded\.\s*$' `
        -Timer $installTimer -TimeoutSeconds $TimeoutSeconds `
        -Description "the Inno Setup installation to finish"

    $requiredRuntimeFiles = @(
        "Cetus.Runtime.dll",
        "runtime\node.exe",
        "runtime\VERSIONS.txt",
        "runtime\dsh\node_modules\@deepseek-ai\dsh\package.json"
    )
    foreach ($relativePath in $requiredRuntimeFiles) {
        $installedPath = Join-Path $InstallDirectory $relativePath
        Wait-ForFile -Path $installedPath -Timer $installTimer `
            -TimeoutSeconds $TimeoutSeconds -Description $relativePath
    }

    if ($ExpectedVersion) {
        $actualVersion = (Get-Item -LiteralPath $cetusExe).VersionInfo.ProductVersion
        if ($actualVersion -ne $ExpectedVersion) {
            throw "Installed product version is '$actualVersion'; expected '$ExpectedVersion'."
        }

        $manifest = Get-Content -LiteralPath (Join-Path $InstallDirectory "runtime\VERSIONS.txt")
        if ($manifest -notcontains "cetus=$ExpectedVersion") {
            throw "Installed runtime manifest does not contain cetus=$ExpectedVersion."
        }
    }

    # Exercise [UninstallDelete] with data that is created at runtime rather
    # than tracked by Inno Setup's installed-file manifest.
    $webViewData = Join-Path $InstallDirectory "WebView2\smoke"
    $logDirectory = Join-Path $InstallDirectory "logs"
    [void](New-Item -ItemType Directory -Force -Path $webViewData)
    [void](New-Item -ItemType Directory -Force -Path $logDirectory)
    Set-Content -LiteralPath (Join-Path $webViewData "marker.txt") -Value "smoke"
    Set-Content -LiteralPath (Join-Path $logDirectory "marker.log") -Value "smoke"
    Set-Content -LiteralPath (Join-Path $InstallDirectory "settings.json") -Value '{"port":3080}'

    Write-Host "PASS: installed and validated $cetusExe"
    $validated = $true
}
finally {
    if ($installed -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        & $uninstaller "/VERYSILENT" "/SUPPRESSMSGBOXES" "/NORESTART" "/LOG=$uninstallLog"
        if ($LASTEXITCODE -ne 0) {
            throw "Uninstaller exited with code $LASTEXITCODE."
        }

        $uninstallTimer = [System.Diagnostics.Stopwatch]::StartNew()
        Wait-ForLogMarker -Path $uninstallLog -Pattern '\bUninstallation process succeeded\.\s*$' `
            -Timer $uninstallTimer -TimeoutSeconds $TimeoutSeconds `
            -Description "the Inno Setup uninstallation to finish"
        while ($uninstallTimer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if (-not (Test-Path -LiteralPath $InstallDirectory)) {
                Write-Host "PASS: uninstalled $InstallDirectory"
                break
            }
            Start-Sleep -Milliseconds 250
        }

        if (Test-Path -LiteralPath $InstallDirectory) {
            throw "Install directory remained after uninstall: $InstallDirectory"
        }
    }

    if ($validated -and -not (Test-Path -LiteralPath $InstallDirectory)) {
        Remove-Item -LiteralPath $installLog -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $uninstallLog -Force -ErrorAction SilentlyContinue
    }
}
