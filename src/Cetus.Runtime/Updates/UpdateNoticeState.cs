using System.Text.Json;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>An update the user has been told about.</summary>
internal sealed record AvailableUpdate(ReleaseInfo Release, UpdateFeedSource Source, bool InstalledEdition);

/// <summary>
/// Builds the update payload consumed by the notice rendered inside the
/// Harness page. Kept as a pure function of its inputs so the contract between
/// Cetus and the injected script is unit-testable without a browser.
/// </summary>
internal static class UpdateNoticeState
{
    internal const int MaxNotesLength = 600;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Unavailable() => """{"available":false}""";

    public static string For(
        ReleaseInfo release,
        UpdateFeedSource source,
        Version currentVersion,
        bool installing,
        double progress,
        bool dismissed,
        bool installable = true)
    {
        string notes = NormalizeNotes(release.Notes);
        return JsonSerializer.Serialize(
            new
            {
                available = !dismissed,
                dismissed,
                version = release.TagName,
                versionNumber = release.Version.ToString(3),
                current = currentVersion.ToString(3),
                notes,
                installing,
                installable,
                progress = Math.Clamp(progress, 0d, 1d),
                source = source switch
                {
                    UpdateFeedSource.GitCode => "gitcode",
                    _ => "github",
                },
            },
            SerializerOptions);
    }

    /// <summary>
    /// Release bodies are Markdown; the notice shows them as plain text, so
    /// heading markers are stripped and over-long bodies are cut on a line
    /// boundary instead of mid-sentence.
    /// </summary>
    internal static string NormalizeNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string[] lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();

        var trimmed = new List<string>();
        foreach (string line in lines)
        {
            string cleaned = line.TrimStart();
            while (cleaned.StartsWith('#'))
            {
                cleaned = cleaned[1..].TrimStart();
            }

            trimmed.Add(cleaned);
        }

        string text = string.Join('\n', trimmed).Trim();
        if (text.Length <= MaxNotesLength)
        {
            return text;
        }

        int cut = text.LastIndexOf('\n', Math.Min(MaxNotesLength, text.Length - 1));
        if (cut <= 0)
        {
            cut = MaxNotesLength;
        }

        return text[..cut].TrimEnd() + "…";
    }
}
