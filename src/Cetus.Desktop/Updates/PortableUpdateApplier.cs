using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>
/// Applies a downloaded portable zip to a running portable install. The
/// bundle is extracted into a staging folder next to the update cache and a
/// self-deleting cmd script takes over after CETUS exits: it waits for the
/// running process to die, mirrors the staging tree over the install
/// directory (a full mirror also clears Node packages the new release no
/// longer contains), relaunches CETUS and removes itself.
/// </summary>
internal static class PortableUpdateApplier
{
    public static string PrepareStaging(string zipPath, Version version)
    {
        string staging = Path.Combine(
            CetusPaths.UpdateCacheDirectory,
            $"staging-{version.ToString(3)}");
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

    /// <summary>Writes the takeover script and returns its path.</summary>
    public static string WriteApplyScript(string stagingDirectory, string targetDirectory, int processId)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            throw new InvalidOperationException("便携更新 staging 目录不存在。");
        }

        string scriptPath = Path.Combine(
            CetusPaths.UpdateCacheDirectory,
            $"apply-update-{processId}.cmd");
        string logPath = Path.Combine(CetusPaths.UpdateCacheDirectory, "apply-update.log");
        string script = $"""
            @echo off
            setlocal
            set /a tries=0
            :wait
            timeout /t 1 /nobreak >nul
            tasklist /FI "PID eq {processId}" 2>nul | find "{processId}" >nul
            if errorlevel 1 goto apply
            set /a tries+=1
            if %tries%==20 taskkill /PID {processId} /F >nul 2>&1
            if %tries% GEQ 40 goto apply
            goto wait
            :apply
            robocopy "{stagingDirectory}" "{targetDirectory}" /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS > "{logPath}"
            if errorlevel 8 exit /b 1
            start "" "{targetDirectory}\Cetus.exe"
            del "%~f0"
            """;
        File.WriteAllText(scriptPath, script.ReplaceLineEndings("\r\n"));
        return scriptPath;
    }

    /// <summary>Hands control to the takeover script; CETUS must exit right after.</summary>
    public static void LaunchApplyScript(string scriptPath)
    {
        using Process? script = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
