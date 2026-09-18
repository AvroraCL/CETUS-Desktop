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
}
