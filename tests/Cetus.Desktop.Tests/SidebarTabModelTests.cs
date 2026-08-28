using Cetus.Sidebar;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class SidebarTabModelTests
{
    [Fact]
    public void RelativeTime_FormatsShortUnits()
    {
        var now = new DateTime(2026, 8, 28, 18, 0, 0);

        Assert.Equal("刚刚", SidebarTabModel.RelativeTime(now.AddSeconds(-30), now));
        Assert.Equal("刚刚", SidebarTabModel.RelativeTime(now.AddSeconds(10), now));
        Assert.Equal("5分", SidebarTabModel.RelativeTime(now.AddMinutes(-5), now));
        Assert.Equal("59分", SidebarTabModel.RelativeTime(now.AddMinutes(-59), now));
        Assert.Equal("3时", SidebarTabModel.RelativeTime(now.AddHours(-3), now));
        Assert.Equal("23时", SidebarTabModel.RelativeTime(now.AddHours(-23), now));
        Assert.Equal("2天", SidebarTabModel.RelativeTime(now.AddDays(-2), now));
    }

    [Fact]
    public void PushClosed_InsertsNewestFirstAndCapsAtTen()
    {
        var list = new List<ClosedTab>();
        for (int i = 0; i < 12; i++)
        {
            list = SidebarTabModel.PushClosed(
                list,
                new ClosedTab(SidebarTabKind.Browser, $"tab-{i}", "\uE774", DateTime.Now, $"https://example.com/{i}"));
        }

        Assert.Equal(SidebarTabModel.MaxRecentlyClosed, list.Count);
        Assert.Equal("tab-11", list[0].Title);
        Assert.Equal("tab-2", list[^1].Title);
    }

    [Theory]
    [InlineData("CETUS 浏览器", null, true)]
    [InlineData("CETUS 浏览器", "", true)]
    [InlineData("CETUS 浏览器", "   ", true)]
    [InlineData("CETUS 浏览器", "浏览器", true)]
    [InlineData("CETUS 浏览器", "cetus", true)]
    [InlineData("CETUS 浏览器", "终端", false)]
    public void MatchesSearch_FiltersByTitleContains(string title, string? search, bool expected)
    {
        Assert.Equal(expected, SidebarTabModel.MatchesSearch(title, search));
    }

    [Fact]
    public void GlyphOf_MapsEveryKind()
    {
        Assert.False(string.IsNullOrEmpty(SidebarTabModel.GlyphOf(SidebarTabKind.Browser)));
        Assert.False(string.IsNullOrEmpty(SidebarTabModel.GlyphOf(SidebarTabKind.Terminal)));
        Assert.False(string.IsNullOrEmpty(SidebarTabModel.GlyphOf(SidebarTabKind.Files)));
    }
}
