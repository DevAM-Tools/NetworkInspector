// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// JSON configuration root for signal-message protocols.
/// Loaded from the file referenced by <c>signal_message.config_file</c>.
/// </summary>
/// <remarks>
/// The configuration DTOs in this file are deserialization targets for untrusted JSON: they carry no
/// validation themselves (mutable properties with safe defaults). Every value is validated by
/// <c>SignalMessageCompiler</c> before it reaches a protocol, so a bad entry becomes a warning instead of a failure.
/// </remarks>
public sealed class SignalMessagesConfig
{
    /// <summary>Signal message definitions; each becomes one protocol instance.</summary>
    [JsonPropertyName("messages")]
    public SignalMessageConfig[] Messages { get; set; } = [];
}

/// <summary>One signal message (one registered protocol instance).</summary>
public sealed class SignalMessageConfig
{
    /// <summary>Registered protocol name and container field name (unique).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable protocol UI name.</summary>
    [JsonPropertyName("ui_name")]
    public string UiName { get; set; } = string.Empty;

    /// <summary>Optional protocol description; default applied at compile time when omitted.</summary>
    [JsonPropertyName("description")]
    public string? Description
    {
        get; set;
    }

    /// <summary>
    /// Declared wire length in bytes (at least 1); must be ≥ the computed RequiredByteLength (the farthest signal bit).
    /// Compile-time check only: parse consumes the computed length, not this value.
    /// </summary>
    [JsonPropertyName("byte_length")]
    public int ByteLength
    {
        get; set;
    }

    /// <summary>Dispatch-table bindings for this message protocol.</summary>
    [JsonPropertyName("dispatch_bindings")]
    public DispatchBinding[] DispatchBindings { get; set; } = [];

    /// <summary>Always-present signals.</summary>
    [JsonPropertyName("signals")]
    public SignalFieldConfig[] Signals { get; set; } = [];

    /// <summary>Optional multiplexer selector.</summary>
    [JsonPropertyName("mux_signal")]
    public MuxSignalConfig? MuxSignal
    {
        get; set;
    }

    /// <summary>Multiplexer-dependent signal groups.</summary>
    [JsonPropertyName("mux_groups")]
    public MuxGroupConfig[] MuxGroups { get; set; } = [];
}

/// <summary>
/// Dispatch table + key binding for a signal message (<c>dispatch_bindings</c> JSON item).
/// </summary>
/// <remarks>
/// Same shape as FrameBuilder <c>DispatchBinding</c> (table name + U64 key), which keeps the two descriptions of a
/// binding in lock-step. A class with setters rather than a record struct because it is a JSON deserialization target.
/// </remarks>
public sealed class DispatchBinding
{
    /// <summary>Dispatch table name (e.g. <c>can.id</c>).</summary>
    [JsonPropertyName("table")]
    public string Table { get; set; } = string.Empty;

    /// <summary>Numeric key in that table.</summary>
    [JsonPropertyName("key")]
    public ulong Key
    {
        get; set;
    }
}

/// <summary>One signal definition inside a message.</summary>
public sealed class SignalFieldConfig
{
    /// <summary>Registered field name (JSON already supplies the target form).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable signal UI name for CustomText.</summary>
    [JsonPropertyName("ui_name")]
    public string UiName { get; set; } = string.Empty;

    /// <summary>
    /// Bit position of the signal start, counted as <c>byte_index * 8 + bit_in_byte</c> with bit 0 the least significant
    /// bit of a byte (DBC convention). It is the MSB for <c>big_endian</c> (Motorola) and the LSB for
    /// <c>little_endian</c> (Intel). Valid range is 0..65535 (stored as <see cref="ushort"/>).
    /// </summary>
    [JsonPropertyName("start_bit")]
    public int StartBit
    {
        get; set;
    }

    /// <summary>Number of bits (1–64); the raw value is extracted into a <see cref="ulong"/>.</summary>
    [JsonPropertyName("bit_length")]
    public int BitLength
    {
        get; set;
    }

    /// <summary>
    /// <c>big_endian</c> or <c>little_endian</c> (case-insensitive). Defaults to <c>big_endian</c> when the JSON omits
    /// the property; an explicit empty or unknown value is rejected by the compiler.
    /// </summary>
    [JsonPropertyName("byte_order")]
    public string ByteOrder { get; set; } = "big_endian";

    /// <summary>Scaling factor: physical = raw × factor + offset (raw is always treated as unsigned).</summary>
    [JsonPropertyName("factor")]
    public double Factor { get; set; } = 1.0;

    /// <summary>Scaling offset.</summary>
    [JsonPropertyName("offset")]
    public double Offset
    {
        get; set;
    }

    /// <summary>Physical unit string.</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    /// <summary>Optional min physical value (informational only; not validated and not used for parsing).</summary>
    [JsonPropertyName("min")]
    public double? Min
    {
        get; set;
    }

    /// <summary>Optional max physical value (informational only; not validated and not used for parsing).</summary>
    [JsonPropertyName("max")]
    public double? Max
    {
        get; set;
    }

    /// <summary>
    /// Map of raw integer values (decimal string keys, JSON object keys cannot be numbers) to non-empty display
    /// names. Keys must fit the signal's bit length and the entry count is limited by
    /// <c>signal_message.max_enum_values</c>.
    /// </summary>
    [JsonPropertyName("value_names")]
    public Dictionary<string, string>? ValueNames
    {
        get; set;
    }
}

/// <summary>Multiplexer selector signal.</summary>
public sealed class MuxSignalConfig
{
    /// <summary>Registered mux field name (JSON already supplies the target form).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>UI name for the multiplexer container.</summary>
    [JsonPropertyName("ui_name")]
    public string UiName { get; set; } = string.Empty;

    /// <summary>Bit position of the mux selector; same convention and range as <see cref="SignalFieldConfig.StartBit"/>.</summary>
    [JsonPropertyName("start_bit")]
    public int StartBit
    {
        get; set;
    }

    /// <summary>Bit length of the mux selector (1–64).</summary>
    [JsonPropertyName("bit_length")]
    public int BitLength
    {
        get; set;
    }

    /// <summary>Byte order of the mux selector (<c>big_endian</c> or <c>little_endian</c>; defaults to <c>big_endian</c>).</summary>
    [JsonPropertyName("byte_order")]
    public string ByteOrder { get; set; } = "big_endian";
}

/// <summary>Signals present when the mux selector equals <see cref="MuxValue"/>.</summary>
public sealed class MuxGroupConfig
{
    /// <summary>Mux selector value that activates this group.</summary>
    [JsonPropertyName("mux_value")]
    public ulong MuxValue
    {
        get; set;
    }

    /// <summary>Signals for this mux value.</summary>
    [JsonPropertyName("signals")]
    public SignalFieldConfig[] Signals { get; set; } = [];
}

/// <summary>
/// Source-generated JSON context for Signal Message configuration (no reflection-based serialization at runtime).
/// The options are deliberately lenient because the file is hand-edited: property names match case-insensitively,
/// comments are skipped, and trailing commas are allowed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SignalMessagesConfig))]
[JsonSerializable(typeof(SignalMessageConfig))]
[JsonSerializable(typeof(SignalFieldConfig))]
[JsonSerializable(typeof(DispatchBinding))]
[JsonSerializable(typeof(MuxSignalConfig))]
[JsonSerializable(typeof(MuxGroupConfig))]
internal sealed partial class SignalMessagesConfigContext : JsonSerializerContext;
