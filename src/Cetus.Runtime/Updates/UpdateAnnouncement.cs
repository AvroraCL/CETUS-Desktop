namespace Cetus.Updates;

/// <summary>
/// The GitHub Pages announcement page replaces the update prompt as the
/// release-notes channel: after CETUS updates itself, the new build opens the
/// page in the in-app browser. Pure URL and version-comparison logic so the
/// decision is unit-testable without I/O.
/// </summary>
public static class UpdateAnnouncement
{
    public const string PageBaseUrl = "https://avroracl.github.io/CETUS-Desktop/";

    /// <summary>
    /// True when the stored previous launch version parses and is lower than
    /// the running version — i.e. this is the first launch after an update.
    /// First launches (nothing stored) and downgrades stay silent.
    /// </summary>
    public static bool ShouldAnnounce(string? lastLaunchVersion, string currentVersion)
    {
        return TryNormalize(lastLaunchVersion, out Version previous)
            && TryNormalize(currentVersion, out Version current)
            && current > previous;
    }

    /// <summary>Parses "v0.2.3" / "0.2.3" into "0.2.3"; null when unusable.</summary>
    public static string? NormalizeVersion(string? version) =>
        TryNormalize(version, out Version parsed) ? parsed.ToString() : null;

    /// <summary>
    /// Builds the page URL carrying the version the user came from and the
    /// version they updated to, so the page can highlight the fresh release.
    /// </summary>
    public static string BuildPageUrl(string? fromVersion, string? toVersion)
    {
        string? from = NormalizeVersion(fromVersion);
        string? to = NormalizeVersion(toVersion);
        if (from is null && to is null)
        {
            return PageBaseUrl;
        }

        var query = new List<string>(2);
        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from)}");
        }

        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to)}");
        }

        return $"{PageBaseUrl}?{string.Join("&", query)}";
    }

    private static bool TryNormalize(string? value, out Version version) =>
        UpdateFeed.TryParseTag(value, out version!);
}
