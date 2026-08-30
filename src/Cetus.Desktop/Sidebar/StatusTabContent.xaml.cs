using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cetus.DshStatus;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace Cetus.Sidebar;

public enum StatusScope
{
    CurrentSession,
    CurrentProject,
    AllHistory,
}

/// <summary>
/// Status tab v2: an observe-and-act surface for the DSH agent. Scope
/// selector (current session / current project / all history), an insight
/// strip driven by pure rules (context pressure, cache hit, output spikes),
/// decision-first KPIs, usage charts and DeepSeek balance. Polls every 10s
/// while visible; the balance is queried on activation and manual refresh.
/// </summary>
public partial class StatusTabContent : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private static readonly Brush TokenInputBrush = Frozen("#60A5FA");
    private static readonly Brush TokenOutputBrush = Frozen("#34D399");
    private static readonly Brush CacheReadBrush = Frozen("#A78BFA");
    private static readonly Brush CacheWriteBrush = Frozen("#FBBF24");
    private static readonly Brush BalanceGrantedBrush = Frozen("#34D399");
    private static readonly Brush BalanceToppedBrush = Frozen("#60A5FA");
    private static readonly Brush SessionBarBrush = Frozen("#60A5FA");
    private static readonly Brush ContextOkBrush = Frozen("#34D399");
    private static readonly Brush ContextWarnBrush = Frozen("#FBBF24");
    private static readonly Brush ContextCriticalBrush = Frozen("#F87171");

    private readonly DshStatusClient _client = new();
    private Func<Uri>? _endpointProvider;
    private DispatcherTimer? _pollTimer;
    private StatusScope _scope = StatusScope.CurrentSession;
    private (long Tokens, DateTime At)? _lastRateSample;
    private string? _focusSessionId;
    private string? _focusWorkspaceId;
    private double _contextPercent;
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
        ContextTrack.SizeChanged += (_, _) => UpdateContextBarWidth();
    }

    /// <summary>Provides the live DSH endpoint (follows port changes).</summary>
    public void SetEndpointProvider(Func<Uri> provider) => _endpointProvider = provider;

    public void ApplyTheme(bool isDark)
    {
        // Colors ride DynamicResource brushes or fixed chart accents.
    }

    private static Brush Frozen(string color) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) { Opacity = 0.9 };

    private void StartPolling()
    {
        if (_pollTimer is null)
        {
            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += (_, _) =>
            {
                if (IsVisible)
                {
                    _ = RefreshAsync(includeBalance: false);
                }
            };
        }

        _pollTimer.Start();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) =>
        _ = RefreshAsync(includeBalance: true);

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (!_disposed && IsLoaded)
        {
            _ = RefreshAsync(includeBalance: false);
        }
    }

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

        _scope = ScopeSessionButton.IsChecked == true
            ? StatusScope.CurrentSession
            : ScopeProjectButton.IsChecked == true
                ? StatusScope.CurrentProject
                : StatusScope.AllHistory;

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

            var (scopedUsage, focus) = ResolveScope(snapshot);
            _focusSessionId = focus?.SessionId;
            _focusWorkspaceId = snapshot.Workspace?.WorkspaceId;

            bool anyRunning = snapshot.Sessions.Any(session => session.Running);
            double outputRate = MeasureOutputRate(snapshot, anyRunning);

            RenderWorkspace(snapshot, focus);
            RenderContext(focus);
            RenderUsageChart(scopedUsage);
            RenderSessionBars(snapshot);
            RenderKpis(snapshot, scopedUsage, focus, outputRate);
            RenderInsights(snapshot, scopedUsage, focus, outputRate);
            RenderTask(focus);

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

    /// <summary>
    /// Computes the scope's aggregated usage and its focus session (running
    /// first, else the most recently updated).
    /// </summary>
    private (DshUsageSummary Usage, DshSessionDetail? Focus) ResolveScope(DshStatusSnapshot snapshot)
    {
        static DshSessionDetail? FocusOf(IEnumerable<DshSessionDetail> sessions) =>
            sessions.FirstOrDefault(session => session.Running)
            ?? sessions.OrderByDescending(session => session.UpdatedAt).FirstOrDefault();

        switch (_scope)
        {
            case StatusScope.CurrentSession:
            {
                var focus = FocusOf(snapshot.Sessions);
                var single = focus is null ? Array.Empty<DshSessionDetail>() : new[] { focus };
                return (DshUsageSummary.Sum(single), focus);
            }
            case StatusScope.CurrentProject when snapshot.Workspace is { } workspace:
            {
                // workspace.list rows do not carry session ids here, so
                // "project" means sessions whose cwd belongs to the workspace
                // title folder; when nothing matches, fall back to all.
                var matching = snapshot.Sessions
                    .Where(session => session.Cwd.EndsWith(
                        workspace.Title,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matching.Count == 0)
                {
                    matching = snapshot.Sessions.ToList();
                }

                return (DshUsageSummary.Sum(matching), FocusOf(matching));
            }
            default:
                return (DshUsageSummary.Sum(snapshot.Sessions), FocusOf(snapshot.Sessions));
        }
    }

    private void RenderWorkspace(DshStatusSnapshot snapshot, DshSessionDetail? focus)
    {
        if (snapshot.Workspace is { } workspace)
        {
            WorkspaceTitleText.Text = workspace.Title;
            WorkspacePathText.Text = workspace.Path;
            WorkspaceMetaText.Text =
                $"{workspace.SessionCount} 个会话 · 最近活动 {workspace.UpdatedAt:MM-dd HH:mm}"
                + (focus is { } f ? $" · 焦点 {f.Title}" : string.Empty);
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
    }

    private void RenderContext(DshSessionDetail? focus)
    {
        double? occupancy = focus?.Pressure?.OccupancyPercent;
        if (focus is null || occupancy is null)
        {
            _contextPercent = 0;
            UpdateContextBarWidth();
            ContextPercentText.Text = "—";
            ContextDetailText.Text = "该会话尚未报告上下文压力。";
            return;
        }

        double percent = Math.Clamp(occupancy.Value, 0, 100);
        _contextPercent = percent;
        UpdateContextBarWidth();
        ContextPercentText.Text = $"{percent:0}%";

        Brush bar = percent >= DshInsights.ContextCriticalPercent
            ? ContextCriticalBrush
            : percent >= DshInsights.ContextWarnPercent
                ? ContextWarnBrush
                : ContextOkBrush;
        ContextBar.Background = bar;

        long? window = focus.Pressure?.ContextWindow;
        double? projected = focus.Pressure?.ProjectedTokens ?? focus.Pressure?.PressureTokens;
        ContextDetailText.Text = window is > 0 && projected is not null
            ? $"{projected:N0} / {window:N0} tokens"
            : "压力数据不完整。";
    }

    private void UpdateContextBarWidth()
    {
        ContextBar.Width = ContextTrack.ActualWidth > 0
            ? ContextTrack.ActualWidth * _contextPercent / 100
            : 0;
    }

    private void RenderKpis(DshStatusSnapshot snapshot, DshUsageSummary usage, DshSessionDetail? focus, double outputRate)
    {
        long denominator = usage.CacheReadTokens + usage.InputTokens;
        CacheHitText.Text = denominator > 0
            ? $"{usage.CacheReadTokens / (double)denominator * 100:0}%"
            : "—";

        bool anyRunning = snapshot.Sessions.Any(session => session.Running);
        RateText.Text = anyRunning
            ? (outputRate >= 0 ? $"{outputRate:N0} tokens/分" : "测量中…")
            : "空闲";

        LlmTimeText.Text = FormatDuration(usage.LlmMilliseconds);
        ToolTimeText.Text = FormatDuration(usage.ToolMilliseconds);
        SessionsText.Text = usage.SessionCount.ToString("N0");
        TurnsText.Text = usage.Turns.ToString("N0");
        StepsText.Text = usage.Steps.ToString("N0");
        SessionsText.ToolTip = $"会话总数 {usage.SessionCount:N0}";
        TurnsText.ToolTip = $"累计轮次 {usage.Turns:N0}";
        StepsText.ToolTip = $"累计步骤 {usage.Steps:N0}";
    }

    /// <summary>
    /// Output velocity measured between consecutive refreshes (all-history
    /// totals so the rate is scope-independent); -1 when not yet measurable.
    /// </summary>
    private double MeasureOutputRate(DshStatusSnapshot snapshot, bool anyRunning)
    {
        long total = snapshot.Usage.OutputTokens;
        DateTime now = DateTime.Now;
        double rate = -1;
        if (_lastRateSample is { } previous && anyRunning && now > previous.At)
        {
            double minutes = (now - previous.At).TotalMinutes;
            if (minutes > 0.01 && total >= previous.Tokens)
            {
                rate = (total - previous.Tokens) / minutes;
            }
        }

        _lastRateSample = (total, now);
        return rate;
    }

    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "—";
        }

        TimeSpan span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}分{span.Seconds}秒"
            : $"{span.TotalSeconds:0.#}秒";
    }

    private void RenderInsights(DshStatusSnapshot snapshot, DshUsageSummary usage, DshSessionDetail? focus, double outputRate)
    {
        InsightsHost.Children.Clear();

        double? occupancy = focus?.Pressure?.OccupancyPercent;
        long denominator = usage.CacheReadTokens + usage.InputTokens;
        double cacheHit = denominator > 0
            ? usage.CacheReadTokens / (double)denominator * 100
            : -1;
        bool anyRunning = snapshot.Sessions.Any(session => session.Running);

        foreach (DshInsight insight in DshInsights.Evaluate(
                     occupancy,
                     cacheHit,
                     usage.InputTokens,
                     anyRunning,
                     outputRate))
        {
            InsightsHost.Children.Add(CreateInsightRow(insight));
        }
    }

    private FrameworkElement CreateInsightRow(DshInsight insight)
    {
        Brush color = insight.Severity switch
        {
            InsightSeverity.Critical => ContextCriticalBrush,
            InsightSeverity.Warn => ContextWarnBrush,
            _ => TokenInputBrush,
        };

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = color,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0),
        });

        var messageColumn = new StackPanel();
        messageColumn.Children.Add(new TextBlock
        {
            Text = insight.Message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("SidebarPanelForegroundBrush"),
        });

        if (insight.Action == InsightAction.NewSession && _focusWorkspaceId is { } workspaceId)
        {
            var newSession = new Button
            {
                Content = "新建会话",
                Style = (Style)FindResource("InsightActionButton"),
            };
            newSession.Click += (_, _) => _ = RunInsightActionAsync(InsightAction.NewSession, workspaceId);
            messageColumn.Children.Add(newSession);
        }
        else if (insight.Action == InsightAction.CancelSession && _focusSessionId is { } sessionId)
        {
            var cancel = new Button
            {
                Content = "中断会话",
                Style = (Style)FindResource("InsightActionButton"),
                Foreground = ContextCriticalBrush,
            };
            cancel.Click += (_, _) => _ = RunInsightActionAsync(InsightAction.CancelSession, sessionId);
            messageColumn.Children.Add(cancel);
        }

        Grid.SetColumn(messageColumn, 1);
        row.Children.Add(messageColumn);
        return row;
    }

    private async Task RunInsightActionAsync(InsightAction action, string argument)
    {
        if (_endpointProvider is null)
        {
            return;
        }

        Uri endpoint = _endpointProvider();
        try
        {
            switch (action)
            {
                case InsightAction.NewSession:
                {
                    string sessionId = await _client.CreateSessionAsync(endpoint, argument, CancellationToken.None);
                    UpdatedText.Text = $"已新建会话 {sessionId}";
                    break;
                }
                case InsightAction.CancelSession:
                {
                    await _client.CancelSessionAsync(endpoint, argument, CancellationToken.None);
                    UpdatedText.Text = "已发送中断请求";
                    break;
                }
            }

            _ = RefreshAsync(includeBalance: false);
        }
        catch (Exception error)
        {
            UpdatedText.Text = $"操作失败：{error.Message}";
        }
    }

    private void RenderTask(DshSessionDetail? focus)
    {
        IReadOnlyList<DshTodo> todos = focus?.Todos ?? Array.Empty<DshTodo>();
        if (todos.Count == 0)
        {
            TaskProgressText.Text = "无任务清单";
            TaskCurrentText.Visibility = Visibility.Collapsed;
            return;
        }

        int completed = todos.Count(todo => todo.Status == "completed");
        TaskProgressText.Text = $"{completed}/{todos.Count} 项完成";

        DshTodo? current = todos.FirstOrDefault(todo => todo.Status == "in_progress")
            ?? todos.FirstOrDefault(todo => todo.Status == "pending");
        if (current is not null)
        {
            TaskCurrentText.Text = $"当前：{current.Content}";
            TaskCurrentText.Visibility = Visibility.Visible;
        }
        else
        {
            TaskCurrentText.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderUsageChart(DshUsageSummary usage)
    {
        long total = usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
        TotalTokensText.Text = $"{total:N0} tokens";
        UsageLegend.Children.Clear();
        UsageBarHost.Children.Clear();
        UsageBarHost.ColumnDefinitions.Clear();

        if (total <= 0)
        {
            UsageEmptyText.Visibility = Visibility.Visible;
            UsageBarFrame.Visibility = Visibility.Collapsed;
            return;
        }

        UsageEmptyText.Visibility = Visibility.Collapsed;
        UsageBarFrame.Visibility = Visibility.Visible;

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

    private void RenderSessionBars(DshStatusSnapshot snapshot)
    {
        SessionBarsHost.Children.Clear();
        SessionBarsHost.ColumnDefinitions.Clear();

        // The bars always draw from the full history so the chart keeps its
        // at-a-glance timeline meaning regardless of the selected scope.
        var recent = snapshot.Sessions
            .Where(session => session.UpdatedAt > DateTime.MinValue)
            .OrderBy(session => session.UpdatedAt)
            .TakeLast(8)
            .ToArray();

        if (recent.Length == 0)
        {
            SessionBarsEmptyText.Visibility = Visibility.Visible;
            return;
        }

        SessionBarsEmptyText.Visibility = Visibility.Collapsed;
        long max = Math.Max(1, recent.Max(session => session.Usage?.OutputTokens ?? 0));
        for (int i = 0; i < recent.Length; i++)
        {
            DshSessionDetail session = recent[i];
            SessionBarsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            double fraction = (session.Usage?.OutputTokens ?? 0) / (double)max;
            var bar = new Border
            {
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = SessionBarBrush,
                Height = Math.Max(4, 90 * fraction),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = $"{session.Title}\n输出 {(session.Usage?.OutputTokens ?? 0):N0} tokens · {session.UpdatedAt:MM-dd HH:mm}",
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
            BalanceBarFrame.Visibility = Visibility.Collapsed;
            BalanceBarHost.ColumnDefinitions.Clear();
            BalanceBarHost.Children.Clear();
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
            BalanceBarFrame.Visibility = Visibility.Collapsed;
            BalanceBarHost.ColumnDefinitions.Clear();
            BalanceBarHost.Children.Clear();
            BalanceDetailText.Text = "余额接口未返回数据（非 DeepSeek 官方平台 Key 时属预期）。";
            BalanceDetailText.Visibility = Visibility.Visible;
            return;
        }

        BalanceText.Text = $"{balance.TotalBalance.ToString("0.##")} {balance.Currency}";
        BalanceBarFrame.Visibility = Visibility.Visible;
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
