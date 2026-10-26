// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Can;

/// <summary>
/// Configuration model for CAN protocol. Provides message name resolution
/// from a JSON config file. Populated once by deserialization while the stack is built and treated as read-only
/// afterwards (<see cref="CanProtocol"/> copies the entries into a frozen lookup and does not keep this object).
/// </summary>
internal sealed class CanConfig
{
    /// <summary>List of CAN message definitions with name mappings.</summary>
    [JsonPropertyName("messages")]
    public CanMessageEntry[] Messages { get; set; } = [];
}

/// <summary>
/// A single CAN message entry providing name resolution for a CAN identifier.
/// </summary>
internal sealed class CanMessageEntry
{
    /// <summary>
    /// CAN identifier (11-bit standard or 29-bit extended), as the masked identifier without the EFF/RTR/ERR flag bits.
    /// <see cref="CanProtocol"/> keys its lookup on this value alone and compares it with the masked identifier of the
    /// received frame, so an entry matches standard and extended frames with the same numeric identifier. When several
    /// entries use the same identifier the first one is kept and <see cref="CanProtocol.ConfigLoadWarning"/> reports the
    /// repeated identifiers.
    /// </summary>
    [JsonPropertyName("id")]
    public uint Id
    {
        get; set;
    }

    /// <summary>Human-readable message name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is an extended (29-bit) CAN ID. Default is false (11-bit standard).
    /// Currently informational: <see cref="CanProtocol"/> does not read it when resolving names, so a standard and an
    /// extended entry with the same number count as a repeated identifier.
    /// </summary>
    [JsonPropertyName("extended")]
    public bool Extended
    {
        get; set;
    }

    /// <summary>Optional comment describing the message purpose.</summary>
    [JsonPropertyName("comment")]
    public string? Comment
    {
        get; set;
    }

    /// <summary>
    /// Optional reference to a signal-message dispatch key for payload decoding.
    /// Currently not read by <see cref="CanProtocol"/>; the parser dispatches the payload by CAN identifier through
    /// the <c>can.id</c> / <c>can.extended_id</c> tables only.
    /// </summary>
    [JsonPropertyName("signal_message_id")]
    public uint? SignalMessageId
    {
        get; set;
    }
}

/// <summary>
/// Source-generated JSON serialization context for AOT-safe deserialization
/// of CAN configuration files. The options make hand-edited files tolerant: property names are matched
/// case-insensitively, <c>//</c> and <c>/* */</c> comments are skipped, and trailing commas are accepted.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CanConfig))]
internal sealed partial class CanConfigContext : JsonSerializerContext;
