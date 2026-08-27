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

$ErrorActionPreference = "Stop"
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
if ($PreserveProcessId -and -not (Get-Process -Id $PreserveProcessId -ErrorAction SilentlyContinue)) {
    throw "Process to preserve is not running: $PreserveProcessId"
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "Cetus-Packaged-Smoke-" + [guid]::NewGuid().ToString("N"))
[void](New-Item -ItemType Directory -Path $testRoot)
$nodeProcessesBefore = @(Get-Process -Name node -ErrorAction SilentlyContinue | ForEach-Object Id)
$applicationProcess = $null
$newNodeIds = @()

try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $application
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = Split-Path -Parent $application
    $startInfo.Environment["CETUS_INSTANCE_ID"] = "package-smoke-$([guid]::NewGuid().ToString("N"))"
    $startInfo.Environment["CETUS_DEV"] = "1"
    $startInfo.Environment["CETUS_PORT"] = $port.ToString()
    $startInfo.Environment["CETUS_DSH_HOME"] = Join-Path $testRoot "dsh-home"
    $startInfo.Environment["DSH_HOME"] = Join-Path $testRoot "dsh-home"
    $startInfo.Environment["CETUS_SETTINGS_PATH"] = Join-Path $testRoot "settings.json"
    $startInfo.Environment["CETUS_WEBVIEW2_USER_DATA"] = Join-Path $testRoot "webview2"
    $startInfo.Environment["CETUS_LOG_DIR"] = Join-Path $testRoot "logs"
    [void]$startInfo.Environment.Remove("CETUS_NODE_EXE")
    [void]$startInfo.Environment.Remove("CETUS_DSH_ENTRY")

    $applicationProcess = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $applicationProcess) {
        throw "Failed to start packaged Cetus."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $response = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($applicationProcess.HasExited) {
            throw "Packaged Cetus exited early with code $($applicationProcess.ExitCode)."
        }

        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and $response.Content -match 'id=["'']root["'']') {
                break
            }
        }
        catch {
            # Startup races are expected until DSH begins listening.
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $response -or
        $response.StatusCode -ne 200 -or
        $response.Content -notmatch 'id=["'']root["'']') {
        throw "Packaged DSH did not become healthy on port $port."
    }

    $newNodes = @(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" |
        Where-Object { $nodeProcessesBefore -notcontains [int]$_.ProcessId })
    if ($newNodes.Count -eq 0) {
        throw "No packaged Node process was observed."
    }

    $expectedNode = [IO.Path]::GetFullPath(
        (Join-Path (Split-Path -Parent $application) "runtime\node.exe"))
    $bundledNodes = @($newNodes |
        Where-Object {
            $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath) -eq $expectedNode
        })
    $newNodeIds = @($bundledNodes | ForEach-Object { [int]$_.ProcessId })
    $bundledNode = $bundledNodes |
        Select-Object -First 1
    if (-not $bundledNode) {
        throw "New Node process did not use bundled runtime: $($newNodes.ExecutablePath -join ', ')"
    }
    if ($bundledNode.CommandLine -notmatch [regex]::Escape("@deepseek-ai\dsh\lib\bin.js")) {
        throw "Bundled Node did not use the packaged DSH entry: $($bundledNode.CommandLine)"
    }

    Write-Host "PASS: app PID $($applicationProcess.Id), bundled Node PID $($bundledNode.ProcessId), DSH healthy on port $port"

    Stop-Process -Id $applicationProcess.Id -Force
    [void]$applicationProcess.WaitForExit(10000)

    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $remainingNodes = @($newNodeIds |
            Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        $endpointAlive = $false
        try {
            $null = Invoke-WebRequest -Uri "http://127.0.0.1:$port" -UseBasicParsing -TimeoutSec 1
            $endpointAlive = $true
        }
        catch {
        }

        if ($remainingNodes.Count -eq 0 -and -not $endpointAlive) {
            break
        }
        Start-Sleep -Milliseconds 250
    }
    while ([DateTime]::UtcNow -lt $cleanupDeadline)

    if ($remainingNodes.Count -ne 0 -or $endpointAlive) {
        throw "Job cleanup failed. Remaining Node PIDs: $($remainingNodes.Id -join ', '); endpoint alive: $endpointAlive"
    }
    if ($PreserveProcessId -and -not (Get-Process -Id $PreserveProcessId -ErrorAction SilentlyContinue)) {
        throw "Process to preserve was disturbed: $PreserveProcessId"
    }

    Write-Host "PASS: app exit reclaimed bundled Node and released the port"
}
finally {
    if ($applicationProcess -and -not $applicationProcess.HasExited) {
        Stop-Process -Id $applicationProcess.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($nodeId in $newNodeIds) {
        Stop-Process -Id $nodeId -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 500
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $isOwnedTestDirectory =
        $resolvedTestRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestRoot) -like "Cetus-Packaged-Smoke-*"
    if ($isOwnedTestDirectory) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
