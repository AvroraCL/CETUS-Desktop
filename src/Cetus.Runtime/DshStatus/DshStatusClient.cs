using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Cetus.DshStatus;

public sealed record DshTokenUsage(long UncachedInputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

public sealed record DshContextPressure(double? ProjectedTokens, double? PressureTokens, long? ContextWindow)
{
    /// <summary>Occupancy percent of the next request against the context window; null when unknown.</summary>
    public double? OccupancyPercent => ContextWindow is > 0
        ? (ProjectedTokens ?? PressureTokens) / (double)ContextWindow * 100
        : null;
}

public sealed record DshTodo(string Content, string Status);

public sealed record DshSessionDetail(
    string SessionId,
    string Title,
    string Cwd,
    bool Running,
    DateTime UpdatedAt,
    DshTokenUsage? Usage,
    long Turns,
    long Steps,
    long LlmMilliseconds,
    long ToolMilliseconds,
    DshContextPressure? Pressure,
    IReadOnlyList<DshTodo> Todos);

public sealed record DshWorkspaceInfo(
    string WorkspaceId,
    string Title,
    string Path,
    int SessionCount,
    DateTime UpdatedAt);

public sealed record DshUsageSummary(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long Turns,
    long Steps,
    long LlmMilliseconds,
    long ToolMilliseconds,
    int SessionCount)
{
    public static DshUsageSummary Sum(IEnumerable<DshSessionDetail> sessions)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, turns = 0, steps = 0, llmMs = 0, toolMs = 0, count = 0;
        foreach (DshSessionDetail session in sessions)
        {
            count++;
            if (session.Usage is { } usage)
            {
                input += usage.UncachedInputTokens;
                output += usage.OutputTokens;
                cacheRead += usage.CacheReadTokens;
                cacheWrite += usage.CacheWriteTokens;
            }

            turns += session.Turns;
            steps += session.Steps;
            llmMs += session.LlmMilliseconds;
            toolMs += session.ToolMilliseconds;
        }

        return new DshUsageSummary(input, output, cacheRead, cacheWrite, turns, steps, llmMs, toolMs, (int)count);
    }
}

public sealed record DshStatusSnapshot(
    DshWorkspaceInfo? Workspace,
    string? Provider,
    string? Model,
    DshUsageSummary Usage,
    IReadOnlyList<DshSessionDetail> Sessions);

public sealed record DeepSeekBalance(bool IsAvailable, string Currency, decimal TotalBalance, decimal GrantedBalance);

/// <summary>
/// Reads status data from the local DSH host over its loopback RPC API
/// (workspaces, per-session projections including token usage, context
/// pressure and todos; session create/cancel) and the DeepSeek platform
/// balance endpoint. JSON parsing is tolerant: fields are looked up by name
/// and missing entries contribute zero or null.
/// </summary>
public sealed class DshStatusClient : IDisposable
{
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

        var details = ParseSessions(sessions).OrderBy(session => session.UpdatedAt).ToList();
        return new DshStatusSnapshot(
            ParseLatestWorkspace(workspaces),
            GetString(describe, "provider"),
            GetString(describe, "model"),
            DshUsageSummary.Sum(details),
            details);
    }

    /// <summary>Creates a session in a workspace; returns the new session id.</summary>
    public async Task<string> CreateSessionAsync(Uri endpoint, string workspaceId, CancellationToken cancellationToken)
    {
        JsonElement value = await PostMethodAsync(endpoint, "session.create", new { workspaceId }, cancellationToken);
        return GetString(value, "sessionId") ?? throw new InvalidOperationException("DSH 未返回新会话 id。");
    }

    /// <summary>Cancels the running agent loop of a session.</summary>
    public async Task CancelSessionAsync(Uri endpoint, string sessionId, CancellationToken cancellationToken)
    {
        await PostMethodAsync(endpoint, "session.cancel", new { sessionId }, cancellationToken);
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
            new { type = "client-request", rpcId = $"cetus-{rpcId}", method, payload });

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
            GetString(latest.Value, "workspaceId") ?? string.Empty,
            GetString(latest.Value, "title") ?? "未命名工作区",
            GetString(latest.Value, "path") ?? string.Empty,
            sessionCount,
            latestUpdated);
    }

    private static List<DshSessionDetail> ParseSessions(JsonElement value)
    {
        var sessions = new List<DshSessionDetail>();
        foreach (JsonElement item in EnumerateArray(value, "items"))
        {
            string cwd = GetString(item, "cwd") ?? string.Empty;
            string title = GetString(item, "cwd") is { } cwdValue
                ? Path.GetFileName(cwdValue.TrimEnd(Path.DirectorySeparatorChar))
                : "会话";
            bool running = item.TryGetProperty("running", out var runningElement)
                && runningElement.ValueKind == JsonValueKind.True;

            DshTokenUsage? usage = null;
            if (TryGetProjection(item, "tokenUsage", out JsonElement usageElement))
            {
                usage = new DshTokenUsage(
                    GetInt64(usageElement, "uncachedInputTokens"),
                    GetInt64(usageElement, "outputTokens"),
                    GetInt64(usageElement, "cacheReadTokens"),
                    GetInt64(usageElement, "cacheWriteTokens"));
            }

            long turns = 0, steps = 0, llmMs = 0, toolMs = 0;
            if (TryGetProjection(item, "sessionStats", out JsonElement stats))
            {
                turns = GetInt64(stats, "turns");
                steps = GetInt64(stats, "steps");
                llmMs = GetInt64(stats, "llmMs");
                toolMs = GetInt64(stats, "toolMs");
            }

            DshContextPressure? pressure = null;
            if (TryGetProjection(item, "contextPressure", out JsonElement pressureElement))
            {
                double? projected = GetNullableDouble(pressureElement, "projectedTokens");
                double? raw = GetNullableDouble(pressureElement, "pressureTokens");
                long? window = pressureElement.TryGetProperty("contextWindow", out var windowElement)
                    && windowElement.ValueKind == JsonValueKind.Number
                    && windowElement.TryGetInt64(out long parsedWindow)
                    ? parsedWindow
                    : null;
                if (projected is not null || raw is not null || window is not null)
                {
                    pressure = new DshContextPressure(projected, raw, window);
                }
            }

            var todos = new List<DshTodo>();
            if (TryGetProjection(item, "todos", out JsonElement todosElement)
                && todosElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement todo in todosElement.EnumerateArray())
                {
                    string? content = GetString(todo, "content");
                    if (content is not null)
                    {
                        todos.Add(new DshTodo(content, GetString(todo, "status") ?? "pending"));
                    }
                }
            }

            if (TryGetProjection(item, "title", out JsonElement titleElement)
                && GetString(titleElement, "title") is { } projectedTitle)
            {
                title = projectedTitle;
            }

            sessions.Add(new DshSessionDetail(
                GetString(item, "sessionId") ?? string.Empty,
                title,
                cwd,
                running,
                GetEpochTime(item, "updatedAt"),
                usage,
                turns,
                steps,
                llmMs,
                toolMs,
                pressure,
                todos));
        }

        return sessions;
    }

    private static bool TryGetProjection(JsonElement sessionItem, string name, out JsonElement projection)
    {
        projection = default;
        return sessionItem.TryGetProperty("projections", out JsonElement projections)
            && projections.ValueKind == JsonValueKind.Object
            && projections.TryGetProperty("values", out JsonElement values)
            && values.ValueKind == JsonValueKind.Object
            && values.TryGetProperty(name, out projection)
            && projection.ValueKind != JsonValueKind.Null
            && projection.ValueKind != JsonValueKind.Undefined;
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

    private static DateTime GetEpochTime(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long epochMs))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime;
            }

            return GetDateTime(element, name);
        }

        return DateTime.MinValue;
    }

    private static long GetInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out long value)
            ? value
            : 0;

    private static double? GetNullableDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.GetDouble() is { } value
            ? value
            : null;

    private static decimal GetDecimal(JsonElement element, string name) =>
        decimal.TryParse(GetString(element, name), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal value)
            ? value
            : 0;

    public void Dispose() => _client.Dispose();
}
