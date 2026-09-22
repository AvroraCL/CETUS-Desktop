#Requires -Version 7
<#
.SYNOPSIS
    Installs and uninstalls a Cetus setup executable in an isolated directory.

.DESCRIPTION
    Pass -Version and -AppSourceDirectory to build an isolated installer
    automatically. By default, the installer uses a small fixture with the
    real Cetus and Node executables; use -FullPayload to exercise every bundled
    file. The smoke variant has its own AppId and Start menu directory, so
    this test cannot alter a user's CETUS installation.

    Waits for both Cetus.exe and its uninstaller after Setup exits. This avoids
    a false result when Inno Setup's worker is still finalizing files after the
    launcher process has returned.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = "Installer")]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$InstallerPath,

    [Parameter(Mandatory, ParameterSetName = "Build")]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory, ParameterSetName = "Build")]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$AppSourceDirectory,

    [Parameter(ParameterSetName = "Build")]
    [string]$IsccPath,

    [Parameter(ParameterSetName = "Build")]
    [switch]$FullPayload,

    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 300,

    [string]$ExpectedVersion,

    # Keep the root deliberately short: third-party Node packages contain
    # relative paths near 180 characters and Inno's file replacement path must
    # remain below the legacy Windows path ceiling.
    [string]$InstallDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) (
        "CS-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),

    # Install twice: plant a retired Node package into runtime\ before the
    # second pass and assert [InstallDelete] cleared it instead of overlaying.
    [switch]$VerifyRuntimeRebuild
)

$ErrorActionPreference = "Stop"

$generatedSmokeDirectory = $null
if ($PSCmdlet.ParameterSetName -eq "Build") {
    $root = Split-Path -Parent $PSScriptRoot
    $sourceDirectory = (Resolve-Path -LiteralPath $AppSourceDirectory).Path
    $iscc = if ($IsccPath) { $IsccPath } else { Join-Path $root "tools\innosetup\ISCC.exe" }
    if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) {
        throw "Inno Setup compiler was not found: $iscc"
    }

    $generatedSmokeDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
        "Cetus-installer-smoke-" + [guid]::NewGuid().ToString("N"))
    $smokeName = "Cetus-Setup-$Version-smoke"
    $smokePath = Join-Path $generatedSmokeDirectory "$smokeName.exe"
    New-Item -ItemType Directory -Force -Path $generatedSmokeDirectory | Out-Null

    try {
        if (-not $FullPayload) {
            $fixtureDirectory = Join-Path $generatedSmokeDirectory "payload"
            $fixtureFiles = @(
                "Cetus.exe",
                "Cetus.Runtime.dll",
                "runtime\node.exe",
                "runtime\VERSIONS.txt",
                "runtime\dsh\node_modules\@deepseek-ai\dsh\package.json",
                "runtime\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"
            )
            foreach ($relativePath in $fixtureFiles) {
                $sourcePath = Join-Path $sourceDirectory $relativePath
                if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                    throw "App source is missing the smoke fixture file: $sourcePath"
                }
                $fixturePath = Join-Path $fixtureDirectory $relativePath
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixturePath) | Out-Null
                Copy-Item -LiteralPath $sourcePath -Destination $fixturePath -Force
            }
            [ordered]@{
                schemaVersion = 1
                fullyManagedDirectories = @("runtime")
                files = @($fixtureFiles.ForEach({ $_.Replace('\', '/') }) + ".cetus-managed-files.json")
            } | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath (Join-Path $fixtureDirectory ".cetus-managed-files.json") -Encoding utf8NoBOM
            $sourceDirectory = $fixtureDirectory
        }

        Write-Host "Building isolated installer smoke package$(if ($FullPayload) { ' (full payload)' } else { ' (small fixture)' })..."
        & $iscc "/Q" (Join-Path $root "installer\Cetus.iss") "/DVersion=$Version" `
            "/DFileVersion=0.$Version" "/DAppSourceDir=$sourceDirectory" "/DSmokeTest=1" `
            "/O$generatedSmokeDirectory" "/F$smokeName"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $smokePath -PathType Leaf)) {
            throw "Smoke installer compilation failed."
        }
        $InstallerPath = $smokePath
        if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) { $ExpectedVersion = $Version }
    }
    catch {
        Remove-Item -LiteralPath $generatedSmokeDirectory -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$productName = (Get-Item -LiteralPath $installer).VersionInfo.ProductName
if ($productName.Trim() -ne "CETUS Installer Smoke") {
    if ($generatedSmokeDirectory) {
        Remove-Item -LiteralPath $generatedSmokeDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw "Installer smoke refuses the production installer. Pass -Version and -AppSourceDirectory to build an isolated smoke package."
}
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

function Stop-SmokeCetus {
    # The smoke variant skips app launch under /VERYSILENT. This guard keeps
    # cleanup safe if a future test option launches it after all.
    $stopped = Get-Process Cetus -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($InstallDirectory, [StringComparison]::OrdinalIgnoreCase)
    }
    foreach ($process in $stopped) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($stopped) {
        Start-Sleep -Seconds 2
    }
}

$cetusExe = Join-Path $InstallDirectory "Cetus.exe"
$uninstaller = Join-Path $InstallDirectory "unins000.exe"
$installLog = "$InstallDirectory-install.log"
$uninstallLog = "$InstallDirectory-uninstall.log"
$smokeStartMenuGroup = "CETUS installation smoke"
$smokeShortcut = Join-Path (Join-Path $env:APPDATA (
    "Microsoft\Windows\Start Menu\Programs\$smokeStartMenuGroup")) "Cetus 鲸鱼座.lnk"
$installed = $false
$validated = $false

try {
    $installProcess = Start-Process -FilePath $installer -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
        "/DIR=$InstallDirectory",
        "/LOG=$installLog"
    ) -PassThru
    $installProcess.WaitForExit()
    if ($installProcess.ExitCode -ne 0) {
        throw "Installer exited with code $($installProcess.ExitCode)."
    }

    $installTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-ForFile -Path $cetusExe -Timer $installTimer -TimeoutSeconds $TimeoutSeconds -Description "Cetus.exe"
    Wait-ForFile -Path $uninstaller -Timer $installTimer -TimeoutSeconds $TimeoutSeconds -Description "the Cetus uninstaller"
    Wait-ForFile -Path $smokeShortcut -Timer $installTimer -TimeoutSeconds $TimeoutSeconds `
        -Description "the smoke Start menu shortcut"
    $installed = $true
    Wait-ForLogMarker -Path $installLog -Pattern '\bInstallation process succeeded\.\s*$' `
        -Timer $installTimer -TimeoutSeconds $TimeoutSeconds `
        -Description "the Inno Setup installation to finish"

    $requiredRuntimeFiles = @(
        "Cetus.Runtime.dll",
        ".cetus-managed-files.json",
        "runtime\node.exe",
        "runtime\VERSIONS.txt",
        "runtime\dsh\node_modules\@deepseek-ai\dsh\package.json"
    )
    foreach ($relativePath in $requiredRuntimeFiles) {
        $installedPath = Join-Path $InstallDirectory $relativePath
        Wait-ForFile -Path $installedPath -Timer $installTimer `
            -TimeoutSeconds $TimeoutSeconds -Description $relativePath
    }

    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($smokeShortcut)
    if (-not [string]::Equals($shortcut.TargetPath, $cetusExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Smoke Start menu shortcut targets '$($shortcut.TargetPath)' instead of '$cetusExe'."
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
    Stop-SmokeCetus

    if ($VerifyRuntimeRebuild) {
        # Regression drill for the mixed-runtime tree failure: plant a
        # package retired by the new release, reinstall over the same
        # directory, and require [InstallDelete] to have rebuilt runtime\.
        $stalePackage = Join-Path $InstallDirectory `
            "runtime\dsh\node_modules\@deepseek-ai\stale-leftover-package\lib\index.js"
        [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stalePackage))
        Set-Content -LiteralPath $stalePackage -Value "leftover"

        $secondLog = "$InstallDirectory-install-2.log"
        $secondInstallProcess = Start-Process -FilePath $installer -ArgumentList @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
            "/DIR=$InstallDirectory",
            "/LOG=$secondLog"
        ) -PassThru
        $secondInstallProcess.WaitForExit()
        if ($secondInstallProcess.ExitCode -ne 0) {
            throw "Second installer pass exited with code $($secondInstallProcess.ExitCode)."
        }

        $reinstallTimer = [System.Diagnostics.Stopwatch]::StartNew()
        Wait-ForLogMarker -Path $secondLog -Pattern '\bInstallation process succeeded\.\s*$' `
            -Timer $reinstallTimer -TimeoutSeconds $TimeoutSeconds `
            -Description "the second Inno Setup installation to finish"
        Wait-ForFile -Path (Join-Path $InstallDirectory "runtime\node.exe") `
            -Timer $reinstallTimer -TimeoutSeconds $TimeoutSeconds -Description "runtime\node.exe"

        if (Test-Path -LiteralPath $stalePackage) {
            throw "runtime was overlaid instead of rebuilt: the leftover package survived the reinstall."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory "runtime\dsh\node_modules\@deepseek-ai\dsh\package.json") -PathType Leaf)) {
            throw "runtime rebuild lost the DSH package."
        }
        Write-Host "PASS: second install rebuilt the runtime tree (leftover cleared)"
        Stop-SmokeCetus
    }
}
finally {
    Stop-SmokeCetus
    if ($installed -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
            "/LOG=$uninstallLog"
        ) -PassThru
        $uninstallProcess.WaitForExit()
        if ($uninstallProcess.ExitCode -ne 0) {
            throw "Uninstaller exited with code $($uninstallProcess.ExitCode)."
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
        if (Test-Path -LiteralPath $smokeShortcut) {
            throw "Smoke Start menu shortcut remained after uninstall: $smokeShortcut"
        }
    }

    if ($validated -and -not (Test-Path -LiteralPath $InstallDirectory)) {
        Remove-Item -LiteralPath $installLog -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $uninstallLog -Force -ErrorAction SilentlyContinue
    }
    if ($generatedSmokeDirectory) {
        Remove-Item -LiteralPath $generatedSmokeDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
