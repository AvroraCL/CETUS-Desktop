using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>
/// Notice the portable-update takeover script leaves behind when a new build
/// failed to start and the previous one was restored. The payload is JSON so
/// both sides (PowerShell writer, C# reader) agree on the version.
/// </summary>
public sealed record PortableUpdateFailureNotice(string? Version, string? Reason)
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public static PortableUpdateFailureNotice? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string content = File.ReadAllText(path).Trim();
            if (content.Length == 0)
            {
                return null;
            }

            if (!content.StartsWith('{'))
            {
                // Legacy plain-text notice written by earlier builds.
                return new PortableUpdateFailureNotice(null, content);
            }

            // The takeover script writes camelCase keys ("version", "reason"),
            // so matching is case-insensitive.
            return JsonSerializer.Deserialize<PortableUpdateFailureNotice>(content, CaseInsensitive)
                ?? new PortableUpdateFailureNotice(null, content);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Remembers versions that already failed to start so the silent startup
/// updater does not reinstall them in a loop. Without this, a rollback
/// relaunches the previous build, that build immediately re-detects the same
/// release, downloads it again and kills DSH once more — every cycle.
/// The marker is keyed by version: a newer release is always attempted.
/// </summary>
public static class UpdateRejection
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        CetusPaths.UpdateCacheDirectory,
        "rejected-versions.txt");

    public static bool IsRejected(Version version)
    {
        try
        {
            lock (Gate)
            {
                return File.Exists(FilePath)
                    && File.ReadLines(FilePath)
                        .Select(line => line.Trim())
                        .Any(line => line.Length > 0 && Normalize(line) == Normalize(version.ToString(3)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Record(Version version)
    {
        string value = Normalize(version.ToString(3));
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(CetusPaths.UpdateCacheDirectory);
                string existing = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
                if (existing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => Normalize(line) == value))
                {
                    return;
                }

                var builder = new StringBuilder(existing.TrimEnd());
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine(value);
                WriteAtomically(builder.ToString());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Drops a rejection once a manual install of that version succeeds.</summary>
    public static void Clear(Version version)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                string value = Normalize(version.ToString(3));
                string[] kept = File.ReadLines(FilePath)
                    .Where(line => line.Trim().Length > 0 && Normalize(line) != value)
                    .ToArray();
                WriteAtomically(string.Join(Environment.NewLine, kept));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Atomic tmp+rename write with a per-writer unique temp name, matching
    /// the pattern used by settings persistence.
    /// </summary>
    private static void WriteAtomically(string content)
    {
        string temporaryPath = FilePath + ".tmp" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        try
        {
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static string Normalize(string value)
    {
        string trimmed = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out Version? parsed)
            ? parsed.ToString(3)
            : trimmed;
    }
}
