using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cetus.Configuration;
using Cetus.Terminal;
using Microsoft.Web.WebView2.Core;

namespace Cetus.Sidebar;

/// <summary>
/// One terminal tab: a ConPTY-backed PowerShell session rendered by xterm.js
/// inside an isolated WebView2 page. Each tab owns its session; closing the
/// tab ends that shell.
/// </summary>
public partial class TerminalTabContent : UserControl, IDisposable
{
    private const string AssetHost = "cetus.terminal";
    private const string TerminalPage = "https://cetus.terminal/terminal.html";

    private static Task<CoreWebView2Environment>? _environmentTask;

    private ConPtySession? _session;
    private bool _sessionEnded;
    private bool _initialized;
    private bool _disposed;

    public TerminalTabContent()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = EnsureInitializedAsync();
    }

    public void ApplyTheme(bool isDark)
    {
        // The terminal keeps a fixed dark palette in both themes.
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environmentTask is not null)
        {
            return _environmentTask;
        }

        string userData = Path.Combine(
            CetusPaths.WebView2UserDataDirectory,
            "TerminalWebView2");
        _environmentTask = CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userData);
        return _environmentTask;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        try
        {
            CoreWebView2Environment environment = await GetEnvironmentAsync();
            await TabWeb.EnsureCoreWebView2Async(environment);
            if (_disposed)
            {
                return;
            }

            CoreWebView2 core = TabWeb.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            string assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            core.SetVirtualHostNameToFolderMapping(
                AssetHost,
                assetsFolder,
                CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.Navigate(TerminalPage);
        }
        catch (Exception error)
        {
            StatusText.Text = $"终端初始化失败：{error.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void StartSession(short columns, short rows)
    {
        _session?.Dispose();
        _sessionEnded = false;
        _session = ConPtySession.Start(
            "powershell.exe -NoLogo -NoProfile -NoExit -Command \"chcp 65001 > $null | Out-Null\"",
            columns,
            rows,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OnPtyOutput);
        _session.Exited += OnPtyExited;
    }

    private void OnPtyOutput(string chunk)
    {
        if (_disposed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_disposed && TabWeb.CoreWebView2 is { } core)
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(
                    new { type = "pty-output", data = chunk }));
            }
        });
    }

    private void OnPtyExited(object? sender, EventArgs e)
    {
        _sessionEnded = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_disposed && TabWeb.CoreWebView2 is { } core)
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(
                    new { type = "pty-output", data = "\r\n\u001b[33m[会话已退出]\u001b[0m\r\n" }));
            }
        });
    }

    private static void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!string.Equals(e.Uri, TerminalPage, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_sessionEnded
            || !string.Equals(e.Source, TerminalPage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = message.RootElement;
            string type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (type == "pty-ready"
                && _session is null
                && root.TryGetProperty("cols", out var readyColsElement)
                && root.TryGetProperty("rows", out var readyRowsElement))
            {
                short cols = (short)Math.Clamp(readyColsElement.GetInt32(), 2, 500);
                short rows = (short)Math.Clamp(readyRowsElement.GetInt32(), 2, 300);
                StartSession(cols, rows);
            }
            else if (type == "pty-input"
                && root.TryGetProperty("data", out var dataElement)
                && dataElement.ValueKind == JsonValueKind.String
                && _session is { } session)
            {
                session.Write(dataElement.GetString() ?? string.Empty);
            }
            else if (type == "pty-resize"
                && root.TryGetProperty("cols", out var colsElement)
                && root.TryGetProperty("rows", out var rowsElement)
                && _session is { } resizeSession)
            {
                short cols = (short)Math.Clamp(colsElement.GetInt32(), 2, 500);
                short rows = (short)Math.Clamp(rowsElement.GetInt32(), 2, 300);
                resizeSession.Resize(cols, rows);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from the renderer page.
        }
        catch (Exception error)
        {
            StatusText.Text = $"终端操作失败：{error.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is { } session)
        {
            session.OutputReceived -= OnPtyOutput;
            session.Exited -= OnPtyExited;
            session.Dispose();
            _session = null;
        }

        if (TabWeb.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
        }

        TabWeb.Dispose();
    }
}
