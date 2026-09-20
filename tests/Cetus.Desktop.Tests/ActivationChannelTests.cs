using Cetus.Application;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class ActivationChannelTests
{
    private static string UniqueId() => "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ForwardedWorkspace_ReachesServerEvent()
    {
        string id = UniqueId();
        TaskCompletionSource<ActivationRequest> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new ActivationChannel(id, request => received.TrySetResult(request));
        server.Start();

        bool forwarded = await ActivationChannel.TryForwardAsync(@"F:\repos\demo", id);

        Assert.True(forwarded);
        ActivationRequest request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"F:\repos\demo", request.WorkspacePath);
    }

    [Fact]
    public async Task PlainForward_SendsNullWorkspace()
    {
        string id = UniqueId();
        TaskCompletionSource<ActivationRequest> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new ActivationChannel(id, request => received.TrySetResult(request));
        server.Start();

        bool forwarded = await ActivationChannel.TryForwardAsync(null, id);

        Assert.True(forwarded);
        ActivationRequest request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(request.WorkspacePath);
    }

    [Fact]
    public async Task TryForward_WithoutServer_ReturnsFalse()
    {
        // A unique id keeps the probe away from any really running instance.
        bool forwarded = await ActivationChannel.TryForwardAsync(
            @"F:\repos\nobody-home", UniqueId());

        Assert.False(forwarded);
    }

    [Fact]
    public void ParseMessage_ReadsWorkspaceAndIgnoresForeignPayloads()
    {
        Assert.Equal(@"F:\repos\demo", ActivationChannel.ParseMessage("""{"workspace": "F:\\repos\\demo"}"""));
        Assert.Null(ActivationChannel.ParseMessage("""{"workspace": null}"""));
        Assert.Null(ActivationChannel.ParseMessage("null"));
        Assert.Null(ActivationChannel.ParseMessage("{ not json"));
        Assert.Null(ActivationChannel.ParseMessage("""{"other": 1}"""));
    }

    [Fact]
    public void InstanceIdSuffix_IsolatesPipeNames()
    {
        Assert.Equal("Cetus.Desktop.Activation", ActivationChannel.BuildPipeName(null));
        Assert.Equal("Cetus.Desktop.Activation", ActivationChannel.BuildPipeName("  "));
        Assert.Equal("Cetus.Desktop.Activation.smoke", ActivationChannel.BuildPipeName("smoke"));
    }
}
