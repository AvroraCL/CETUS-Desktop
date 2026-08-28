namespace Cetus.Sidebar;

public enum SidebarTabKind
{
    Browser,
    Terminal,
    Files,
}

/// <summary>Immutable snapshot of a closed tab, kept for the restore list.</summary>
public sealed record ClosedTab(
    SidebarTabKind Kind,
    string Title,
    string Glyph,
    DateTime ClosedAt,
    string? Url);

/// <summary>
/// Pure rules for the side-panel tab model: relative timestamps, the
/// recently-closed cap and the dropdown search filter. No WPF or I/O so the
/// rules stay unit-testable.
/// </summary>
public static class SidebarTabModel
{
    public const int MaxRecentlyClosed = 10;

    public static string GlyphOf(SidebarTabKind kind) => kind switch
    {
        SidebarTabKind.Browser => "\uE774",
        SidebarTabKind.Terminal => "\uE756",
        _ => "\uE8B7",
    };

    public static string TitleOf(SidebarTabKind kind) => kind switch
    {
        SidebarTabKind.Browser => "浏览器",
        SidebarTabKind.Terminal => "终端",
        _ => "文件",
    };

    /// <summary>Short dropdown style: 刚刚 / N分 / N时 / N天.</summary>
    public static string RelativeTime(DateTime closedAt, DateTime now)
    {
        TimeSpan elapsed = now - closedAt;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}分";
        }

        if (elapsed < TimeSpan.FromHours(24))
        {
            return $"{(int)elapsed.TotalHours}时";
        }

        return $"{(int)elapsed.TotalDays}天";
    }

    /// <summary>Inserts newest-first and trims the list to MaxRecentlyClosed.</summary>
    public static List<ClosedTab> PushClosed(List<ClosedTab> list, ClosedTab entry)
    {
        list.Insert(0, entry);
        if (list.Count > MaxRecentlyClosed)
        {
            list.RemoveRange(MaxRecentlyClosed, list.Count - MaxRecentlyClosed);
        }

        return list;
    }

    public static bool MatchesSearch(string title, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || title.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
