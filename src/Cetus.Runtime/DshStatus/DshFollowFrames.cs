using System.Text.Json;

namespace Cetus.DshStatus;

public readonly record struct DshFollowEvent(string EventType, long Seq, string? TurnEndKind);

/// <summary>
/// Tolerant parser for <c>session/follow</c> stream items carried by the mux
/// (see docs/dev/dsh-mux-protocol.md). A frame is
/// <c>{ type: "event", event: { type, seq, data } }</c>; snapshots and
/// assistant-stream frames are recognized but not modeled here.
/// </summary>
public static class DshFollowFrames
{
    public const string FrameEvent = "event";
    public const string FrameSnapshot = "snapshot";
    public const string FrameAssistantStream = "assistant-stream";

    /// <summary>The frame discriminator, or null when the value is not a follow frame.</summary>
    public static string? FrameType(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || item.TryGetProperty("type", out JsonElement type) is false
            || type.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return type.GetString();
    }

    /// <summary>
    /// Parses an <c>event</c> frame into its type, sequence and (for
    /// <c>turn/end</c>) the turn-end reason kind. Returns null for any other
    /// frame type or a malformed event.
    /// </summary>
    public static DshFollowEvent? TryParseEvent(JsonElement item)
    {
        if (FrameType(item) != FrameEvent)
        {
            return null;
        }

        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("event", out JsonElement e)
            || e.ValueKind != JsonValueKind.Object
            || e.TryGetProperty("type", out JsonElement eventType) is false
            || eventType.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        long seq = e.TryGetProperty("seq", out JsonElement seqElement)
            && seqElement.ValueKind == JsonValueKind.Number
            && seqElement.TryGetInt64(out long parsed)
            ? parsed
            : 0;

        string? turnEndKind = null;
        if (eventType.GetString() == "turn/end"
            && e.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("reason", out JsonElement reason)
            && reason.ValueKind == JsonValueKind.Object
            && reason.TryGetProperty("kind", out JsonElement reasonKind)
            && reasonKind.ValueKind == JsonValueKind.String)
        {
            turnEndKind = reasonKind.GetString();
        }

        return new DshFollowEvent(eventType.GetString()!, seq, turnEndKind);
    }
}
