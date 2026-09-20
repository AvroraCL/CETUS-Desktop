using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Cetus.Configuration;

namespace Cetus.Hosting;

/// <summary>
/// Remembers the DSH sidecar Cetus spawned last, so a later start can tell a
/// service the user runs from a sidecar orphaned by a previous Cetus run.
/// Cetus owns its host through a Windows Job Object with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (see <see cref="SidecarJob"/>): when a
/// Cetus process ends without stopping its host, that host is already doomed.
/// Adopting such an endpoint means adopting a process that is about to die,
/// which the health monitor would then report as a restart a few seconds later.
/// </summary>
internal static class HostOwnerState
{
    private static readonly object Gate = new();

    internal static string FilePath => Path.Combine(
        CetusPaths.UserDataDirectory,
        "dsh-host-owner.json");

    private sealed record Record(int ProcessId, DateTimeOffset StartedAt, DateTimeOffset ObservedAt);

    public static void Write(int processId, DateTimeOffset startedAt)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(CetusPaths.UserDataDirectory);
                File.WriteAllText(
                    FilePath,
                    JsonSerializer.Serialize(
                        new Record(processId, startedAt, DateTimeOffset.UtcNow)),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Ownership tracking is advisory: failing to record only means the
            // next start cannot prove ownership and falls back on liveness.
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Recorded sidecar pid, or null when nothing was recorded.</summary>
    public static int? ReadProcessId() => Read()?.ProcessId;

    /// <summary>
    /// The recorded sidecar. The record is dropped when its start time no
    /// longer matches the live process, which means the pid was recycled.
    /// </summary>
    public static int? ReadVerifiedProcessId(out string? detail)
    {
        detail = null;
        Record? record = Read();
        if (record is null)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited)
            {
                detail = $"记录的宿主进程 pid={record.ProcessId} 已退出";
                return null;
            }

            if (record.StartedAt != default)
            {
                DateTimeOffset actual;
                try
                {
                    actual = process.StartTime;
                }
                catch (InvalidOperationException)
                {
                    actual = DateTimeOffset.MinValue;
                }

                if (actual != default
                    && Math.Abs((actual - record.StartedAt).TotalSeconds) > 1)
                {
                    detail = $"记录的宿主进程 pid={record.ProcessId} 已被其他进程复用";
                    return null;
                }
            }

            detail = $"记录的宿主进程 pid={record.ProcessId} 仍在运行";
            return record.ProcessId;
        }
        catch (ArgumentException)
        {
            detail = $"记录的宿主进程 pid={record.ProcessId} 已不存在";
            return null;
        }
        catch (InvalidOperationException)
        {
            detail = $"记录的宿主进程 pid={record.ProcessId} 无法查询";
            return null;
        }
    }

    private static Record? Read()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<Record>(File.ReadAllText(FilePath));
            }
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }
}
