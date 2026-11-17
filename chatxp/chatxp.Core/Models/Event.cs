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
    public EventTypes Type => EventTypeNames.FromWire(TypeName) ?? EventTypes.Send;

    /// <summary>The encrypted payload (Base64 AES-GCM combined blob), or for control
    /// events (typing/endCall) the plain convention Swift uses.</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    /// <summary>UUID string of the Event being replied to, or null (omitted on the wire).</summary>
    [JsonPropertyName("replyingTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyingTo { get; init; }

    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    public static Event SendEvent(string payload, string? replyingTo = null) =>
        new() { TypeName = EventTypeNames.Send, Payload = payload, ReplyingTo = replyingTo };

    public static Event DeleteEvent(string eventId) =>
        new() { TypeName = EventTypeNames.Delete, Payload = eventId };

    public static Event TypingEvent() =>
        new() { TypeName = EventTypeNames.Typing, Payload = string.Empty };

    public static Event EndCallEvent() =>
        new() { TypeName = EventTypeNames.EndCall, Payload = string.Empty };
}
