using System.Windows.Controls;
using System.Windows.Threading;
using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class FilesTabContentTests
{
    [Fact]
    public Task WorkspaceSwitch_DiscardsPendingPreviewAndOutOfOrderRoot() => RunOnDispatcherAsync(async () =>
    {
        string root = TestWorkspace.CreateDirectory();
        try
        {
            var application = new System.Windows.Application();
            application.Resources["SidebarIconButton"] = new System.Windows.Style(typeof(Button));
            string first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            using var view = new FilesTabContent();
            var preview = new TaskCompletionSource<FilePreviewResult>();
            CancellationToken previewToken = default;
            view.PreviewLoader = (_, token) => { previewToken = token; return preview.Task; };
            Task pendingPreview = view.LoadPreviewAsync(Path.Combine(first, "old.txt"));
            var oldWorkspace = new TaskCompletionSource<string?>();
            view.WorkspaceResolver = _ => oldWorkspace.Task;
            Task oldRefresh = view.RefreshWorkspaceAsync();
            view.WorkspaceResolver = _ => Task.FromResult<string?>(second);
            await view.RefreshWorkspaceAsync();
            oldWorkspace.SetResult(first);
            await oldRefresh;
            preview.SetResult(new FilePreviewResult(FilePreviewContent.Text,
                [new FilePreviewLine("STALE")], null, "", false, 5));
            await pendingPreview;

            Assert.True(previewToken.IsCancellationRequested);
            Assert.Equal(second, ((TextBox)view.FindName("PathBox")).Text);
            Assert.Null(((ItemsControl)view.FindName("PreviewLines")).ItemsSource);
        }
        finally
        {
            if (!TestWorkspace.RetainArtifacts)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    });

    private static Task RunOnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
