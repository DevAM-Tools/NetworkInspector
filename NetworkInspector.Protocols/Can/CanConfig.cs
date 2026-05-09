// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkInspector.Protocols.Can;

/// <summary>
/// Configuration model for CAN protocol. Provides message name resolution
/// from a JSON config file.
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
    /// <summary>CAN identifier (11-bit standard or 29-bit extended).</summary>
    [JsonPropertyName("id")]
    public uint Id
    {
        get; set;
    }

    /// <summary>Human-readable message name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this is an extended (29-bit) CAN ID. Default is false (11-bit standard).</summary>
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

    /// <summary>Optional reference to a Signal PDU definition for payload decoding.</summary>
    [JsonPropertyName("signal_pdu_id")]
    public uint? SignalPduId
    {
        get; set;
    }
}

/// <summary>
/// Source-generated JSON serialization context for AOT-safe deserialization
/// of CAN configuration files.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CanConfig))]
internal sealed partial class CanConfigContext : JsonSerializerContext;
