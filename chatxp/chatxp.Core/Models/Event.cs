using System.Text.Json.Serialization;

namespace chatxp.core;

/// <summary>
/// Event types exchanged over the WebRTC data channel between peers.
/// Mirrors EventTypes in chat/Data.swift. The wire values are lowercase strings
/// ("send", "delete", "typing", "endCall"); offer/answer/candidate/incoming also
/// exist in the enum but are signaling-side values.
/// </summary>
public enum EventTypes
{
    Send,
    Delete,
    Offer,
    Answer,
    Candidate,
    Incoming,
    Typing,
    EndCall
}

internal static class EventTypeNames
{
    // JSON names exactly as Swift's Codable encodes EventTypes (String raw values).
    public const string Send = "send";
    public const string Delete = "delete";
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string Candidate = "candidate";
    public const string Incoming = "incoming";
    public const string Typing = "typing";
    public const string EndCall = "endCall";

    public static string ToWire(EventTypes type) => type switch
    {
        EventTypes.Send => Send,
        EventTypes.Delete => Delete,
        EventTypes.Offer => Offer,
        EventTypes.Answer => Answer,
        EventTypes.Candidate => Candidate,
        EventTypes.Incoming => Incoming,
        EventTypes.Typing => Typing,
        EventTypes.EndCall => EndCall,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static EventTypes? FromWire(string? raw) => raw switch
    {
        Send => EventTypes.Send,
        Delete => EventTypes.Delete,
        Offer => EventTypes.Offer,
        Answer => EventTypes.Answer,
        Candidate => EventTypes.Candidate,
        Incoming => EventTypes.Incoming,
        Typing => EventTypes.Typing,
        EndCall => EventTypes.EndCall,
        _ => null,
    };
}

/// <summary>
/// The P2P message envelope sent over the WebRTC data channel between peers.
/// Mirrors `Event` in chat/Data.swift:
///   { "id": "UPPERCASE-UUID", "type": "send", "payload": "&lt;base64 AES-GCM&gt;" }
/// Swift's JSONEncoder writes camelCase keys and omits nulls (encodeIfPresent);
/// UUIDs are encoded as their uppercase hyphenated string. This class reproduces
/// both behaviors so its JSON is byte-identical to the Swift app's.
/// </summary>
public sealed class Event
{
    /// <summary>Upper-case hyphenated UUID string (Swift UUID().uuidString style).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = NewId();

    /// <summary>Wire name of the event type ("send", "typing", ...).</summary>
    [JsonPropertyName("type")]
    public string TypeName { get; init; } = EventTypeNames.Send;

    [JsonIgnore]
    public EventTypes? KnownType => EventTypeNames.FromWire(TypeName);

    [JsonIgnore]
    public EventTypes Type => KnownType ?? EventTypes.Send;

    /// <summary>True for wire types this build understands. Unknown values are dropped
    /// at the boundary instead of being mistaken for chat messages.</summary>
    [JsonIgnore]
    public bool IsKnownType => KnownType is not null;

    /// <summary>The encrypted payload (Base64 AES-GCM combined blob), or for control
    /// events (typing/endCall) the plain convention Swift uses.</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    /// <summary>UUID string of the Event being replied to, or null (omitted on the wire).</summary>
    [JsonPropertyName("replyingTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyingTo { get; init; }

    /// <summary>CRDT: public key of the op's author. Older peers omit it; the receiver
    /// defaults it to the connected peer. Omitted from JSON when null so legacy
    /// Swift/.NET decoders see the exact event shape they already understand.</summary>
    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; init; }

    /// <summary>CRDT: author wall-clock milliseconds at creation. Older peers omit it;
    /// the receiver defaults it to receipt time. Part of the deterministic ordering
    /// tuple (ts, author, id).</summary>
    [JsonPropertyName("ts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ts { get; init; }

    /// <summary>Per-author Lamport counter used by clear markers. Comparing the same
    /// author's counter avoids comparing two different device clocks.</summary>
    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seq { get; init; }

    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    public static Event SendEvent(string payload, string? replyingTo = null,
        string? author = null, long? ts = null, long? seq = null) =>
        new() { TypeName = EventTypeNames.Send, Payload = payload, ReplyingTo = replyingTo,
                Author = author, Ts = ts, Seq = seq };

    public static Event DeleteEvent(string eventId, string? author = null, long? ts = null,
        long? seq = null) =>
        new() { TypeName = EventTypeNames.Delete, Payload = eventId, Author = author,
                Ts = ts, Seq = seq };

    /// <summary>Rebuilds an op for anti-entropy replay. The id and metadata are preserved
    /// so the receiving peer deduplicates by id regardless of delivery order.</summary>
    public static Event Replay(string id, string typeName, string payload, string? replyingTo,
        string? author, long? ts, long? seq = null) =>
        new() { Id = id, TypeName = typeName, Payload = payload, ReplyingTo = replyingTo,
                Author = author, Ts = ts, Seq = seq };

    public static Event TypingEvent() =>
        new() { TypeName = EventTypeNames.Typing, Payload = string.Empty };

    public static Event EndCallEvent() =>
        new() { TypeName = EventTypeNames.EndCall, Payload = string.Empty };
}
