// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkInspector.Protocols.SignalPdu;

/// <summary>
/// Configuration model for Signal PDU protocol. Defines PDU layouts with
/// bit-level signal definitions, multiplexer support, and dynamic table registration.
/// Loaded from a JSON file specified by the <c>signal_pdu.config_file</c> setting.
/// </summary>
internal sealed class SignalPduConfig
{
    /// <summary>List of PDU definitions with signal layouts.</summary>
    [JsonPropertyName("pdus")]
    public SignalPduDefinition[] Pdus { get; set; } = [];
}

/// <summary>
/// A single PDU definition containing its signals and registration targets.
/// </summary>
internal sealed class SignalPduDefinition
{
    /// <summary>Unique PDU identifier used for internal routing.</summary>
    [JsonPropertyName("pdu_id")]
    public uint PduId
    {
        get; set;
    }

    /// <summary>Human-readable PDU name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Dispatch tables to register this PDU into at startup.</summary>
    [JsonPropertyName("register_at")]
    public SignalPduRegistration[] RegisterAt { get; set; } = [];

    /// <summary>Expected byte length of the PDU payload.</summary>
    [JsonPropertyName("byte_length")]
    public int ByteLength
    {
        get; set;
    }

    /// <summary>Signal definitions for this PDU.</summary>
    [JsonPropertyName("signals")]
    public SignalDefinition[] Signals { get; set; } = [];

    /// <summary>Optional multiplexer signal definition.</summary>
    [JsonPropertyName("mux_signal")]
    public MuxSignalDefinition? MuxSignal
    {
        get; set;
    }

    /// <summary>Multiplexer-dependent signal groups.</summary>
    [JsonPropertyName("mux_groups")]
    public MuxGroup[] MuxGroups { get; set; } = [];
}

/// <summary>
/// Specifies a dispatch table and key for dynamic registration.
/// </summary>
internal sealed class SignalPduRegistration
{
    /// <summary>Dispatch table name (e.g., "can.id", "pdu_transport.id").</summary>
    [JsonPropertyName("table")]
    public string Table { get; set; } = string.Empty;

    /// <summary>Key value to register at in the dispatch table.</summary>
    [JsonPropertyName("key")]
    public ulong Key
    {
        get; set;
    }
}

/// <summary>
/// Defines a single signal within a PDU, including bit position, encoding, and scaling.
/// </summary>
internal sealed class SignalDefinition
{
    /// <summary>Human-readable signal name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Bit position of the signal start (MSB for big-endian, LSB for little-endian).</summary>
    [JsonPropertyName("start_bit")]
    public int StartBit
    {
        get; set;
    }

    /// <summary>Number of bits to extract (1-64).</summary>
    [JsonPropertyName("bit_length")]
    public int BitLength
    {
        get; set;
    }

    /// <summary>Byte order: "big_endian" or "little_endian".</summary>
    [JsonPropertyName("byte_order")]
    public string ByteOrder { get; set; } = "big_endian";

    /// <summary>Data type: "unsigned", "signed", "float32", "float64".</summary>
    [JsonPropertyName("data_type")]
    public string DataType { get; set; } = "unsigned";

    /// <summary>Scaling factor: physical = raw * factor + offset.</summary>
    [JsonPropertyName("factor")]
    public double Factor { get; set; } = 1.0;

    /// <summary>Offset: physical = raw * factor + offset.</summary>
    [JsonPropertyName("offset")]
    public double Offset
    {
        get; set;
    }

    /// <summary>Physical unit string (e.g., "rpm", "°C").</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    /// <summary>Minimum valid physical value.</summary>
    [JsonPropertyName("min")]
    public double? Min
    {
        get; set;
    }

    /// <summary>Maximum valid physical value.</summary>
    [JsonPropertyName("max")]
    public double? Max
    {
        get; set;
    }

    /// <summary>Map of raw integer values to display names (e.g., "0": "Off", "1": "On").</summary>
    [JsonPropertyName("value_names")]
    public Dictionary<string, string>? ValueNames
    {
        get; set;
    }

    /// <summary>
    /// Numeric-key version of <see cref="ValueNames"/> for zero-allocation lookup.
    /// Populated by <see cref="BuildNumericValueNames"/> after deserialization.
    /// </summary>
    [JsonIgnore]
    internal Dictionary<ulong, string>? NumericValueNames
    {
        get; private set;
    }

    /// <summary>
    /// Converts string-keyed ValueNames to ulong-keyed dictionary for zero-allocation lookup.
    /// Must be called after JSON deserialization.
    /// </summary>
    internal void BuildNumericValueNames()
    {
        if (ValueNames is null || ValueNames.Count == 0)
        {
            return;
        }
        NumericValueNames = new Dictionary<ulong, string>(ValueNames.Count);
        foreach (KeyValuePair<string, string> kvp in ValueNames)
        {
            if (ulong.TryParse(kvp.Key, out ulong key))
            {
                NumericValueNames[key] = kvp.Value;
            }
        }
    }

    /// <summary>Whether this signal uses big-endian byte order.</summary>
    [JsonIgnore]
    internal bool IsBigEndian => ByteOrder.Equals("big_endian", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Defines the multiplexer selector signal.
/// </summary>
internal sealed class MuxSignalDefinition
{
    /// <summary>Multiplexer signal name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Bit position of the mux selector.</summary>
    [JsonPropertyName("start_bit")]
    public int StartBit
    {
        get; set;
    }

    /// <summary>Number of bits for the mux selector.</summary>
    [JsonPropertyName("bit_length")]
    public int BitLength
    {
        get; set;
    }

    /// <summary>Byte order of the mux selector.</summary>
    [JsonPropertyName("byte_order")]
    public string ByteOrder { get; set; } = "big_endian";

    /// <summary>Whether this mux signal uses big-endian byte order.</summary>
    [JsonIgnore]
    internal bool IsBigEndian => ByteOrder.Equals("big_endian", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A group of signals that are only present when the multiplexer has a specific value.
/// </summary>
internal sealed class MuxGroup
{
    /// <summary>The mux selector value that activates this group.</summary>
    [JsonPropertyName("mux_value")]
    public ulong MuxValue
    {
        get; set;
    }

    /// <summary>Signals present when the mux selector matches this value.</summary>
    [JsonPropertyName("signals")]
    public SignalDefinition[] Signals { get; set; } = [];
}

/// <summary>
/// Source-generated JSON serialization context for AOT-safe deserialization
/// of Signal PDU configuration files.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SignalPduConfig))]
internal sealed partial class SignalPduConfigContext : JsonSerializerContext;
