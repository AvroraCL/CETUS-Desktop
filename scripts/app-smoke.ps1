#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ApplicationPath,

    [Parameter(Mandatory)]
    [ValidateSet("Bundled", "Explicit")]
    [string]$RuntimeMode,

    [string]$NodeExe,
    [string]$DshEntry,

    [ValidateRange(10, 180)]
    [int]$TimeoutSeconds = 60,

    [string]$TestRoot,
    [int]$PreserveProcessId
)

$ErrorActionPreference = "Stop"
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$applicationDirectory = Split-Path -Parent $application
if ($PreserveProcessId -and -not (Get-Process -Id $PreserveProcessId -ErrorAction SilentlyContinue)) {
    throw "Process to preserve is not running: $PreserveProcessId"
}

if ($RuntimeMode -eq "Bundled") {
    $expectedNode = Join-Path $applicationDirectory "runtime\node.exe"
    $expectedEntry = Join-Path $applicationDirectory "runtime\dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"
}
else {
    if (-not $NodeExe -or -not (Test-Path -LiteralPath $NodeExe -PathType Leaf)) {
        throw "Explicit runtime mode requires an existing -NodeExe."
    }
    if (-not $DshEntry -or -not (Test-Path -LiteralPath $DshEntry -PathType Leaf)) {
        throw "Explicit runtime mode requires an existing -DshEntry."
    }
    $expectedNode = $NodeExe
    $expectedEntry = $DshEntry
}
$expectedNode = [IO.Path]::GetFullPath($expectedNode)
$expectedEntry = [IO.Path]::GetFullPath($expectedEntry)
if (-not (Test-Path -LiteralPath $expectedNode -PathType Leaf) -or
    -not (Test-Path -LiteralPath $expectedEntry -PathType Leaf)) {
    throw "Expected runtime is incomplete: $expectedNode / $expectedEntry"
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$ownsTestRoot = [string]::IsNullOrWhiteSpace($TestRoot)
if ($ownsTestRoot) {
    $TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("Cetus-App-Smoke-" + [guid]::NewGuid().ToString("N"))
}
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
New-Item -ItemType Directory -Force -Path $TestRoot | Out-Null
$nodeProcessesBefore = @(Get-Process -Name node -ErrorAction SilentlyContinue | ForEach-Object Id)
$applicationProcess = $null
$ownedNodeIds = @()

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $application
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $applicationDirectory
    $startInfo.Environment["CETUS_INSTANCE_ID"] = "app-smoke-$([guid]::NewGuid().ToString("N"))"
    $startInfo.Environment["CETUS_DEV"] = "1"
    $startInfo.Environment["CETUS_PORT"] = $port.ToString()
    $startInfo.Environment["CETUS_DSH_HOME"] = Join-Path $TestRoot "dsh-home"
    $startInfo.Environment["DSH_HOME"] = Join-Path $TestRoot "dsh-home"
    $startInfo.Environment["CETUS_SETTINGS_PATH"] = Join-Path $TestRoot "settings.json"
    $startInfo.Environment["CETUS_WEBVIEW2_USER_DATA"] = Join-Path $TestRoot "webview2"
    $startInfo.Environment["CETUS_LOG_DIR"] = Join-Path $TestRoot "logs"
    if ($RuntimeMode -eq "Explicit") {
        $startInfo.Environment["CETUS_NODE_EXE"] = $expectedNode
        $startInfo.Environment["CETUS_DSH_ENTRY"] = $expectedEntry
    }
    else {
        [void]$startInfo.Environment.Remove("CETUS_NODE_EXE")
        [void]$startInfo.Environment.Remove("CETUS_DSH_ENTRY")
    }

    $applicationProcess = [Diagnostics.Process]::Start($startInfo)
    if (-not $applicationProcess) { throw "Failed to start CETUS application." }

    $startupWatch = [Diagnostics.Stopwatch]::StartNew()
    $healthy = $false
    $windowReady = $false
    while ($startupWatch.Elapsed -lt [TimeSpan]::FromSeconds($TimeoutSeconds)) {
        if ($applicationProcess.HasExited) {
            throw "CETUS exited early with code $($applicationProcess.ExitCode)."
        }
        $applicationProcess.Refresh()
        $windowReady = $applicationProcess.MainWindowHandle -ne 0 -and
            $applicationProcess.MainWindowTitle -match 'DEV'
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port" -UseBasicParsing -TimeoutSec 2
            $healthy = $response.StatusCode -eq 200 -and $response.Content -match 'id=["'']root["'']'
        }
        catch { $healthy = $false }
        if ($windowReady -and $healthy) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $windowReady) { throw "CETUS did not expose a DEV main-window HWND before timeout." }
    if (-not $healthy) { throw "DSH did not become healthy on port $port before timeout." }

    $newNodes = @(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" |
        Where-Object { $nodeProcessesBefore -notcontains [int]$_.ProcessId })
    $runtimeNodes = @($newNodes | Where-Object {
        $_.ExecutablePath -and
        [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $expectedNode, [StringComparison]::OrdinalIgnoreCase) -and
        $_.CommandLine -and $_.CommandLine -match [regex]::Escape($expectedEntry)
    })
    if ($runtimeNodes.Count -eq 0) {
        throw "No new Node process used the expected executable and DSH entry. Observed: $($newNodes.ExecutablePath -join ', ')"
    }
    $ownedNodeIds = @($runtimeNodes | ForEach-Object { [int]$_.ProcessId })
    Write-Host "PASS: DEV HWND $($applicationProcess.MainWindowHandle), app PID $($applicationProcess.Id), Node PID $($ownedNodeIds -join ','), port $port"
    Write-Host "PASS: runtime $expectedNode"

    Stop-Process -Id $applicationProcess.Id -Force
    [void]$applicationProcess.WaitForExit(10000)

    $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remainingNodes = @($ownedNodeIds | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        $portOwner = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
        if ($remainingNodes.Count -eq 0 -and -not $portOwner) { break }
        Start-Sleep -Milliseconds 250
    } while ($cleanupWatch.Elapsed -lt [TimeSpan]::FromSeconds(15))

    if ($remainingNodes.Count -ne 0 -or $portOwner) {
        throw "Job cleanup failed. Remaining Node PIDs: $($remainingNodes.Id -join ', '); port owner: $($portOwner.OwningProcess -join ', ')"
    }
    if ($PreserveProcessId -and -not (Get-Process -Id $PreserveProcessId -ErrorAction SilentlyContinue)) {
        throw "Process to preserve was disturbed: $PreserveProcessId"
    }
    Write-Host "PASS: application exit reclaimed Node and released port $port" -ForegroundColor Green
}
finally {
    if ($applicationProcess -and -not $applicationProcess.HasExited) {
        Stop-Process -Id $applicationProcess.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($nodeId in $ownedNodeIds) {
        Stop-Process -Id $nodeId -Force -ErrorAction SilentlyContinue
    }
    if ($ownsTestRoot) {
        $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $leaf = Split-Path -Leaf $TestRoot
        if ($TestRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            $leaf -like "Cetus-App-Smoke-*") {
            Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
