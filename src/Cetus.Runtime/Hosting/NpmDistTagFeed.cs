using System.Net.Http;
using System.Text.Json;

namespace Cetus.Hosting;

public sealed record DshDistTag(string Channel, string Version, DshVersion ParsedVersion);

public sealed record DshDistTags(string? Latest, string? Alpha)
{
    /// <summary>Highest valid semantic version across the dist-tags CETUS tracks.</summary>
    public DshDistTag? HighestAvailable =>
        EnumerateParsedTags()
            .OrderBy(tag => tag.ParsedVersion, DshVersion.SemanticComparer)
            .LastOrDefault();

    public IEnumerable<DshDistTag> EnumerateParsedTags()
    {
        if (Latest is { } latest
            && DshVersion.TryParse(latest, out DshVersion? parsedLatest)
            && parsedLatest is not null)
        {
            yield return new DshDistTag("latest", latest, parsedLatest);
        }

        if (Alpha is { } alpha
            && DshVersion.TryParse(alpha, out DshVersion? parsedAlpha)
            && parsedAlpha is not null)
        {
            yield return new DshDistTag("alpha", alpha, parsedAlpha);
        }
    }
}

/// <summary>
/// Reads the npm registry dist-tags for the DSH package so the UI can tell
/// whether a newer runtime exists upstream (it always ships with the next
/// CETUS release — CETUS never swaps the runtime dependency tree in place).
/// </summary>
public sealed class NpmDistTagFeed : IDisposable
{
    public const string DefaultRegistry = "https://registry.npmjs.org/-/package/@deepseek-ai%2Fdsh/dist-tags";

    private readonly HttpClient _client;
    private readonly string _registryUrl;
    private bool _disposed;

    public NpmDistTagFeed(string? registryUrl = null, HttpMessageHandler? handler = null)
    {
        _registryUrl = registryUrl ?? DefaultRegistry;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>Returns null when the registry is unreachable or malformed.</summary>
    public async Task<DshDistTags?> FetchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(_registryUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new DshDistTags(
                GetString(root, "latest"),
                GetString(root, "alpha") ?? GetString(root, "next"));
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException or UriFormatException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }
}
