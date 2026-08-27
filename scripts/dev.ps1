#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("doctor", "bootstrap", "run", "test", "check", "smoke", "reset")]
    [string]$Command = "run",

    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')]
    [string]$Profile = "default",

    [ValidateRange(0, 65535)]
    [int]$Port = 3084,

    [switch]$Force,
    [switch]$All
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

$root = Get-CetusRepositoryRoot
$layout = Get-CetusRuntimeLayout
$solution = Join-Path $root "Cetus.slnx"
$project = Join-Path $root "src\Cetus.Desktop\Cetus.Desktop.csproj"
$testProject = Join-Path $root "tests\Cetus.Desktop.Tests\Cetus.Desktop.Tests.csproj"

function Get-ProfileLayout {
    param([Parameter(Mandatory)][string]$Name)

    $profileRoot = Join-Path $layout.DevRoot "profiles\$Name"
    return [pscustomobject]@{
        Root = $profileRoot
        DshHome = Join-Path $profileRoot "dsh-home"
        Settings = Join-Path $profileRoot "settings.json"
        WebView = Join-Path $profileRoot "webview2"
        Logs = Join-Path $profileRoot "logs"
        Session = Join-Path $profileRoot "session.json"
        Artifacts = Join-Path $profileRoot "artifacts"
        Application = Join-Path $profileRoot "artifacts\bin\Cetus.Desktop\debug\Cetus.exe"
    }
}

function Assert-OwnedDevPath {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolvedDev = [IO.Path]::GetFullPath($layout.DevRoot).TrimEnd('\')
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolvedDev.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        ($resolvedPath -ne $resolvedDev -and
         -not $resolvedPath.StartsWith($resolvedDev + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to modify a path outside the repository .dev directory: $resolvedPath"
    }
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally { $listener.Stop() }
}

function Get-PortOwner {
    param([Parameter(Mandatory)][int]$Number)

    $connection = Get-NetTCPConnection -State Listen -LocalPort $Number -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $connection) { return $null }
    $process = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        Id = [int]$connection.OwningProcess
        Name = if ($process) { $process.ProcessName } else { "unknown" }
        Path = if ($process) { $process.Path } else { $null }
    }
}

function Stop-ProfileProcess {
    param([Parameter(Mandatory)]$ProfileLayout)

    if (-not (Test-Path -LiteralPath $ProfileLayout.Session -PathType Leaf)) { return }
    $session = Get-Content -LiteralPath $ProfileLayout.Session -Raw | ConvertFrom-Json
    $process = Get-Process -Id ([int]$session.pid) -ErrorAction SilentlyContinue
    if (-not $process) {
        Remove-Item -LiteralPath $ProfileLayout.Session -Force
        return
    }

    $expected = [IO.Path]::GetFullPath([string]$session.applicationPath)
    $actual = if ($process.Path) { [IO.Path]::GetFullPath($process.Path) } else { "" }
    if ($expected -ne [IO.Path]::GetFullPath($ProfileLayout.Application) -or
        -not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Profile PID $($process.Id) no longer points to its recorded CETUS executable; it will not be stopped."
    }

    Write-Host "Stopping profile '$Profile' process $($process.Id)..."
    Stop-Process -Id $process.Id
    if (-not $process.WaitForExit(10000)) {
        Stop-Process -Id $process.Id -Force
        [void]$process.WaitForExit(5000)
    }
    Remove-Item -LiteralPath $ProfileLayout.Session -Force -ErrorAction SilentlyContinue
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$LogPath
    )

    $dotnet = Resolve-CetusDotNet
    if ($LogPath) {
        & $dotnet @Arguments 2>&1 | Tee-Object -FilePath $LogPath
    }
    else {
        & $dotnet @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-LockedRestore {
    param(
        [string]$LogPath,
        [string]$Target = $solution,
        [string]$ArtifactsPath
    )
    $arguments = @("restore", $Target, "--locked-mode", "-v", "minimal")
    if ($ArtifactsPath) { $arguments += @("--artifacts-path", $ArtifactsPath) }
    Invoke-DotNet -Arguments $arguments -LogPath $LogPath
}

function New-TestRun {
    $path = Join-Path $layout.DevRoot ("test-runs\" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Remove-OwnedDevDirectory {
    param([Parameter(Mandatory)][string]$Path)
    Assert-OwnedDevPath $Path
    $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch {
            if ($cleanupWatch.Elapsed -ge [TimeSpan]::FromSeconds(15)) { throw }
            Start-Sleep -Milliseconds 250
        }
    } while ($true)
}

function Test-WebView2Runtime {
    $locations = @(
        "${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application",
        "$env:ProgramFiles\Microsoft\EdgeWebView\Application"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }
    return @($locations).Count -gt 0
}

function Invoke-Doctor {
    $failures = [Collections.Generic.List[string]]::new()
    $warnings = [Collections.Generic.List[string]]::new()
    $manifest = Get-CetusRuntimeManifest

    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        $failures.Add("OS: Windows x64 is required")
    } else { Write-Host "PASS OS: Windows x64" }

    if ($PSVersionTable.PSVersion.Major -lt 7) { $failures.Add("PowerShell 7 is required") }
    else { Write-Host "PASS PowerShell: $($PSVersionTable.PSVersion)" }

    try {
        $dotnet = Resolve-CetusDotNet
        $sdk = (& $dotnet --version).Trim()
        if ($sdk -notmatch '^10\.0\.') { $failures.Add(".NET 10 SDK is required; found $sdk") }
        else { Write-Host "PASS .NET SDK: $sdk ($dotnet)" }
    } catch { $failures.Add($_.Exception.Message) }

    if (Test-WebView2Runtime) { Write-Host "PASS WebView2 Runtime" }
    else { $failures.Add("WebView2 Runtime was not found") }

    $lockPath = Join-Path $root "eng\dsh-runtime\package-lock.json"
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        $failures.Add("DSH package-lock.json is missing")
    }
    else {
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
        $entry = $lock.packages['node_modules/@deepseek-ai/dsh']
        if (-not $entry -or $entry.version -ne $manifest.dsh.version -or $entry.integrity -ne $manifest.dsh.integrity) {
            $failures.Add("DSH lockfile does not match eng/runtime.json")
        } else { Write-Host "PASS Runtime manifest and DSH lock" }
    }

    if (Test-CetusRuntime -Quiet) { Write-Host "PASS Runtime cache: complete" }
    else { $warnings.Add("Runtime cache is absent or incomplete; bootstrap/run will repair it") }

    $longestProbe = Join-Path $layout.DevRoot "runtime\dsh\node_modules\@deepseek-ai\dsh\node_modules\placeholder\package.json"
    if ($longestProbe.Length -ge 240) { $warnings.Add("Repository path is long ($($longestProbe.Length) character probe); use a shorter checkout path") }
    else { Write-Host "PASS Path length probe: $($longestProbe.Length) characters" }

    if ($Port -ne 0) {
        $owner = Get-PortOwner $Port
        if ($owner) { $warnings.Add("Port $Port is occupied by PID $($owner.Id) [$($owner.Name)] $($owner.Path)") }
        else { Write-Host "PASS Port ${Port}: available" }
    }

    foreach ($warning in $warnings) { Write-Host "WARN $warning" -ForegroundColor Yellow }
    foreach ($failure in $failures) { Write-Host "FAIL $failure" -ForegroundColor Red }
    if ($failures.Count -gt 0) { throw "Doctor found $($failures.Count) blocking problem(s)." }
    Write-Host "Doctor completed: $($warnings.Count) warning(s)." -ForegroundColor Green
}

function Invoke-Run {
    $runtime = Initialize-CetusRuntime
    $profileLayout = Get-ProfileLayout $Profile
    Assert-OwnedDevPath $profileLayout.Root
    New-Item -ItemType Directory -Force -Path $profileLayout.DshHome, $profileLayout.WebView, $profileLayout.Logs | Out-Null
    Stop-ProfileProcess $profileLayout

    $effectivePort = if ($Port -eq 0) { Get-FreePort } else { $Port }
    $owner = Get-PortOwner $effectivePort
    if ($owner) {
        throw "Port $effectivePort is occupied by PID $($owner.Id) [$($owner.Name)] $($owner.Path). The process was not changed."
    }

    Invoke-LockedRestore -Target $project -ArtifactsPath $profileLayout.Artifacts
    Invoke-DotNet -Arguments @(
        "build", $project, "-c", "Debug", "--no-restore", "--artifacts-path", $profileLayout.Artifacts, "-v", "minimal"
    )
    $profileApp = $profileLayout.Application
    if (-not (Test-Path -LiteralPath $profileApp -PathType Leaf)) { throw "Debug application was not built: $profileApp" }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $profileApp
    $startInfo.WorkingDirectory = Split-Path -Parent $profileApp
    $startInfo.UseShellExecute = $false
    $startInfo.Environment["CETUS_DEV"] = "1"
    $startInfo.Environment["CETUS_INSTANCE_ID"] = "dev-$Profile"
    $startInfo.Environment["CETUS_PORT"] = $effectivePort.ToString()
    $startInfo.Environment["CETUS_NODE_EXE"] = $runtime.NodeExe
    $startInfo.Environment["CETUS_DSH_ENTRY"] = $runtime.DshEntry
    $startInfo.Environment["CETUS_DSH_HOME"] = $profileLayout.DshHome
    $startInfo.Environment["DSH_HOME"] = $profileLayout.DshHome
    $startInfo.Environment["CETUS_SETTINGS_PATH"] = $profileLayout.Settings
    $startInfo.Environment["CETUS_WEBVIEW2_USER_DATA"] = $profileLayout.WebView
    $startInfo.Environment["CETUS_LOG_DIR"] = $profileLayout.Logs

    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process) { throw "Failed to start CETUS DEV." }
    [ordered]@{
        pid = $process.Id
        applicationPath = [IO.Path]::GetFullPath($profileApp)
        port = $effectivePort
        profile = $Profile
        startedAt = [DateTimeOffset]::Now.ToString("O")
    } | ConvertTo-Json | Set-Content -LiteralPath $profileLayout.Session -Encoding utf8

    Write-Host "CETUS DEV started: PID $($process.Id), profile '$Profile', http://127.0.0.1:$effectivePort/" -ForegroundColor Green
    Write-Host "State: $($profileLayout.Root)"
}

function Invoke-FastTests {
    $testRun = New-TestRun
    $artifacts = Join-Path $layout.DevRoot "build\fast-tests"
    $previousTestRoot = $env:CETUS_TEST_ROOT
    $env:CETUS_TEST_ROOT = $testRun
    try {
        Invoke-LockedRestore -Target $testProject -ArtifactsPath $artifacts
        Invoke-DotNet -Arguments @(
            "test", $testProject, "-c", "Debug", "--no-restore", "--artifacts-path", $artifacts,
            "-v", "minimal", "--filter", "Category!=Integration"
        )
        Remove-OwnedDevDirectory $testRun
    }
    catch {
        Write-Host "Fast-test artifacts retained at: $testRun" -ForegroundColor Red
        throw
    }
    finally {
        $env:CETUS_TEST_ROOT = $previousTestRoot
    }
}

function Invoke-Check {
    $runtime = Initialize-CetusRuntime
    $testRun = New-TestRun
    $previousTestRoot = $env:CETUS_TEST_ROOT
    $previousTestNode = $env:CETUS_TEST_NODE_EXE
    $env:CETUS_TEST_ROOT = $testRun
    $env:CETUS_TEST_NODE_EXE = $runtime.NodeExe
    try {
        Invoke-LockedRestore -LogPath (Join-Path $testRun "01-restore.log")
        Invoke-DotNet -Arguments @("format", $solution, "--verify-no-changes", "--no-restore", "-v", "minimal") `
            -LogPath (Join-Path $testRun "02-format.log")
        Invoke-DotNet -Arguments @("build", $solution, "-c", "Release", "--no-restore", "-v", "minimal") `
            -LogPath (Join-Path $testRun "03-build.log")
        Invoke-DotNet -Arguments @("test", $solution, "-c", "Release", "--no-build", "--no-restore", "-v", "minimal") `
            -LogPath (Join-Path $testRun "04-tests.log")
        Remove-OwnedDevDirectory $testRun
        Write-Host "Full check passed." -ForegroundColor Green
    }
    catch {
        Write-Host "Check failed. Test artifacts retained at: $testRun" -ForegroundColor Red
        Get-ChildItem -LiteralPath $testRun -Filter *.log -ErrorAction SilentlyContinue |
            ForEach-Object {
                Write-Host "--- tail: $($_.Name)"
                Get-Content -LiteralPath $_.FullName -Tail 30
            }
        throw
    }
    finally {
        $env:CETUS_TEST_ROOT = $previousTestRoot
        $env:CETUS_TEST_NODE_EXE = $previousTestNode
    }
}

function Invoke-Smoke {
    $runtime = Initialize-CetusRuntime
    $testRun = New-TestRun
    $artifacts = Join-Path $testRun "artifacts"
    $smokeApp = Join-Path $artifacts "bin\Cetus.Desktop\debug\Cetus.exe"
    try {
        Invoke-LockedRestore -Target $project -ArtifactsPath $artifacts
        Invoke-DotNet -Arguments @(
            "build", $project, "-c", "Debug", "--no-restore", "--artifacts-path", $artifacts, "-v", "minimal"
        )
        & (Join-Path $PSScriptRoot "app-smoke.ps1") -ApplicationPath $smokeApp -RuntimeMode Explicit `
            -NodeExe $runtime.NodeExe -DshEntry $runtime.DshEntry -TestRoot $testRun
        if ($LASTEXITCODE -ne 0) { throw "Desktop smoke failed with exit code $LASTEXITCODE." }
        Remove-OwnedDevDirectory $testRun
    }
    catch {
        Write-Host "Smoke artifacts retained at: $testRun" -ForegroundColor Red
        $latestLog = Get-ChildItem -LiteralPath (Join-Path $testRun "logs") -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latestLog) { Get-Content -LiteralPath $latestLog.FullName -Tail 50 }
        throw
    }
}

function Invoke-Reset {
    if ($All) {
        Assert-OwnedDevPath $layout.DevRoot
        Get-ChildItem -LiteralPath (Join-Path $layout.DevRoot "profiles") -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-ProfileProcess (Get-ProfileLayout $_.Name) }
        if (Test-Path -LiteralPath $layout.DevRoot) {
            Remove-OwnedDevDirectory $layout.DevRoot
        }
        Write-Host "Removed repository development state: $($layout.DevRoot)"
        return
    }

    $profileLayout = Get-ProfileLayout $Profile
    Assert-OwnedDevPath $profileLayout.Root
    Stop-ProfileProcess $profileLayout
    if (Test-Path -LiteralPath $profileLayout.Root) {
        Remove-OwnedDevDirectory $profileLayout.Root
    }
    Write-Host "Removed profile '$Profile': $($profileLayout.Root)"
}

switch ($Command) {
    "doctor" { Invoke-Doctor }
    "bootstrap" { Initialize-CetusRuntime -Force:$Force | Out-Null }
    "run" { Invoke-Run }
    "test" { Invoke-FastTests }
    "check" { Invoke-Check }
    "smoke" { Invoke-Smoke }
    "reset" { Invoke-Reset }
}
