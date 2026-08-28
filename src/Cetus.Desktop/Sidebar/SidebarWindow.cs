using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cetus.Sidebar;

/// <summary>
/// The right sidebar as a borderless owned window layered over the main
/// window's right edge. Slides by animating its own width while the content
/// stays frozen at its expanded layout (right-anchored, clipped by the window
/// edges), so a slide never reflows the panel or the hosted DSH WebView — the
/// same slide-not-morph contract as DSH's native sidebar.
/// </summary>
public sealed class SidebarWindow : Window, IDisposable
{
    private const double TitleBarHeight = 40;
    private const double DividerLineWidth = 1;

    private readonly RightSidebarView _content = new();
    private Window? _owner;
    private bool _isOpen;
    private bool _disposed;

    public SidebarWindow()
    {
        Title = "CETUS鲸鱼座 · 侧栏";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = false;
        SetResourceReference(BackgroundProperty, "RightSidebarBackgroundBrush");
        Width = 0;

        var contentHost = new Grid { ClipToBounds = true };
        contentHost.Children.Add(_content);

        // The 1px divider rides the window's left edge, so it always hugs the
        // panel regardless of the animated width.
        var frame = new Border { BorderThickness = new Thickness(DividerLineWidth, 0, 0, 0) };
        frame.SetResourceReference(Border.BorderBrushProperty, "SidebarBorderBrush");
        frame.Child = contentHost;
        Content = frame;

        SizeChanged += (_, _) => SyncToOwner();
        Visibility = Visibility.Hidden;
    }

    /// <summary>Wires the sidebar to its owner window and initial geometry.</summary>
    public void Attach(Window owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Owner = owner;
        owner.LocationChanged += (_, _) => SyncToOwner();
        owner.SizeChanged += (_, _) => SyncToOwner();
        owner.IsVisibleChanged += (_, _) => SyncVisibility();
        SyncToOwner();
        SyncVisibility();
    }

    /// <summary>Open/closed intent; the window only shows while the owner does.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value || _disposed)
            {
                return;
            }

            _isOpen = value;
            SyncVisibility();
        }
    }

    /// <summary>Freezes the panel at its expanded width for a slide.</summary>
    public void FreezeContent(double expandedWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _content.Width = expandedWidth;
        _content.HorizontalAlignment = HorizontalAlignment.Right;
    }

    /// <summary>Returns the panel to stretching with the window.</summary>
    public void ReleaseContent()
    {
        if (_disposed)
        {
            return;
        }

        _content.ClearValue(WidthProperty);
        _content.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public void EndWidthAnimation() => BeginAnimation(WidthProperty, null);

    public void ApplyTheme(bool isDark) => _content.ApplyTheme(isDark);

    private void SyncToOwner()
    {
        if (_owner is null || _disposed || !_owner.IsLoaded || _owner.ActualWidth <= 0)
        {
            return;
        }

        Top = _owner.Top + TitleBarHeight;
        Height = Math.Max(0, _owner.ActualHeight - TitleBarHeight);
        Left = _owner.Left + _owner.ActualWidth - ActualWidth;
    }

    private void SyncVisibility()
    {
        if (_owner is null || _disposed)
        {
            return;
        }

        bool shouldShow = _isOpen && _owner.IsVisible;
        if (shouldShow)
        {
            SyncToOwner();
            if (!IsVisible)
            {
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _content.Dispose();
        Close();
    }
}
