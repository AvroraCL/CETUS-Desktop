using System.Text.Json;
using Cetus.DshStatus;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshFollowFramesTests
{
    [Fact]
    public void TryParseEvent_TurnEnd_ExtractsTypeSeqAndReasonKind()
    {
        var frame = Json.Parse("""
            {
              "type": "event",
              "event": {
                "type": "turn/end",
                "seq": 42,
                "time": 1758400000000,
                "data": { "turn": 3, "reason": { "kind": "completed" } }
              }
            }
            """);

        DshFollowEvent? parsed = DshFollowFrames.TryParseEvent(frame);

        Assert.NotNull(parsed);
        Assert.Equal("turn/end", parsed.Value.EventType);
        Assert.Equal(42, parsed.Value.Seq);
        Assert.Equal("completed", parsed.Value.TurnEndKind);
    }

    [Fact]
    public void TryParseEvent_NonEventFrames_ReturnNull()
    {
        Assert.Null(DshFollowFrames.TryParseEvent(Json.Parse(
            """{ "type": "snapshot", "cursor": 1, "records": [] }""")));
        Assert.Null(DshFollowFrames.TryParseEvent(Json.Parse(
            """{ "type": "assistant-stream", "frame": {} }""")));
        Assert.Null(DshFollowFrames.TryParseEvent(Json.Parse("null")));
    }

    [Fact]
    public void TryParseEvent_MalformedEvent_DegradesGracefully()
    {
        var frame = Json.Parse("""
            { "type": "event", "event": { "seq": "not-a-number" } }
            """);

        DshFollowEvent? parsed = DshFollowFrames.TryParseEvent(frame);

        Assert.Null(parsed);
    }

    [Fact]
    public void FrameType_ReturnsTheDiscriminator()
    {
        Assert.Equal("event", DshFollowFrames.FrameType(Json.Parse("""{ "type": "event" }""")));
        Assert.Null(DshFollowFrames.FrameType(Json.Parse("""{ "nope": true }""")));
        Assert.Null(DshFollowFrames.FrameType(Json.Parse("[]")));
    }
}

internal static class Json
{
    public static JsonElement Parse(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);
}
