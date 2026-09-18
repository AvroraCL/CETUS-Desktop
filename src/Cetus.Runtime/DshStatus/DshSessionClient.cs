using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cetus.Hosting;

namespace Cetus.DshStatus;

public sealed record DshSessionInfo(string SessionId, string Title, bool Running, DateTimeOffset UpdatedAt);

/// <summary>
/// Queries the local DSH host for its session list over the loopback RPC API.
/// Every request carries the browser-session auth cookie introduced with DSH
/// 0.1.5; JSON parsing is tolerant and silently drops malformed entries.
/// </summary>
public sealed class DshSessionClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly string? _dshHomeOverride;
    private int _rpcId;
    private bool _disposed;

    public DshSessionClient(string? dshHomeOverride = null, HttpMessageHandler? handler = null)
    {
        _dshHomeOverride = dshHomeOverride;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<IReadOnlyList<DshSessionInfo>> GetSessionsAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        JsonElement value = await PostMethodAsync(endpoint, "session.list", new { }, cancellationToken);
        return ParseSessions(value);
    }

    private async Task<JsonElement> PostMethodAsync(
        Uri endpoint,
        string method,
        object payload,
        CancellationToken cancellationToken)
    {
        int rpcId = Interlocked.Increment(ref _rpcId);
        string body = JsonSerializer.Serialize(
            new { type = "client-request", rpcId = $"cetus-{rpcId}", method, payload });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, $"/api/{method}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (DshAuth.TryGetSessionCookie(endpoint, _dshHomeOverride) is { } cookie)
        {
            request.Headers.Add("Cookie", $"{cookie.Name}={cookie.Value}");
        }

        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("result", out JsonElement result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("ok", out JsonElement ok)
            || ok.ValueKind != JsonValueKind.True)
        {
            string detail = result.TryGetProperty("error", out JsonElement error)
                ? error.ToString()
                : "unknown";
            throw new InvalidOperationException($"DSH 接口 {method} 调用失败：{detail}");
        }

        return result.GetProperty("value").Clone();
    }

    private static List<DshSessionInfo> ParseSessions(JsonElement value)
    {
        var sessions = new List<DshSessionInfo>();
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("items", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return sessions;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? sessionId = GetString(item, "sessionId");
            if (string.IsNullOrEmpty(sessionId))
            {
                continue;
            }

            string cwd = GetString(item, "cwd") ?? string.Empty;
            string? title = !string.IsNullOrWhiteSpace(cwd)
                ? Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : null;
            if (item.TryGetProperty("title", out JsonElement titleElement)
                && titleElement.ValueKind == JsonValueKind.Object
                && GetString(titleElement, "title") is { } projectedTitle)
            {
                title = projectedTitle;
            }

            bool running = item.TryGetProperty("running", out JsonElement runningElement)
                && runningElement.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(title))
            {
                title = "会话";
            }

            sessions.Add(new DshSessionInfo(
                sessionId,
                title,
                running,
                GetEpochTime(item, "updatedAt")));
        }

        return sessions;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset GetEpochTime(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long milliseconds))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }

            if (property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString(), out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        return DateTimeOffset.MinValue;
    }

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
