// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.PduTransport;

/// <summary>
/// Configuration model for PDU Transport protocol. Provides name resolution
/// for PDU identifiers. Loaded from a JSON file specified by the
/// <c>pdu_transport.config_file</c> setting.
/// </summary>
internal sealed class PduTransportConfig
{
    /// <summary>List of PDU definitions with ID-to-name mappings.</summary>
    [JsonPropertyName("pdus")]
    public PduTransportPduEntry[] Pdus { get; set; } = [];
}

/// <summary>
/// A single PDU entry defining an ID and its display name.
/// </summary>
internal sealed class PduTransportPduEntry
{
    /// <summary>Numeric PDU identifier.</summary>
    [JsonPropertyName("id")]
    public uint Id
    {
        get; set;
    }

    /// <summary>Human-readable name for this PDU.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional comment/description.</summary>
    [JsonPropertyName("comment")]
    public string? Comment
    {
        get; set;
    }
}

/// <summary>
/// Source-generated JSON serialization context for AOT-safe deserialization
/// of PDU Transport configuration files.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PduTransportConfig))]
internal sealed partial class PduTransportConfigContext : JsonSerializerContext;
