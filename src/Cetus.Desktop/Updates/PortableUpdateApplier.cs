using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>
/// Stages a portable bundle and writes an independent PowerShell takeover
/// process that preserves user files and rolls back unhealthy updates.
/// </summary>
internal static class PortableUpdateApplier
{
    internal const string ManagedFilesManifestName = ".cetus-managed-files.json";
    internal static string FailureNoticePath => Path.Combine(
        CetusPaths.UpdateCacheDirectory,
        "portable-update-failure.txt");

    public static string PrepareStaging(string zipPath, Version version)
    {
        string staging = Path.Combine(CetusPaths.UpdateCacheDirectory, $"staging-{version.ToString(3)}");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        if (!File.Exists(Path.Combine(staging, "Cetus.exe")))
        {
            throw new InvalidOperationException("便携更新包中没有 Cetus.exe，已取消升级。");
        }

        return staging;
    }

    public static string WriteApplyScript(
        string stagingDirectory,
        string targetDirectory,
        int processId,
        string? zipPath = null)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            throw new InvalidOperationException("便携更新 staging 目录不存在。");
        }

        Directory.CreateDirectory(CetusPaths.UpdateCacheDirectory);
        string token = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(CetusPaths.UpdateCacheDirectory, $"apply-update-{token}.ps1");
        string backupPath = Path.Combine(CetusPaths.UpdateCacheDirectory, $"backup-{token}");
        string healthPath = Path.Combine(CetusPaths.UpdateCacheDirectory, $"update-health-{token}.ready");
        string logPath = Path.Combine(CetusPaths.UpdateCacheDirectory, "apply-update.log");

        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $staging = {{Ps(stagingDirectory)}}
            $target = {{Ps(targetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}}
            $oldPid = {{processId}}
            $archive = {{Ps(zipPath ?? string.Empty)}}
            $backup = {{Ps(backupPath)}}
            $health = {{Ps(healthPath)}}
            $log = {{Ps(logPath)}}
            $failureNotice = {{Ps(FailureNoticePath)}}
            $manifestName = '{{ManagedFilesManifestName}}'

            function Write-UpdateLog([string]$message) {
                try {
                    Add-Content -LiteralPath $log -Value ("{0:yyyy-MM-dd HH:mm:ss.fff} {1}" -f (Get-Date), $message) -Encoding UTF8
                } catch { }
            }

            function Read-ManagedFiles([string]$root) {
                $path = Join-Path $root $manifestName
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return @() }
                $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
                return @($document.files | ForEach-Object { [string]$_ })
            }

            function Safe-RelativePath([string]$relative) {
                if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) { return $false }
                $full = [IO.Path]::GetFullPath((Join-Path $target $relative))
                $root = [IO.Path]::GetFullPath($target).TrimEnd('\') + '\'
                return $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
            }

            function Remove-ManagedFiles([string[]]$files) {
                Remove-Item -LiteralPath (Join-Path $target 'runtime') -Recurse -Force -ErrorAction SilentlyContinue
                foreach ($relative in $files) {
                    if (-not (Safe-RelativePath $relative) -or $relative -like 'runtime/*' -or $relative -like 'runtime\*') { continue }
                    Remove-Item -LiteralPath (Join-Path $target $relative) -Force -ErrorAction SilentlyContinue
                }
            }

            function Restore-Backup {
                if (-not (Test-Path -LiteralPath $backup)) { return }
                Get-ChildItem -LiteralPath $backup -Force | Copy-Item -Destination $target -Recurse -Force
                Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
            }

            $newFiles = @(Read-ManagedFiles $staging)
            if ($newFiles.Count -eq 0) {
                $newFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
                    $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
                })
            }
            $oldFiles = @(Read-ManagedFiles $target)
            $backupFiles = if ($oldFiles.Count -gt 0) { $oldFiles } else { $newFiles }
            $newProcess = $null
            $copyStarted = $false

            try {
                Remove-Item -LiteralPath $failureNotice -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $health -Force -ErrorAction SilentlyContinue
                Write-UpdateLog "Waiting for old CETUS process $oldPid."
                for ($attempt = 0; $attempt -lt 90; $attempt++) {
                    if (-not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Seconds 1
                }
                if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {
                    Stop-Process -Id $oldPid -Force -ErrorAction Stop
                    (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
                }

                New-Item -ItemType Directory -Path $backup -Force | Out-Null
                $oldRuntime = Join-Path $target 'runtime'
                if (Test-Path -LiteralPath $oldRuntime) {
                    Move-Item -LiteralPath $oldRuntime -Destination (Join-Path $backup 'runtime') -Force
                }
                foreach ($relative in $backupFiles) {
                    if (-not (Safe-RelativePath $relative) -or $relative -like 'runtime/*' -or $relative -like 'runtime\*') { continue }
                    $source = Join-Path $target $relative
                    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
                    $destination = Join-Path $backup $relative
                    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
                    Move-Item -LiteralPath $source -Destination $destination -Force
                }

                $copyStarted = $true
                Get-ChildItem -LiteralPath $staging -Force | Copy-Item -Destination $target -Recurse -Force
                $exe = Join-Path $target 'Cetus.exe'
                if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'New package did not install Cetus.exe.' }
                Write-UpdateLog 'Copy completed; launching the new version.'
                $newProcess = Start-Process -FilePath $exe -ArgumentList ('--update-health="' + $health + '"') -PassThru

                $healthy = $false
                for ($attempt = 0; $attempt -lt 180; $attempt++) {
                    if (Test-Path -LiteralPath $health -PathType Leaf) { $healthy = $true; break }
                    if ($newProcess.HasExited) { throw "New CETUS process exited early with code $($newProcess.ExitCode)." }
                    Start-Sleep -Milliseconds 500
                    $newProcess.Refresh()
                }
                if (-not $healthy) { throw 'New CETUS did not become healthy within 90 seconds.' }

                Write-UpdateLog 'Portable update completed successfully.'
                Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
                if ($archive) { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath $health -Force -ErrorAction SilentlyContinue
            }
            catch {
                $reason = $_.Exception.Message
                Write-UpdateLog "Portable update failed: $reason"
                if ($newProcess -and -not $newProcess.HasExited) {
                    Stop-Process -Id $newProcess.Id -Force -ErrorAction SilentlyContinue
                }
                try {
                    if ($copyStarted) { Remove-ManagedFiles $newFiles }
                    Restore-Backup
                } catch {
                    Write-UpdateLog "Rollback encountered an additional error: $($_.Exception.Message)"
                }
                $notice = "新版本升级失败，已恢复旧版本。详情：$reason"
                try { Set-Content -LiteralPath $failureNotice -Value $notice -Encoding UTF8 } catch { }
                $oldExe = Join-Path $target 'Cetus.exe'
                if (Test-Path -LiteralPath $oldExe -PathType Leaf) {
                    try { Start-Process -FilePath $oldExe -ErrorAction Stop } catch {
                        Write-UpdateLog "Unable to restart the old CETUS version: $($_.Exception.Message)"
                    }
                }
                exit 1
            }
            finally {
                if (Test-Path -LiteralPath $health) { Remove-Item -LiteralPath $health -Force -ErrorAction SilentlyContinue }
            }

            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
        File.WriteAllText(scriptPath, script.ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
        return scriptPath;
    }

    public static void LaunchApplyScript(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        using Process? process = Process.Start(startInfo);
    }

    private static string Ps(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
