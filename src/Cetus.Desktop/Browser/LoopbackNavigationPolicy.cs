namespace Cetus.Browser;

/// <summary>
/// Immutable origin policy for the embedded browser. Paths may vary, but
/// scheme, host and port must match the configured loopback DSH endpoint.
/// </summary>
internal sealed class LoopbackNavigationPolicy
{
    private readonly Uri _origin;

    public LoopbackNavigationPolicy(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri
            || !origin.IsLoopback
            || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(origin.UserInfo))
        {
            throw new ArgumentException("嵌入式浏览器只允许无凭据的 HTTP(S) 回环地址。", nameof(origin));
        }
        _origin = origin;
    }

    public bool Allows(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? candidate))
        {
            return false;
        }

        return string.Equals(candidate.Scheme, _origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == _origin.Port
            && string.IsNullOrEmpty(candidate.UserInfo);
    }
}
