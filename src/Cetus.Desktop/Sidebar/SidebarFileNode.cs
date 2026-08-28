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

    public string Glyph => IsDirectory ? "\uE8B7" : "\uE7C3";

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
