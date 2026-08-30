using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class SidebarFileNodeTests
{
    [Fact]
    public void LoadChildren_ListsDirectoriesBeforeFiles()
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            string directory = Path.Combine(root, "folder");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(root, "note.txt"), "CETUS");
            var node = new SidebarFileNode(root, isDirectory: true);

            node.LoadChildren();

            Assert.Collection(
                node.Children,
                item =>
                {
                    Assert.True(item.IsDirectory);
                    Assert.Equal("folder", item.Name);
                },
                item =>
                {
                    Assert.False(item.IsDirectory);
                    Assert.Equal("note.txt", item.Name);
                });
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsExpanded_NotifiesPropertyChanged()
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            var node = new SidebarFileNode(root, isDirectory: true);
            var changes = new List<string?>();
            node.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            node.IsExpanded = true;
            node.IsExpanded = true; // No duplicate notification for the same value.
            node.IsExpanded = false;

            Assert.Equal(2, changes.Count);
            Assert.All(changes, name => Assert.Equal(nameof(SidebarFileNode.IsExpanded), name));
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
