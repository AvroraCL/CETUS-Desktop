using System.IO;

namespace Cetus.Sidebar;

/// <summary>One changed file in the reviewer file list.</summary>
/// <param name="Status">
/// Porcelain status letter: M modified, A added, D deleted, R renamed,
/// ?? untracked.
/// </param>
internal sealed record ReviewFile(string Status, string Path)
{
    public static string NormalizeStatus(string raw) =>
        raw == "??" ? "??" : raw.TrimEnd('?').Trim().Length == 0 ? "M" : raw.TrimEnd('?').Trim()[..1].ToUpperInvariant();

    public string StatusBadge => Status == "??" ? "U" : Status;

    public string StatusColor => Status switch
    {
        "M" => "#5B9BD5",
        "A" => "#6CC26C",
        "D" => "#E57373",
        "R" => "#C8A2E0",
        _ => "#9CA3AF",
    };
}

/// <summary>One line of a rendered diff (or an info notice).</summary>
/// <param name="Kind">add, del, hunk, ctx or info.</param>
internal sealed record DiffLine(string Kind, string Text)
{
    public static DiffLine Info(string text) => new("info", text);
}
