using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cetus.DshStatus;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Cetus.Sidebar;

/// <summary>
/// Status tab: project/workspace context, token usage with a stacked
/// composition bar, a per-session output bar chart, call totals and the
/// DeepSeek platform balance. Polls while visible; the balance is only
/// queried on demand (activation or manual refresh).
/// </summary>
public partial class StatusTabContent : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly Brush TokenInputBrush = Frozen("#60A5FA");
    private static readonly Brush TokenOutputBrush = Frozen("#34D399");
    private static readonly Brush CacheReadBrush = Frozen("#A78BFA");
    private static readonly Brush CacheWriteBrush = Frozen("#FBBF24");
    private static readonly Brush BalanceGrantedBrush = Frozen("#34D399");
    private static readonly Brush BalanceToppedBrush = Frozen("#60A5FA");
    private static readonly Brush SessionBarBrush = Frozen("#60A5FA");

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
        // All colors ride DynamicResource brushes or fixed chart accents.
    }

    private static Brush Frozen(string color) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) { Opacity = 0.9 };

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

            RenderUsageChart(snapshot.Usage);
            RenderSessionBars(snapshot.Usage);
            SessionsText.Text = snapshot.Usage.SessionCount.ToString("N0");
            TurnsText.Text = snapshot.Usage.Turns.ToString("N0");
            StepsText.Text = snapshot.Usage.Steps.ToString("N0");

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

    private static long TotalTokens(DshUsageSummary usage) =>
        usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;

    private void RenderUsageChart(DshUsageSummary usage)
    {
        long total = TotalTokens(usage);
        TotalTokensText.Text = $"{total:N0} tokens";
        UsageLegend.Children.Clear();
        UsageBarHost.Children.Clear();
        UsageBarHost.ColumnDefinitions.Clear();

        if (total <= 0)
        {
            UsageEmptyText.Visibility = Visibility.Visible;
            UsageBarHost.Visibility = Visibility.Collapsed;
            return;
        }

        UsageEmptyText.Visibility = Visibility.Collapsed;
        UsageBarHost.Visibility = Visibility.Visible;

        var segments = new (string Name, long Value, Brush Brush)[]
        {
            ("输入", usage.InputTokens, TokenInputBrush),
            ("输出", usage.OutputTokens, TokenOutputBrush),
            ("缓存读", usage.CacheReadTokens, CacheReadBrush),
            ("缓存写", usage.CacheWriteTokens, CacheWriteBrush),
        };

        foreach (var (name, value, brush) in segments)
        {
            if (value <= 0)
            {
                continue;
            }

            UsageBarHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(value, GridUnitType.Star),
            });
            var segment = new Border { Background = brush };
            Grid.SetColumn(segment, UsageBarHost.ColumnDefinitions.Count - 1);
            UsageBarHost.Children.Add(segment);

            var dot = new Border { Style = (Style)FindResource("LegendDot"), Background = brush };
            var legendLabel = new TextBlock
            {
                Text = $"{name} {value:N0}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("SidebarPanelSecondaryBrush"),
                Margin = new Thickness(0, 0, 12, 0),
            };
            var legendEntry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            legendEntry.Children.Add(dot);
            legendEntry.Children.Add(legendLabel);
            UsageLegend.Children.Add(legendEntry);
        }
    }

    private void RenderSessionBars(DshUsageSummary usage)
    {
        SessionBarsHost.Children.Clear();
        SessionBarsHost.ColumnDefinitions.Clear();
        var recent = usage.Sessions
            .Where(session => session.UpdatedAt > DateTime.MinValue)
            .OrderBy(session => session.UpdatedAt)
            .TakeLast(8)
            .ToList();

        if (recent.Count == 0)
        {
            SessionBarsEmptyText.Visibility = Visibility.Visible;
            return;
        }

        SessionBarsEmptyText.Visibility = Visibility.Collapsed;
        long max = Math.Max(1, recent.Max(session => session.OutputTokens));
        for (int i = 0; i < recent.Count; i++)
        {
            DshSessionUsage session = recent[i];
            SessionBarsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            double fraction = session.OutputTokens / (double)max;
            var bar = new Border
            {
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = SessionBarBrush,
                Height = Math.Max(4, 90 * fraction),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = $"{session.Title}\n输出 {session.OutputTokens:N0} tokens · {session.UpdatedAt:MM-dd HH:mm}",
            };
            Grid.SetColumn(bar, i);
            SessionBarsHost.Children.Add(bar);
        }
    }

    private async Task RefreshBalanceAsync()
    {
        string? apiKey = DshCredentials.ReadApiKey();
        if (apiKey is null)
        {
            BalanceText.Text = "未接入";
            BalanceBarHost.Visibility = Visibility.Collapsed;
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
            BalanceBarHost.Visibility = Visibility.Collapsed;
            BalanceDetailText.Text = "余额接口未返回数据（非 DeepSeek 官方平台 Key 时属预期）。";
            BalanceDetailText.Visibility = Visibility.Visible;
            return;
        }

        BalanceText.Text = $"{balance.TotalBalance.ToString("0.##")} {balance.Currency}";
        BalanceBarHost.Visibility = Visibility.Visible;
        BalanceBarHost.ColumnDefinitions.Clear();
        BalanceBarHost.Children.Clear();
        if (balance.TotalBalance > 0)
        {
            decimal granted = Math.Clamp(balance.GrantedBalance, 0, balance.TotalBalance);
            decimal topped = balance.TotalBalance - granted;
            BalanceBarHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength((double)granted, GridUnitType.Star),
            });
            BalanceBarHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength((double)topped, GridUnitType.Star),
            });
            if (granted > 0)
            {
                var grantedBar = new Border { Background = BalanceGrantedBrush };
                Grid.SetColumn(grantedBar, 0);
                BalanceBarHost.Children.Add(grantedBar);
            }

            var toppedBar = new Border { Background = BalanceToppedBrush };
            Grid.SetColumn(toppedBar, granted > 0 ? 1 : 0);
            BalanceBarHost.Children.Add(toppedBar);
        }

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
