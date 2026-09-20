using System.Diagnostics;
using System.IO;
using System.Windows;
using Cetus.Configuration;
using Cetus.Platform;
using Cetus.Runtime;
using Cetus.Updates;

namespace Cetus;

/// <summary>
/// Safe-mode diagnostics: the failure report panel shown when automatic
/// DSH recovery is exhausted, plus the diagnostics export entry points.
/// </summary>
public partial class MainWindow
{
    private string? _diagnosticsReport;

    /// <summary>Safe-mode view: replaces the bare status line with a full failure report.</summary>
    private void ShowDiagnostics(DesktopRuntimeState state)
    {
        if (state.Error is not { } error)
        {
            return;
        }

        StatusText.Visibility = Visibility.Collapsed;
        DiagnosticsPanel.Visibility = Visibility.Visible;
        DiagnosticsSummary.Text = error.Message;

        int port = _runtime.Endpoint.Port;
        string report = DiagnosticsCollector.BuildFailureReport(
            error.Message,
            _runtime.HostLogPath,
            port,
            UpdateCoordinator.ReadCurrentVersion().ToString());
        string logTail = DiagnosticsCollector.ReadLogTail(
            _runtime.HostLogPath,
            CetusPaths.LogDirectory);
        _diagnosticsReport = $"{report}{Environment.NewLine}{Environment.NewLine}日志尾部{Environment.NewLine}{logTail}";
        DiagnosticsDetail.Text = _diagnosticsReport;
        DiagnosticsDetail.ScrollToEnd();
    }

    private void OnDiagnosticsRetryClicked(object sender, RoutedEventArgs e) =>
        _ = RetryDshAsync();

    private void OnDiagnosticsOpenLogsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CetusPaths.LogDirectory}\""));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Shell launch is best effort.
        }
    }

    private void OnDiagnosticsCopyClicked(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsReport is not { } report)
        {
            return;
        }

        try
        {
            Clipboard.SetText(report);
            _tray?.ShowBalloonTip("CETUS · 诊断信息", "诊断信息已复制到剪贴板。");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be momentarily held by another process.
        }
    }

    private void OnDiagnosticsExportClicked(object sender, RoutedEventArgs e) =>
        _ = ExportDiagnosticsAsync();

    private async Task ExportDiagnosticsAsync()
    {
        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Filter = "诊断包 (*.zip)|*.zip",
            FileName = $"cetus-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            string report = _diagnosticsReport
                ?? DiagnosticsCollector.BuildFailureReport(
                    "手动导出",
                    _runtime.HostLogPath,
                    _runtime.Endpoint.Port,
                    UpdateCoordinator.ReadCurrentVersion().ToString());
            string appDirectory = AppContext.BaseDirectory;
            string runtimeVersions = Path.Combine(appDirectory, "runtime", "VERSIONS.txt");
            DiagnosticsCollector.BuildArchive(
                dialog.FileName,
                CetusPaths.SettingsFile,
                CetusPaths.LogDirectory,
                File.Exists(runtimeVersions) ? runtimeVersions : null,
                report);
            _tray?.ShowBalloonTip("CETUS · 诊断包", $"已导出：{dialog.FileName}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _tray?.ShowBalloonTip("CETUS · 导出失败", error.Message);
        }
    }

    private void OnDiagnosticsExitClicked(object sender, RoutedEventArgs e) => ExitApplication();
}
