using System.IO;
using System.Text.Json;

namespace Cetus.Configuration;

public sealed record RecentWorkspace(string Path, string Title, DateTimeOffset LastUsed);

/// <summary>
/// Persisted list of recently opened workspace directories (newest first,
/// deduplicated by normalized path, capped). Pure list operations are static
/// so they can be tested without touching the filesystem; persistence follows
/// the same tolerant-load / atomic-write pattern as CetusSettings.
/// </summary>
public sealed class RecentWorkspaces
{
    public const int MaxEntries = 10;

    private readonly string _filePath;
    private List<RecentWorkspace> _entries;

    public RecentWorkspaces(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    public IReadOnlyList<RecentWorkspace> Entries => _entries;

    /// <summary>Moves <paramref name="workspacePath"/> to the front (adding it when new).</summary>
    public RecentWorkspace Add(string workspacePath)
    {
        string normalized = NormalizePath(workspacePath);
        RecentWorkspace entry = new(
            normalized,
            Path.GetFileName(normalized) is { Length: > 0 } title ? title : normalized,
            DateTimeOffset.UtcNow);

        _entries = Promote(_entries, entry, MaxEntries);
        Persist();
        return entry;
    }

    /// <summary>Full path with separators trimmed and casing canonicalized where the OS allows.</summary>
    public static string NormalizePath(string workspacePath)
    {
        string trimmed = workspacePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            return System.IO.Path.GetFullPath(trimmed);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return trimmed;
        }
    }

    public static List<RecentWorkspace> Promote(
        IReadOnlyList<RecentWorkspace> entries,
        RecentWorkspace entry,
        int maxEntries)
    {
        var result = new List<RecentWorkspace>(maxEntries) { entry };
        foreach (RecentWorkspace existing in entries)
        {
            if (!string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(existing);
            }
        }

        return result.Count > maxEntries ? result.Take(maxEntries).ToList() : result;
    }

    private static List<RecentWorkspace> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            RecentWorkspaceFile? file = JsonSerializer.Deserialize<RecentWorkspaceFile>(File.ReadAllText(filePath));
            if (file?.Entries is null)
            {
                return [];
            }

            var loaded = new List<RecentWorkspace>();
            foreach (RecentWorkspaceFile.Entry entry in file.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                string path = entry.Path;
                string title = !string.IsNullOrWhiteSpace(entry.Title)
                    ? entry.Title
                    : Path.GetFileName(path) is { Length: > 0 } fileName ? fileName : path;
                DateTimeOffset lastUsed = entry.LastUsedUnixMs is long milliseconds && milliseconds > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                    : DateTimeOffset.MinValue;
                loaded.Add(new RecentWorkspace(path, title, lastUsed));
            }

            return loaded;
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist()
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("最近工作区文件路径必须包含目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp";
        string json = JsonSerializer.Serialize(new RecentWorkspaceFile
        {
            Entries = _entries
                .Select(entry => new RecentWorkspaceFile.Entry
                {
                    Path = entry.Path,
                    Title = entry.Title,
                    LastUsedUnixMs = entry.LastUsed.ToUnixTimeMilliseconds(),
                })
                .ToList(),
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private sealed class RecentWorkspaceFile
    {
        public List<Entry>? Entries { get; set; }

        public sealed class Entry
        {
            public string? Path { get; set; }
            public string? Title { get; set; }
            public long? LastUsedUnixMs { get; set; }
        }
    }
}
