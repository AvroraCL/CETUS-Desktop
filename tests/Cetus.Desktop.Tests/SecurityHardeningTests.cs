using System.Net;
using System.Net.Sockets;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class LoopbackBindingGuardTests
{
    [Fact]
    public void IsLoopbackOnly_LoopbackListener_ReturnsTrue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(LoopbackBindingGuard.IsLoopbackOnly(port));
    }

    [Fact]
    public void IsLoopbackOnly_WildcardListener_ReturnsFalse()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(LoopbackBindingGuard.IsLoopbackOnly(port));
    }

    [Fact]
    public void IsLoopbackOnly_NotListening_ReturnsTrue()
    {
        // A quiet port exposes nothing; treat it as safe.
        int port = FreePortFinder.Reserve();

        Assert.True(LoopbackBindingGuard.IsLoopbackOnly(port));
    }
}

public sealed class CredentialGuardTests
{
    [Fact]
    public void IsBroadPrincipal_MatchesWellKnownGroups()
    {
        Assert.True(CredentialGuard.IsBroadPrincipal(
            new System.Security.Principal.SecurityIdentifier("S-1-5-32-545")));  // BUILTIN\Users
        Assert.True(CredentialGuard.IsBroadPrincipal(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0")));       // Everyone
        Assert.True(CredentialGuard.IsBroadPrincipal(
            new System.Security.Principal.SecurityIdentifier("S-1-5-11")));      // Authenticated Users
        Assert.False(CredentialGuard.IsBroadPrincipal(
            new System.Security.Principal.SecurityIdentifier("S-1-5-18")));      // SYSTEM
    }

    [Fact]
    public void EnsureUserOnlyAccess_RealFile_NeverFailsForCurrentUser()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, ".credentials.yaml");
        File.WriteAllText(path, "records:\n  client-connection/browser-session:\n");

        CredentialGuard.AccessState state = CredentialGuard.EnsureUserOnlyAccess(path);

        // Either outcome is acceptable on CI; the guard must never throw and
        // the file must survive untouched in content.
        Assert.NotEqual(CredentialGuard.AccessState.Failed, state);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void EnsureUserOnlyAccess_MissingFile_ReportsFailedWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "missing.yaml");

        Assert.Equal(CredentialGuard.AccessState.Failed, CredentialGuard.EnsureUserOnlyAccess(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

        public string Path { get; }

        public void Dispose()
        {
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }
}
