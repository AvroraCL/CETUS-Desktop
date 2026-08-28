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
        if (localCandidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || localCandidate.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            || localCandidate.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || localCandidate.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase))
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
}
