using System.Security.Cryptography;
using System.Text;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshAuthTests
{
    [Fact]
    public void CreateSessionCookie_ComputesDeterministicValidCookie()
    {
        var endpoint = new Uri("http://127.0.0.1:3080/");
        byte[] secretBytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            secretBytes[i] = (byte)(i + 1);
        }
        string secret = DshAuth.Base64Url(secretBytes);

        var cookie = DshAuth.CreateSessionCookie(endpoint, secret);

        // Name starts with dsh-auth- followed by Base64Url SHA256 of "127.0.0.1:3080"
        byte[] authorityHash = SHA256.HashData(Encoding.UTF8.GetBytes("127.0.0.1:3080"));
        string expectedName = "dsh-auth-" + DshAuth.Base64Url(authorityHash);
        Assert.Equal(expectedName, cookie.Name);

        // Value starts with v1.
        Assert.StartsWith("v1.", cookie.Value);
        string[] parts = cookie.Value.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("v1", parts[0]);

        // Decode payload
        byte[] payloadBytes = DshAuth.DecodeBase64Url(parts[1]);
        string payloadJson = Encoding.UTF8.GetString(payloadBytes);
        Assert.Contains("\"authority\":\"127.0.0.1:3080\"", payloadJson);
        Assert.Contains("\"version\":1", payloadJson);

        // Verify HMAC signature
        using var hmac = new HMACSHA256(secretBytes);
        byte[] expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[1]));
        string expectedSigB64 = DshAuth.Base64Url(expectedSig);
        Assert.Equal(expectedSigB64, parts[2]);
    }

    [Fact]
    public void EnsureSessionSecret_CreatesNewFileWhenAbsent_AndReadsExisting()
    {
        string dir = TestWorkspace.CreateDirectory();
        try
        {
            string secret1 = DshAuth.EnsureSessionSecret(dir);
            Assert.False(string.IsNullOrWhiteSpace(secret1));

            // Calling again returns the exact same secret
            string secret2 = DshAuth.EnsureSessionSecret(dir);
            Assert.Equal(secret1, secret2);

            string? read = DshAuth.ReadSessionSecret(dir);
            Assert.Equal(secret1, read);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void TryGetSessionCookie_ReturnsNull_WhenCredentialsMissing()
    {
        string dir = TestWorkspace.CreateDirectory();
        try
        {
            var cookie = DshAuth.TryGetSessionCookie(new Uri("http://127.0.0.1:3080/"), dir);
            Assert.Null(cookie);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsureSessionSecret_OnEmptyExistingFile_KeepsTheVersionHeader()
    {
        string dir = TestWorkspace.CreateDirectory();
        try
        {
            // DSH parses this file strictly and refuses to boot without the
            // top-level version header, so extending a pre-existing file must
            // not drop it — a failed boot here becomes a restart loop.
            string path = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(path, string.Empty);

            string secret = DshAuth.EnsureSessionSecret(dir);

            string content = File.ReadAllText(path);
            Assert.Contains("version: 1", content);
            Assert.Contains("records:", content);
            Assert.Contains("client-connection/browser-session:", content);
            Assert.Contains($"secret: {secret}", content);
            Assert.Equal(secret, DshAuth.ReadSessionSecret(dir));
            Assert.False(File.Exists(path + ".cetus.tmp"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsureSessionSecret_OnExistingCredentialFile_PreservesOtherEntries()
    {
        string dir = TestWorkspace.CreateDirectory();
        try
        {
            string path = Path.Combine(dir, ".credentials.yaml");
            File.WriteAllText(
                path,
                """
                version: 1
                refs:
                  DEEPSEEK_API_KEY: sk-test
                """);

            string secret = DshAuth.EnsureSessionSecret(dir);

            string content = File.ReadAllText(path);
            Assert.Contains("DEEPSEEK_API_KEY: sk-test", content);
            Assert.Contains("secret: " + secret, content);

            // Exactly one document-level version line: the nested payload has
            // its own "version: 1" further in, indented.
            string[] topLevelLines = content
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("version:", StringComparison.Ordinal))
                .ToArray();
            Assert.Single(topLevelLines);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
