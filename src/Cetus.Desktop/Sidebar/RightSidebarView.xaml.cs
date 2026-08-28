using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace Cetus.Sidebar;

/// <summary>
/// Edge-style tabbed side panel: a pill tab strip with new-tab and dropdown
/// management, an empty-state picker and per-kind tab contents. Browser tabs
/// each own a WebView2; terminal tabs share one PowerShell session; file tabs
/// carry independent tree state.
/// </summary>
public partial class RightSidebarView : UserControl, IDisposable
{
    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");

    private readonly List<SidebarTab> _tabs = new();
    private readonly List<ClosedTab> _closed = new();
    private readonly SidebarTerminalSession _terminal = new();
    private SidebarTab? _activeTab;
    private Func<Uri>? _dshEndpointProvider;
    private bool _isDark = true;
    private bool _disposed;
    // When an auto-closing popup swallows the click that re-targets its anchor
    // button, the button's Click fires after the close; suppress the reopen.
    private DateTime _newTabMenuSuppressedUntil;
    private DateTime _tabsMenuSuppressedUntil;

    public RightSidebarView()
    {
        InitializeComponent();
    }

    /// <summary>Provides the live DSH endpoint for status polling (port changes follow).</summary>
    public void SetDshEndpointProvider(Func<Uri> provider) => _dshEndpointProvider = provider;

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        foreach (SidebarTab tab in _tabs)
        {
            if (tab.Content is BrowserTabContent browser)
            {
                browser.ApplyTheme(isDark);
            }
        }
    }

    /// <summary>
    /// Dims the panel while a real modal ([role=dialog][aria-modal=true]) is
    /// open on the DSH page, matching the page's own mask; the dim also
    /// blocks panel interaction while the modal is topmost.
    /// </summary>
    public void SetModalDim(bool visible)
    {
        if (_disposed)
        {
            return;
        }

        ModalDim.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewTabClicked(object sender, RoutedEventArgs e)
    {
        if (DateTime.Now < _newTabMenuSuppressedUntil)
        {
            return;
        }

        if (NewTabMenuPopup.IsOpen)
        {
            NewTabMenuPopup.IsOpen = false;
            return;
        }

        NewTabMenuPopup.PlacementTarget = NewTabButton;
        NewTabMenuPopup.IsOpen = true;
    }

    private void OnNewTabMenuPopupClosed(object sender, EventArgs e)
    {
        _newTabMenuSuppressedUntil = DateTime.Now.AddMilliseconds(250);
    }

    private void OnMenuBrowserClicked(object sender, RoutedEventArgs e)
    {
        NewTabMenuPopup.IsOpen = false;
        OpenTab(SidebarTabKind.Browser);
    }

    private void OnMenuTerminalClicked(object sender, RoutedEventArgs e)
    {
        NewTabMenuPopup.IsOpen = false;
        OpenTab(SidebarTabKind.Terminal);
    }

    private void OnMenuFilesClicked(object sender, RoutedEventArgs e)
    {
        NewTabMenuPopup.IsOpen = false;
        OpenTab(SidebarTabKind.Files);
    }

    private void OnEmptyBrowserClicked(object sender, RoutedEventArgs e) =>
        OpenTab(SidebarTabKind.Browser);

    private void OnEmptyTerminalClicked(object sender, RoutedEventArgs e) =>
        OpenTab(SidebarTabKind.Terminal);

    private void OnEmptyFilesClicked(object sender, RoutedEventArgs e) =>
        OpenTab(SidebarTabKind.Files);

    private void OnEmptyStatusClicked(object sender, RoutedEventArgs e) =>
        OpenTab(SidebarTabKind.Status);

    private void OnTabsMenuClicked(object sender, RoutedEventArgs e)
    {
        if (DateTime.Now < _tabsMenuSuppressedUntil)
        {
            return;
        }

        if (TabsMenuPopup.IsOpen)
        {
            TabsMenuPopup.IsOpen = false;
            return;
        }

        TabsMenuBorder.Width = Math.Max(260, ActualWidth - 20);
        RebuildPopupLists();
        TabsMenuPopup.IsOpen = true;
    }

    private void OnTabSearchChanged(object sender, TextChangedEventArgs e)
    {
        TabSearchPlaceholder.Visibility = TabSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildPopupLists();
    }

    private void OnTabsMenuPopupClosed(object sender, EventArgs e)
    {
        _tabsMenuSuppressedUntil = DateTime.Now.AddMilliseconds(250);
        TabSearchBox.Clear();
    }

    private void OpenTab(SidebarTabKind kind, string? initialUrl = null)
    {
        if (_disposed)
        {
            return;
        }

        FrameworkElement content;
        string title;
        switch (kind)
        {
            case SidebarTabKind.Browser:
                var browser = new BrowserTabContent(initialUrl);
                content = browser;
                title = SidebarTabModel.TitleOf(kind);
                break;
            case SidebarTabKind.Terminal:
                content = new TerminalTabContent(_terminal);
                title = SidebarTabModel.TitleOf(kind);
                break;
            case SidebarTabKind.Status:
                var status = new StatusTabContent();
                status.SetEndpointProvider(() => _dshEndpointProvider?.Invoke()
                    ?? new Uri("http://127.0.0.1:3080/"));
                content = status;
                title = SidebarTabModel.TitleOf(kind);
                break;
            default:
                content = new FilesTabContent();
                title = SidebarTabModel.TitleOf(kind);
                break;
        }

        var tab = new SidebarTab(kind, title, SidebarTabModel.GlyphOf(kind), content);
        if (content is BrowserTabContent browserContent)
        {
            browserContent.TitleChanged += (_, documentTitle) =>
            {
                tab.Title = documentTitle;
                RefreshTabStrip();
                if (TabsMenuPopup.IsOpen)
                {
                    RebuildPopupLists();
                }
            };
        }

        _tabs.Add(tab);
        ActivateTab(tab);
    }

    private void ActivateTab(SidebarTab tab)
    {
        if (_disposed)
        {
            return;
        }

        _activeTab = tab;
        ActiveTabHost.Content = tab.Content;
        UpdateEmptyState();
        RefreshTabStrip();
    }

    private void CloseTab(SidebarTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        string? url = tab.Content is BrowserTabContent browser ? browser.CurrentAddress : null;
        _closed.Insert(0, new ClosedTab(tab.Kind, tab.Title, tab.Glyph, DateTime.Now, url));
        if (_closed.Count > SidebarTabModel.MaxRecentlyClosed)
        {
            _closed.RemoveAt(_closed.Count - 1);
        }

        _tabs.Remove(tab);
        if (ReferenceEquals(_activeTab, tab))
        {
            _activeTab = _tabs.Count > 0 ? _tabs[Math.Min(index, _tabs.Count - 1)] : null;
            ActiveTabHost.Content = _activeTab?.Content;
        }

        ReleaseTab(tab);
        UpdateEmptyState();
        RefreshTabStrip();
        if (TabsMenuPopup.IsOpen)
        {
            RebuildPopupLists();
        }
    }

    private void RestoreClosed(ClosedTab entry)
    {
        _closed.Remove(entry);
        if (TabsMenuPopup.IsOpen)
        {
            RebuildPopupLists();
        }

        OpenTab(entry.Kind, entry.Url);
    }

    private static void ReleaseTab(SidebarTab tab)
    {
        switch (tab.Content)
        {
            case BrowserTabContent browser:
                browser.Dispose();
                break;
            case TerminalTabContent terminal:
                terminal.Detach();
                break;
            case StatusTabContent status:
                status.Dispose();
                break;
        }
    }

    /// <summary>Shows the empty-state picker when no tabs are open.</summary>
    public void UpdateEmptyState()
    {
        bool empty = _tabs.Count == 0;
        EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ActiveTabHost.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshTabStrip()
    {
        TabStripPanel.Children.Clear();
        foreach (SidebarTab tab in _tabs)
        {
            TabStripPanel.Children.Add(CreatePill(tab));
        }
    }

    private Border CreatePill(SidebarTab tab)
    {
        bool active = ReferenceEquals(tab, _activeTab);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(8, 4, 4, 4),
            Background = active
                ? (Brush)FindResource("SidebarPanelSelectedBrush")
                : Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var close = new TextBlock
        {
            Text = "\uE8BB",
            FontFamily = GlyphFont,
            FontSize = 10,
            Opacity = 0.75,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "关闭标签页",
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseTab(tab);
        };

        var layout = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        layout.Children.Add(close);
        layout.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = tab.Glyph,
                    FontFamily = GlyphFont,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = tab.Title,
                    Margin = new Thickness(6, 0, 0, 0),
                    MaxWidth = 110,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        });

        pill.Child = layout;
        pill.MouseEnter += (_, _) =>
        {
            if (!active)
            {
                pill.Background = HoverBrush();
            }
        };
        pill.MouseLeave += (_, _) =>
        {
            if (!active)
            {
                pill.Background = Brushes.Transparent;
            }
        };
        pill.MouseLeftButtonUp += (_, _) => ActivateTab(tab);
        ToolTipService.SetToolTip(pill, tab.Title);
        return pill;
    }

    private void RebuildPopupLists()
    {
        string? search = TabSearchBox.Text;
        OpenTabsHost.Children.Clear();
        ClosedTabsHost.Children.Clear();

        int openCount = 0;
        foreach (SidebarTab tab in _tabs)
        {
            if (!SidebarTabModel.MatchesSearch(tab.Title, search))
            {
                continue;
            }

            OpenTabsHost.Children.Add(CreateOpenTabRow(tab));
            openCount++;
        }

        int closedCount = 0;
        foreach (ClosedTab entry in _closed)
        {
            if (!SidebarTabModel.MatchesSearch(entry.Title, search))
            {
                continue;
            }

            ClosedTabsHost.Children.Add(CreateClosedRow(entry));
            closedCount++;
        }

        OpenTabsHeader.Visibility = openCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClosedTabsHeader.Visibility = closedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchHint.Visibility = openCount == 0 && closedCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border CreateOpenTabRow(SidebarTab tab)
    {
        var close = new TextBlock
        {
            Text = "\uE8BB",
            FontFamily = GlyphFont,
            FontSize = 11,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "关闭",
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseTab(tab);
        };

        var row = CreateRow(tab.Glyph, tab.Title, "刚刚", close);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            TabsMenuPopup.IsOpen = false;
            ActivateTab(tab);
        };
        return row;
    }

    private Border CreateClosedRow(ClosedTab entry)
    {
        var row = CreateRow(
            entry.Glyph,
            entry.Title,
            SidebarTabModel.RelativeTime(entry.ClosedAt, DateTime.Now),
            trailing: null);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RestoreClosed(entry);
        };
        return row;
    }

    private Border CreateRow(string glyph, string title, string timeText, TextBlock? trailing)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var layout = new DockPanel { LastChildFill = true };
        var time = new TextBlock
        {
            Text = timeText,
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, trailing is null ? 0 : 8, 0),
        };
        DockPanel.SetDock(time, Dock.Right);
        layout.Children.Add(time);
        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            layout.Children.Add(trailing);
        }

        layout.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = glyph,
                    FontFamily = GlyphFont,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        });

        row.Child = layout;
        row.MouseEnter += (_, _) => row.Background = HoverBrush();
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private Brush HoverBrush() =>
        (TryFindResource("CaptionHoverBrush") as Brush) ?? Brushes.Transparent;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NewTabMenuPopup.IsOpen = false;
        TabsMenuPopup.IsOpen = false;
        foreach (SidebarTab tab in _tabs)
        {
            ReleaseTab(tab);
        }

        _tabs.Clear();
        _activeTab = null;
        _terminal.Dispose();
    }
}

/// <summary>View-layer tab entry pairing the model data with its live content.</summary>
public sealed class SidebarTab
{
    public SidebarTab(SidebarTabKind kind, string title, string glyph, FrameworkElement content)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
        Content = content;
    }

    public SidebarTabKind Kind { get; }

    public string Title { get; set; }

    public string Glyph { get; }

    public FrameworkElement Content { get; }

    public DateTime OpenedAt { get; } = DateTime.Now;
}
