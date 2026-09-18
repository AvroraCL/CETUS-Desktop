using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cetus.DshStatus;

namespace Cetus.Hosting;

/// <summary>
/// Manages browser-session authentication introduced in DSH 0.1.5/0.1.6.
/// Generates and validates the HMAC-SHA256 signed cookie bound to authority
/// and client-connection/browser-session secret in .credentials.yaml.
/// </summary>
public static class DshAuth
{
    private const int SecretBytes = 32;
    private const string CookiePrefix = "dsh-auth-";

    public readonly record struct SessionCookie(string Name, string Value);

    public static SessionCookie? TryGetSessionCookie(Uri endpoint, string? dshHomeOverride = null)
    {
        string? secret = ReadSessionSecret(dshHomeOverride);
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        return CreateSessionCookie(endpoint, secret);
    }

    public static SessionCookie CreateSessionCookie(Uri endpoint, string secret)
    {
        string authority = $"{endpoint.Host}:{endpoint.Port}";
        byte[] authorityHash = SHA256.HashData(Encoding.UTF8.GetBytes(authority));
        string cookieName = CookiePrefix + Base64Url(authorityHash);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long expiresAt = now + (30L * 24 * 60 * 60 * 1000);
        string payload = $"{{\"version\":1,\"authority\":\"{authority}\",\"issuedAt\":{now},\"expiresAt\":{expiresAt}}}";
        string body = Base64Url(Encoding.UTF8.GetBytes(payload));

        byte[] secretBytes = DecodeBase64Url(secret);
        using var hmac = new HMACSHA256(secretBytes);
        byte[] sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        string signature = Base64Url(sigBytes);

        return new SessionCookie(cookieName, $"v1.{body}.{signature}");
    }

    public static string EnsureSessionSecret(string? dshHomeOverride = null)
    {
        string? existing = ReadSessionSecret(dshHomeOverride);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        string dshHome = DshCredentials.ResolveDshHome(dshHomeOverride);
        Directory.CreateDirectory(dshHome);
        string path = Path.Combine(dshHome, ".credentials.yaml");

        byte[] bytes = new byte[SecretBytes];
        RandomNumberGenerator.Fill(bytes);
        string newSecret = Base64Url(bytes);

        WriteSessionSecret(path, newSecret);
        return newSecret;
    }

    public static string? ReadSessionSecret(string? dshHomeOverride = null)
    {
        string dshHome = DshCredentials.ResolveDshHome(dshHomeOverride);
        string path = Path.Combine(dshHome, ".credentials.yaml");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            bool inSessionRecord = false;
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("client-connection/browser-session", StringComparison.Ordinal))
                {
                    inSessionRecord = true;
                    continue;
                }

                if (inSessionRecord)
                {
                    if (trimmed.StartsWith("secret:", StringComparison.Ordinal))
                    {
                        int separator = trimmed.IndexOf(':');
                        if (separator > 0)
                        {
                            string val = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                            return val.Length > 0 ? val : null;
                        }
                    }
                    else if (!line.StartsWith(' ') && !line.StartsWith('\t') && trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    {
                        inSessionRecord = false;
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static void WriteSessionSecret(string path, string secret)
    {
        try
        {
            if (File.Exists(path))
            {
                string content = File.ReadAllText(path);
                if (content.Contains("client-connection/browser-session", StringComparison.Ordinal))
                {
                    return;
                }

                var sb = new StringBuilder(content.TrimEnd());
                sb.AppendLine();
                if (!content.Contains("records:", StringComparison.Ordinal))
                {
                    sb.AppendLine("records:");
                }
                sb.AppendLine("  client-connection/browser-session:");
                sb.AppendLine("    kind: grant");
                sb.AppendLine("    payload:");
                sb.AppendLine("      version: 1");
                sb.AppendLine($"      secret: {secret}");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            else
            {
                string content = $"""
                    version: 1
                    records:
                      client-connection/browser-session:
                        kind: grant
                        payload:
                          version: 1
                          secret: {secret}

                    """;
                File.WriteAllText(path, content, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] DecodeBase64Url(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }
}
