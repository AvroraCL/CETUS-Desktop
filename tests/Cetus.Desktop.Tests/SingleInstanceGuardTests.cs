using Cetus.Application;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void Acquire_OnlyOneGuardOwnsTheSameIdentityAtATime()
    {
        string identity = $"test-{Guid.NewGuid():N}";
        using SingleInstanceGuard first = SingleInstanceGuard.Acquire(identity);
        using SingleInstanceGuard second = SingleInstanceGuard.Acquire(identity);

        Assert.True(first.IsPrimaryInstance);
        Assert.False(second.IsPrimaryInstance);
    }

    [Fact]
    public void Dispose_ReleasesTheIdentityForTheNextGuard()
    {
        string identity = $"test-{Guid.NewGuid():N}";
        using (SingleInstanceGuard first = SingleInstanceGuard.Acquire(identity))
        {
            Assert.True(first.IsPrimaryInstance);
        }

        using SingleInstanceGuard next = SingleInstanceGuard.Acquire(identity);
        Assert.True(next.IsPrimaryInstance);
    }
}
