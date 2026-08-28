using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cetus.DshStatus;

namespace Cetus.Sidebar;

/// <summary>
/// Status tab: project/workspace context, cumulative token usage and call
/// counts from the DSH host, and the DeepSeek platform balance. Polls while
/// visible; the balance is only queried on demand (activation or manual).
/// </summary>
public partial class StatusTabContent : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly DshStatusClient _client = new();
    private Func<Uri>? _endpointProvider;
    private DispatcherTimer? _pollTimer;
    private bool _refreshing;
    private bool _disposed;

    public StatusTabContent()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ = RefreshAsync(includeBalance: true);
            StartPolling();
        };
        Unloaded += (_, _) => _pollTimer?.Stop();
    }

    /// <summary>Provides the live DSH endpoint (follows port changes).</summary>
    public void SetEndpointProvider(Func<Uri> provider) => _endpointProvider = provider;

    public void ApplyTheme(bool isDark)
    {
        // All colors ride DynamicResource brushes; nothing to redo here.
    }

    private void StartPolling()
    {
        if (_pollTimer is not null)
        {
            return;
        }

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) =>
        {
            if (IsVisible)
            {
                _ = RefreshAsync(includeBalance: false);
            }
        };
        _pollTimer.Start();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) =>
        _ = RefreshAsync(includeBalance: true);

    private async Task RefreshAsync(bool includeBalance)
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        if (_endpointProvider is null)
        {
            WorkspaceTitleText.Text = "未连接 DSH";
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            Uri endpoint = _endpointProvider();
            DshStatusSnapshot snapshot = await _client.GetStatusAsync(endpoint, CancellationToken.None);
            if (_disposed)
            {
                return;
            }

            if (snapshot.Workspace is { } workspace)
            {
                WorkspaceTitleText.Text = workspace.Title;
                WorkspacePathText.Text = workspace.Path;
                WorkspaceMetaText.Text =
                    $"{workspace.SessionCount} 个会话 · 最近活动 {workspace.UpdatedAt:MM-dd HH:mm}";
            }
            else
            {
                WorkspaceTitleText.Text = "没有工作区";
                WorkspacePathText.Text = string.Empty;
                WorkspaceMetaText.Text = string.Empty;
            }

            ModelText.Text = string.IsNullOrWhiteSpace(snapshot.Model)
                ? "—"
                : $"{snapshot.Model}（{snapshot.Provider ?? "未知来源"}）";

            DshUsageSummary usage = snapshot.Usage;
            InputTokensText.Text = usage.InputTokens.ToString("N0");
            OutputTokensText.Text = usage.OutputTokens.ToString("N0");
            CacheReadText.Text = usage.CacheReadTokens.ToString("N0");
            CacheWriteText.Text = usage.CacheWriteTokens.ToString("N0");
            SessionsText.Text = usage.SessionCount.ToString("N0");
            TurnsText.Text = usage.Turns.ToString("N0");
            StepsText.Text = usage.Steps.ToString("N0");

            if (includeBalance)
            {
                await RefreshBalanceAsync();
            }

            UpdatedText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                UpdatedText.Text = $"刷新失败：{error.Message}";
            }
        }
        finally
        {
            _refreshing = false;
            if (!_disposed)
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task RefreshBalanceAsync()
    {
        string? apiKey = DshCredentials.ReadApiKey();
        if (apiKey is null)
        {
            BalanceText.Text = "未接入";
            BalanceDetailText.Text = "未找到 DEEPSEEK_API_KEY，无法查询平台余额。";
            BalanceDetailText.Visibility = Visibility.Visible;
            return;
        }

        DeepSeekBalance? balance = await _client.GetBalanceAsync(
            apiKey,
            DshCredentials.DefaultBaseUrl,
            CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        if (balance is null)
        {
            BalanceText.Text = "不可用";
            BalanceDetailText.Text = "余额接口未返回数据（非 DeepSeek 官方平台 Key 时属预期）。";
            BalanceDetailText.Visibility = Visibility.Visible;
            return;
        }

        BalanceText.Text = $"{balance.TotalBalance.ToString("0.##")} {balance.Currency}";
        BalanceDetailText.Text = balance.IsAvailable
            ? $"账户可用 · 赠送余额 {balance.GrantedBalance.ToString("0.##")} {balance.Currency}"
            : "账户暂不可用";
        BalanceDetailText.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer?.Stop();
        _client.Dispose();
    }
}
