using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Cetus.DshStatus;

public sealed record DshWorkspaceInfo(string Title, string Path, int SessionCount, DateTime UpdatedAt);

public sealed record DshUsageSummary(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long Turns,
    long Steps,
    int SessionCount);

public sealed record DshStatusSnapshot(
    DshWorkspaceInfo? Workspace,
    string? Provider,
    string? Model,
    DshUsageSummary Usage);

public sealed record DeepSeekBalance(bool IsAvailable, string Currency, decimal TotalBalance, decimal GrantedBalance);

/// <summary>
/// Reads status data from the local DSH host over its loopback RPC API
/// (workspace context, per-session token projections) and the DeepSeek
/// platform balance endpoint. JSON parsing is tolerant: fields are looked up
/// by name and missing entries contribute zero.
/// </summary>
public sealed class DshStatusClient : IDisposable
{
    private static readonly JsonSerializerOptions RequestOptions = new();

    private readonly HttpClient _client;
    private int _rpcId;

    public DshStatusClient(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<DshStatusSnapshot> GetStatusAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        JsonElement workspaces = await PostMethodAsync(endpoint, "workspace.list", new { }, cancellationToken);
        JsonElement sessions = await PostMethodAsync(endpoint, "session.list", new { }, cancellationToken);
        JsonElement describe = await PostMethodAsync(endpoint, "host.describe", new { }, cancellationToken);

        return new DshStatusSnapshot(
            ParseLatestWorkspace(workspaces),
            GetString(describe, "provider"),
            GetString(describe, "model"),
            ParseUsage(sessions));
    }

    /// <summary>
    /// Queries the DeepSeek platform balance with the user's API key.
    /// Returns null when the endpoint refuses the call (proxy keys, offline).
    /// </summary>
    public async Task<DeepSeekBalance?> GetBalanceAsync(string apiKey, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), "/user/balance"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement root = document.RootElement;
            bool isAvailable = root.TryGetProperty("is_available", out var availableElement)
                && availableElement.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("balance_infos", out var infos)
                || infos.ValueKind != JsonValueKind.Array
                || infos.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement info = infos[0];
            return new DeepSeekBalance(
                isAvailable,
                GetString(info, "currency") ?? "CNY",
                GetDecimal(info, "total_balance"),
                GetDecimal(info, "granted_balance"));
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or UriFormatException)
        {
            return null;
        }
    }

    private async Task<JsonElement> PostMethodAsync(
        Uri endpoint,
        string method,
        object payload,
        CancellationToken cancellationToken)
    {
        int rpcId = Interlocked.Increment(ref _rpcId);
        string body = JsonSerializer.Serialize(
            new { type = "client-request", rpcId = $"cetus-{rpcId}", method, payload },
            RequestOptions);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.PostAsync(
            new Uri(endpoint, $"/api/{method}"),
            content,
            cancellationToken);
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

    private static DshWorkspaceInfo? ParseLatestWorkspace(JsonElement value)
    {
        JsonElement? latest = null;
        DateTime latestUpdated = DateTime.MinValue;
        foreach (JsonElement item in EnumerateArray(value, "items"))
        {
            DateTime updated = GetDateTime(item, "updatedAt");
            if (latest is null || updated > latestUpdated)
            {
                latest = item.Clone();
                latestUpdated = updated;
            }
        }

        if (latest is null)
        {
            return null;
        }

        int sessionCount = latest.Value.TryGetProperty("sessionIds", out var ids)
            && ids.ValueKind == JsonValueKind.Array
            ? ids.GetArrayLength()
            : 0;
        return new DshWorkspaceInfo(
            GetString(latest.Value, "title") ?? "未命名工作区",
            GetString(latest.Value, "path") ?? string.Empty,
            sessionCount,
            latestUpdated);
    }

    private static DshUsageSummary ParseUsage(JsonElement value)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, turns = 0, steps = 0;
        int sessionCount = 0;
        foreach (JsonElement item in EnumerateArray(value, "items"))
        {
            sessionCount++;
            if (!TryGetProjection(item, "tokenUsage", out JsonElement usage))
            {
                continue;
            }

            input += GetInt64(usage, "uncachedInputTokens");
            output += GetInt64(usage, "outputTokens");
            cacheRead += GetInt64(usage, "cacheReadTokens");
            cacheWrite += GetInt64(usage, "cacheWriteTokens");

            if (TryGetProjection(item, "sessionStats", out JsonElement stats))
            {
                turns += GetInt64(stats, "turns");
                steps += GetInt64(stats, "steps");
            }
        }

        return new DshUsageSummary(input, output, cacheRead, cacheWrite, turns, steps, sessionCount);
    }

    private static bool TryGetProjection(JsonElement sessionItem, string name, out JsonElement projection)
    {
        projection = default;
        return sessionItem.TryGetProperty("projections", out JsonElement projections)
            && projections.ValueKind == JsonValueKind.Object
            && projections.TryGetProperty("values", out JsonElement values)
            && values.ValueKind == JsonValueKind.Object
            && values.TryGetProperty(name, out projection)
            && projection.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out JsonElement array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTime GetDateTime(JsonElement element, string name) =>
        DateTime.TryParse(GetString(element, name), out DateTime parsed) ? parsed : DateTime.MinValue;

    private static long GetInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out long value)
            ? value
            : 0;

    private static decimal GetDecimal(JsonElement element, string name) =>
        decimal.TryParse(GetString(element, name), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal value)
            ? value
            : 0;

    public void Dispose() => _client.Dispose();
}
