using System.ComponentModel;
using System.Diagnostics;

namespace Cetus.Platform;

/// <summary>
/// The single place that hands a URL to the system browser. Validation
/// matches the WebView2 shell rule: only http/https may be shell-executed,
/// everything else is ignored (file:, custom schemes must never escape).
/// </summary>
internal static class SystemBrowser
{
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // External launch is best effort.
        }
    }
}
