namespace Cetus.Sidebar;

internal static class SidebarBrowserAddress
{
    private static readonly Uri BlankPage = new("about:blank");

    public static Uri Resolve(string? input)
    {
        string value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return BlankPage;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)
            && IsAllowed(absolute))
        {
            return absolute;
        }

        string localCandidate = value.TrimEnd('/');
        if (IsLoopbackHost(localCandidate, "localhost")
            || IsLoopbackHost(localCandidate, "127.0.0.1")
            || IsLoopbackHost(localCandidate, "[::1]"))
        {
            return new Uri($"http://{value}");
        }

        if (!value.Any(char.IsWhiteSpace)
            && value.Contains('.')
            && Uri.TryCreate($"https://{value}", UriKind.Absolute, out Uri? host))
        {
            return host;
        }

        return new Uri($"https://www.bing.com/search?q={Uri.EscapeDataString(value)}");
    }

    public static bool IsAllowed(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);

    /// <summary>Matches the bare loopback host or host:port, never longer lookalike names.</summary>
    private static bool IsLoopbackHost(string candidate, string host) =>
        candidate.Equals(host, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith($"{host}:", StringComparison.OrdinalIgnoreCase);
}
