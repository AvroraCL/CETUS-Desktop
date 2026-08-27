Set-StrictMode -Version Latest

function Get-CetusRepositoryRoot {
    [CmdletBinding()]
    param()

    return [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}

function Resolve-CetusDotNet {
    [CmdletBinding()]
    param()

    $candidates = @(
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
        "C:\Program Files\dotnet\dotnet.exe"
    )

    $pathCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($pathCommand) { $candidates += $pathCommand.Source }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks -match '^10\.') {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw ".NET 10 SDK was not found. Install SDK 10.0.400 or a compatible patch."
}

function Get-CetusRuntimeManifest {
    [CmdletBinding()]
    param()

    $path = Join-Path (Get-CetusRepositoryRoot) "eng\runtime.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Runtime manifest is missing: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-CetusRuntimeLayout {
    [CmdletBinding()]
    param()

    $root = Get-CetusRepositoryRoot
    $devRoot = Join-Path $root ".dev"
    $runtime = Join-Path $devRoot "runtime"
    return [pscustomobject]@{
        RepositoryRoot = $root
        DevRoot = $devRoot
        CacheRoot = Join-Path $devRoot "cache"
        RuntimeRoot = $runtime
        NodeExe = Join-Path $runtime "node.exe"
        DshRoot = Join-Path $runtime "dsh"
        DshEntry = Join-Path $runtime "dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"
        DshPackage = Join-Path $runtime "dsh\node_modules\@deepseek-ai\dsh\package.json"
        Versions = Join-Path $runtime "VERSIONS.txt"
    }
}

function Test-CetusRuntime {
    [CmdletBinding()]
    param([switch]$Quiet)

    $manifest = Get-CetusRuntimeManifest
    $layout = Get-CetusRuntimeLayout
    $problems = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $layout.NodeExe -PathType Leaf)) {
        $problems.Add("node.exe is missing")
    }
    else {
        $hash = (Get-FileHash -LiteralPath $layout.NodeExe -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne [string]$manifest.node.executableSha256) { $problems.Add("node.exe hash mismatch") }
    }

    if (-not (Test-Path -LiteralPath $layout.DshEntry -PathType Leaf)) { $problems.Add("DSH entry is missing") }
    if (-not (Test-Path -LiteralPath $layout.DshPackage -PathType Leaf)) {
        $problems.Add("DSH package metadata is missing")
    }
    else {
        $installed = Get-Content -LiteralPath $layout.DshPackage -Raw | ConvertFrom-Json
        if ([string]$installed.version -ne [string]$manifest.dsh.version) { $problems.Add("DSH version mismatch") }
    }

    if (-not (Test-Path -LiteralPath $layout.Versions -PathType Leaf)) {
        $problems.Add("runtime version marker is missing")
    }
    else {
        $marker = Get-Content -LiteralPath $layout.Versions -Raw
        if ($marker -notmatch "(?m)^node=$([regex]::Escape([string]$manifest.node.version))\r?$") {
            $problems.Add("runtime Node marker mismatch")
        }
        if ($marker -notmatch "(?m)^dsh=$([regex]::Escape([string]$manifest.dsh.version))\r?$") {
            $problems.Add("runtime DSH marker mismatch")
        }
    }

    if (-not $Quiet -and $problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "      - $_" -ForegroundColor Yellow }
    }
    return $problems.Count -eq 0
}

function Initialize-CetusRuntime {
    [CmdletBinding()]
    param([switch]$Force)

    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        throw "CETUS runtime bootstrap supports Windows x64 only."
    }

    $manifest = Get-CetusRuntimeManifest
    $layout = Get-CetusRuntimeLayout
    $archive = Join-Path $layout.CacheRoot ([string]$manifest.node.archive)
    $expectedArchiveHash = ([string]$manifest.node.sha256).ToLowerInvariant()
    $archiveValid = $false
    $archiveCorrupt = $false
    if (Test-Path -LiteralPath $archive -PathType Leaf) {
        $archiveValid = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedArchiveHash
        $archiveCorrupt = -not $archiveValid
    }

    if (-not $Force -and -not $archiveCorrupt -and (Test-CetusRuntime -Quiet)) {
        Write-Host "Runtime ready: Node $($manifest.node.version), DSH $($manifest.dsh.version)"
        return $layout
    }

    New-Item -ItemType Directory -Force -Path $layout.CacheRoot | Out-Null
    if ($archiveCorrupt) {
        Write-Host "Cached Node archive is corrupt; downloading a verified copy." -ForegroundColor Yellow
        Remove-Item -LiteralPath $archive -Force
    }

    if (-not $archiveValid) {
        $partial = "$archive.partial"
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        Write-Host "Downloading $($manifest.node.url)"
        try {
            Invoke-WebRequest -Uri ([string]$manifest.node.url) -OutFile $partial -UseBasicParsing
            $downloadHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($downloadHash -ne $expectedArchiveHash) {
                throw "Node archive SHA-256 mismatch. Expected $expectedArchiveHash, got $downloadHash."
            }
            Move-Item -LiteralPath $partial -Destination $archive
        }
        finally {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        }
    }

    $workRoot = Join-Path $layout.DevRoot ("bootstrap-" + [guid]::NewGuid().ToString("N"))
    $extractRoot = Join-Path $workRoot "node-archive"
    $stage = Join-Path $workRoot "runtime"
    $backup = "$($layout.RuntimeRoot).previous"
    try {
        New-Item -ItemType Directory -Force -Path $extractRoot, $stage | Out-Null
        Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
        $nodeRoot = Get-ChildItem -LiteralPath $extractRoot -Directory | Select-Object -First 1
        if (-not $nodeRoot) { throw "Node archive did not contain its expected root directory." }

        $archiveNode = Join-Path $nodeRoot.FullName "node.exe"
        $npmCli = Join-Path $nodeRoot.FullName "node_modules\npm\bin\npm-cli.js"
        if (-not (Test-Path -LiteralPath $archiveNode -PathType Leaf) -or
            -not (Test-Path -LiteralPath $npmCli -PathType Leaf)) {
            throw "Node archive is missing node.exe or its bundled npm CLI."
        }
        $nodeHash = (Get-FileHash -LiteralPath $archiveNode -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($nodeHash -ne ([string]$manifest.node.executableSha256).ToLowerInvariant()) {
            throw "Extracted node.exe SHA-256 mismatch."
        }

        Copy-Item -LiteralPath $archiveNode -Destination (Join-Path $stage "node.exe")
        $dshStage = Join-Path $stage "dsh"
        New-Item -ItemType Directory -Path $dshStage | Out-Null
        Copy-Item -LiteralPath (Join-Path $layout.RepositoryRoot "eng\dsh-runtime\package.json") -Destination $dshStage
        Copy-Item -LiteralPath (Join-Path $layout.RepositoryRoot "eng\dsh-runtime\package-lock.json") -Destination $dshStage

        Write-Host "Installing locked DSH runtime with Node-bundled npm..."
        & $archiveNode $npmCli ci --prefix $dshStage --omit=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }

        $installedPackage = Join-Path $dshStage "node_modules\@deepseek-ai\dsh\package.json"
        $installedEntry = Join-Path $dshStage "node_modules\@deepseek-ai\dsh\lib\bin.js"
        if (-not (Test-Path -LiteralPath $installedEntry -PathType Leaf)) {
            throw "Locked DSH install did not produce lib/bin.js."
        }
        $installedVersion = (Get-Content -LiteralPath $installedPackage -Raw | ConvertFrom-Json).version
        if ([string]$installedVersion -ne [string]$manifest.dsh.version) {
            throw "Installed DSH version mismatch: $installedVersion"
        }

        @(
            "node=$($manifest.node.version)"
            "node.sha256=$($manifest.node.executableSha256)"
            "dsh=$($manifest.dsh.version)"
            "dsh.integrity=$($manifest.dsh.integrity)"
        ) | Set-Content -LiteralPath (Join-Path $stage "VERSIONS.txt") -Encoding utf8

        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $layout.RuntimeRoot) {
            Move-Item -LiteralPath $layout.RuntimeRoot -Destination $backup
        }
        try {
            Move-Item -LiteralPath $stage -Destination $layout.RuntimeRoot
        }
        catch {
            if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $layout.RuntimeRoot)) {
                Move-Item -LiteralPath $backup -Destination $layout.RuntimeRoot
            }
            throw
        }
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-CetusRuntime)) { throw "Runtime bootstrap completed but validation failed." }
    Write-Host "Runtime bootstrapped: Node $($manifest.node.version), DSH $($manifest.dsh.version)" -ForegroundColor Green
    return $layout
}
