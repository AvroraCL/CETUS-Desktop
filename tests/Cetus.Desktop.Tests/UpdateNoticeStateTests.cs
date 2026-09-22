using System.Text.Json;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// Contract between Cetus and the update notice rendered inside the Harness
/// page. The injected script has no other way to learn about a release, so the
/// payload shape is pinned here.
/// </summary>
public sealed class UpdateNoticeStateTests
{
    [Fact]
    public void For_AvailableRelease_DescribesVersionNotesAndProgress()
    {
        ReleaseInfo release = Release("v0.3.3", "## 修复\n- 不再误杀主机");

        JsonElement state = Parse(UpdateNoticeState.For(
            release,
            UpdateFeedSource.GitHub,
            new Version(0, 3, 2),
            installing: false,
            progress: 0.42,
            dismissed: false));

        Assert.True(state.GetProperty("available").GetBoolean());
        Assert.False(state.GetProperty("dismissed").GetBoolean());
        Assert.Equal("v0.3.3", state.GetProperty("version").GetString());
        Assert.Equal("0.3.3", state.GetProperty("versionNumber").GetString());
        Assert.Equal("0.3.2", state.GetProperty("current").GetString());
        Assert.Equal("github", state.GetProperty("source").GetString());
        Assert.False(state.GetProperty("installing").GetBoolean());
        Assert.Equal(0.42, state.GetProperty("progress").GetDouble(), 3);

        // Markdown heading markers are stripped: the card shows plain text.
        string notes = state.GetProperty("notes").GetString()!;
        Assert.Contains("修复", notes);
        Assert.DoesNotContain("##", notes);
    }

    [Fact]
    public void For_DismissedRelease_HidesTheCardButKeepsTheVersion()
    {
        JsonElement state = Parse(UpdateNoticeState.For(
            Release("v0.4.0", "notes"),
            UpdateFeedSource.GitCode,
            new Version(0, 3, 3),
            installing: false,
            progress: 0,
            dismissed: true));

        Assert.False(state.GetProperty("available").GetBoolean());
        Assert.True(state.GetProperty("dismissed").GetBoolean());
        Assert.Equal("v0.4.0", state.GetProperty("version").GetString());
        Assert.Equal("gitcode", state.GetProperty("source").GetString());
    }

    [Fact]
    public void For_Installing_FlagsBusyStateSoTheCardDisablesItsButton()
    {
        JsonElement state = Parse(UpdateNoticeState.For(
            Release("v0.4.0", "notes"),
            UpdateFeedSource.GitHub,
            new Version(0, 3, 3),
            installing: true,
            progress: 0.75,
            dismissed: false));

        Assert.True(state.GetProperty("installing").GetBoolean());
        Assert.Equal(0.75, state.GetProperty("progress").GetDouble(), 3);
    }

    [Fact]
    public void For_DevelopmentBuild_DisablesInPageInstall()
    {
        JsonElement state = Parse(UpdateNoticeState.For(
            Release("v0.4.0", "notes"),
            UpdateFeedSource.GitHub,
            new Version(0, 3, 3),
            installing: false,
            progress: 0,
            dismissed: false,
            installable: false));

        Assert.False(state.GetProperty("installable").GetBoolean());
    }

    [Fact]
    public void For_ProgressOutOfRange_IsClamped()
    {
        JsonElement over = Parse(UpdateNoticeState.For(
            Release("v1.0.0", null), UpdateFeedSource.GitHub, new Version(0, 9, 0),
            installing: true, progress: 3.4, dismissed: false));
        JsonElement under = Parse(UpdateNoticeState.For(
            Release("v1.0.0", null), UpdateFeedSource.GitHub, new Version(0, 9, 0),
            installing: true, progress: -1, dismissed: false));

        Assert.Equal(1d, over.GetProperty("progress").GetDouble(), 3);
        Assert.Equal(0d, under.GetProperty("progress").GetDouble(), 3);
        Assert.Equal(string.Empty, over.GetProperty("notes").GetString());
    }

    [Fact]
    public void Unavailable_IsAParseableHiddenState()
    {
        JsonElement state = Parse(UpdateNoticeState.Unavailable());

        Assert.False(state.GetProperty("available").GetBoolean());
    }

    [Fact]
    public void NormalizeNotes_TruncatesLongBodiesOnALineBoundary()
    {
        string body = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"第 {i} 行更新说明，内容足够长以触发截断"));

        string notes = UpdateNoticeState.NormalizeNotes(body);

        Assert.True(notes.Length <= UpdateNoticeState.MaxNotesLength + 1);
        Assert.EndsWith("…", notes, StringComparison.Ordinal);

        // Truncation cuts whole lines: every retained line is intact.
        string retained = notes[..^1];
        Assert.StartsWith("第 0 行更新说明", retained, StringComparison.Ordinal);
        foreach (string line in retained.Split('\n'))
        {
            Assert.EndsWith("触发截断", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NormalizeNotes_HandlesMissingAndWindowsBodies()
    {
        Assert.Equal(string.Empty, UpdateNoticeState.NormalizeNotes(null));
        Assert.Equal(string.Empty, UpdateNoticeState.NormalizeNotes("   "));

        string notes = UpdateNoticeState.NormalizeNotes("# 标题\r\n正文\r\n");
        Assert.Equal("标题\n正文", notes);
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ReleaseInfo Release(string tag, string? notes) =>
        new(tag, Version.Parse(tag.TrimStart('v', 'V')), notes, []);
}
