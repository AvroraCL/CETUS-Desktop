using System.Collections.ObjectModel;
using System.IO;

namespace Cetus.Sidebar;

internal sealed class SidebarFileNode
{
    private static readonly SidebarFileNode Placeholder = new();
    private bool _childrenLoaded;

    private SidebarFileNode()
    {
        Name = string.Empty;
        FullPath = string.Empty;
    }

    public SidebarFileNode(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (Name.Length == 0)
        {
            Name = fullPath;
        }

        if (isDirectory)
        {
            Children.Add(Placeholder);
        }
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool IsPlaceholder => ReferenceEquals(this, Placeholder);

    /// <summary>Fluent icon kind reflecting the file type (folder/file group).</summary>
    public string IconKind
    {
        get
        {
            if (IsDirectory)
            {
                return "Folder";
            }

            return Path.GetExtension(FullPath).ToLowerInvariant() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" => "Image",
                ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" => "MusicNote",
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "Video",
                ".zip" or ".7z" or ".rar" or ".gz" or ".tar" => "FolderZip",
                ".csv" or ".xlsx" or ".xls" => "Table",
                ".txt" or ".md" or ".log" or ".ini" or ".cfg" => "TextDescription",
                ".json" or ".xml" or ".yml" or ".yaml" => "Braces",
                ".js" or ".ts" or ".jsx" or ".tsx" or ".py" or ".cs" or ".cpp" or ".c"
                    or ".h" or ".java" or ".rs" or ".go" or ".rb" or ".php" or ".html"
                    or ".htm" or ".css" or ".sql" or ".ps1" or ".bat" or ".sh" => "Code",
                _ => "Document",
            };
        }
    }

    public ObservableCollection<SidebarFileNode> Children { get; } = [];

    public void LoadChildren()
    {
        if (!IsDirectory || _childrenLoaded)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        try
        {
            var directory = new DirectoryInfo(FullPath);
            IEnumerable<SidebarFileNode> nodes = directory
                .EnumerateDirectories()
                .Where(item => !item.Attributes.HasFlag(FileAttributes.System))
                .Select(item => new SidebarFileNode(item.FullName, isDirectory: true))
                .Concat(directory
                    .EnumerateFiles()
                    .Select(item => new SidebarFileNode(item.FullName, isDirectory: false)))
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (SidebarFileNode node in nodes)
            {
                Children.Add(node);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // Inaccessible folders remain empty and do not break the whole tree.
        }
    }
}
