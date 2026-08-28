using System.Security.Cryptography;
using System.Text.Json;

namespace Cetus.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    string TagName,
    Version Version,
    string? Notes,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Pure parsing and selection logic over GitHub release payloads. Network
/// access lives in <see cref="UpdateService"/>; this type stays testable
/// without any I/O.
/// </summary>
public static class UpdateFeed
{
    private const string InstallerPrefix = "Cetus-Setup-";
    private const string ChecksumFileName = "SHA256SUMS.txt";

    /// <summary>Parses a releases/latest JSON payload; null when unusable.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out JsonElement tagElement)
                || tagElement.ValueKind != JsonValueKind.String
                || !TryParseTag(tagElement.GetString(), out Version version))
            {
                return null;
            }

            string? notes = root.TryGetProperty("body", out JsonElement bodyElement)
                && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out JsonElement assetsElement)
                && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assetsElement.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object
                        || !asset.TryGetProperty("name", out JsonElement nameElement)
                        || nameElement.ValueKind != JsonValueKind.String
                        || !asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                        || urlElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    long size = asset.TryGetProperty("size", out JsonElement sizeElement)
                        && sizeElement.ValueKind == JsonValueKind.Number
                        && sizeElement.TryGetInt64(out long parsedSize)
                        ? parsedSize
                        : 0;
                    assets.Add(new ReleaseAsset(nameElement.GetString()!, urlElement.GetString()!, size));
                }
            }

            return new ReleaseInfo(tagElement.GetString()!, version, notes, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Accepts "v0.2.0", "V0.2.0" and "0.2.0".</summary>
    public static bool TryParseTag(string? tagName, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        string candidate = tagName.Trim();
        if (candidate.Length > 1 && (candidate[0] is 'v' or 'V'))
        {
            candidate = candidate[1..];
        }

        return Version.TryParse(candidate, out version!) && version.Revision <= 0;
    }

    public static ReleaseAsset? SelectInstallerAsset(ReleaseInfo release)
    {
        string expected = $"{InstallerPrefix}{release.Version}.exe";
        return FindAsset(release, expected);
    }

    public static ReleaseAsset? SelectChecksumAsset(ReleaseInfo release) =>
        FindAsset(release, ChecksumFileName);

    /// <summary>
    /// Looks up the hash for <paramref name="fileName"/> in sha256sum-style
    /// content ("&lt;hex&gt;&lt;two spaces&gt;&lt;name&gt;"). Null when absent.
    /// </summary>
    public static string? FindChecksum(string sumsContent, string fileName)
    {
        foreach (string line in sumsContent.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            int separator = trimmed.IndexOf("  ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string name = trimmed[(separator + 2)..].Trim();
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                string hash = trimmed[..separator].Trim();
                return hash.Length == 64 && IsHex(hash) ? hash.ToLowerInvariant() : null;
            }
        }

        return null;
    }

    /// <summary>Compares a file's SHA-256 against the sums entry; false with a reason on any mismatch.</summary>
    public static bool VerifyChecksum(string sumsContent, string fileName, string actualHashLower, out string? error)
    {
        string? expected = FindChecksum(sumsContent, fileName);
        if (expected is null)
        {
            error = $"SHA256SUMS 中没有 {fileName} 的校验条目。";
            return false;
        }

        if (!expected.Equals(actualHashLower, StringComparison.OrdinalIgnoreCase))
        {
            error = $"安装器校验失败：SHA-256 与 SHA256SUMS 不一致。";
            return false;
        }

        error = null;
        return true;
    }

    public static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static ReleaseAsset? FindAsset(ReleaseInfo release, string name) =>
        release.Assets.FirstOrDefault(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
