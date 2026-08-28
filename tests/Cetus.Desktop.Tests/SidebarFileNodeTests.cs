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
}
