using System.IO;
using System.Windows.Shell;
using Cetus.Configuration;

namespace Cetus.Platform;

/// <summary>
/// Rebuilds the taskbar Jump List from the recent-workspaces list. Each entry
/// is a JumpTask that launches Cetus.exe with the directory as an argument;
/// the second process then forwards the request to the running instance over
/// the activation channel.
/// </summary>
internal static class JumpListController
{
    public static void Apply(IReadOnlyList<RecentWorkspace> entries)
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var jumpList = new JumpList();
        foreach (RecentWorkspace entry in entries)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = entry.Title,
                Description = entry.Path,
                ApplicationPath = executablePath,
                Arguments = $"\"{entry.Path}\"",
                WorkingDirectory = entry.Path,
                IconResourcePath = executablePath,
            });
        }

        JumpList.SetJumpList(System.Windows.Application.Current, jumpList);
        try
        {
            jumpList.Apply();
        }
        catch (IOException)
        {
            // Shell integration is best-effort; the tray menu still works.
        }
    }
}
