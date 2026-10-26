// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.PduTransport;

/// <summary>
/// Configuration model for PDU Transport protocol. Provides name resolution
/// for PDU identifiers. Loaded from a JSON file specified by the
/// <c>pdu_transport.config_file</c> setting.
/// <para>
/// This is external input: the file (or stream) is operator-supplied. The settings manager bounds file size and JSON
/// depth for the file setting; a caller-supplied stream is not bounded (see <see cref="PduTransportRegistration"/>).
/// The model is a plain mutable DTO that is only read while the protocol is registered; it is not thread-safe and
/// is not retained by the protocol (the names are copied into a frozen lookup).
/// </para>
/// </summary>
public sealed class PduTransportConfig
{
    /// <summary>List of PDU definitions with ID-to-name mappings.</summary>
    [JsonPropertyName("pdus")]
    public PduTransportPduEntry[] Pdus { get; set; } = [];
}

/// <summary>
/// A single PDU entry defining an ID and its display name. Mutable DTO, not thread-safe (see
/// <see cref="PduTransportConfig"/>).
/// </summary>
public sealed class PduTransportPduEntry
{
    /// <summary>
    /// Numeric PDU identifier. Stored as <see cref="uint"/>, the widest ID the protocol can read (a 4-byte ID field);
    /// a JSON number outside that range makes the whole file fail to load.
    /// </summary>
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
/// of PDU Transport configuration files. Source generation avoids reflection at run time, so loading works under
/// trimming and Native AOT. The options make hand-edited files forgiving (case-insensitive property names, comments
/// and trailing commas allowed); they do not change what is accepted structurally.
/// <para>Thread safety: the generated context is immutable and shared.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PduTransportConfig))]
internal sealed partial class PduTransportConfigContext : JsonSerializerContext;
