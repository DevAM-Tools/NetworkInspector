// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// JSON configuration root for signal-message protocols.
/// Loaded from the file referenced by <c>signal_message.config_file</c>.
/// </summary>
internal sealed class SignalMessagesConfig
{
    /// <summary>Signal message definitions; each becomes one protocol instance.</summary>
    [JsonPropertyName("messages")]
    public SignalMessageConfig[] Messages { get; set; } = [];
}

/// <summary>One signal message (one registered protocol instance).</summary>
internal sealed class SignalMessageConfig
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

    /// <summary>Declared wire length in bytes; must be ≥ computed RequiredByteLength.</summary>
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
/// Same shape as FrameBuilder <c>DispatchBinding</c> (table name + U64 key).
/// </remarks>
internal sealed class DispatchBinding
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
internal sealed class SignalFieldConfig
{
    /// <summary>Registered field name (JSON already supplies the target form).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable signal UI name for CustomText.</summary>
    [JsonPropertyName("ui_name")]
    public string UiName { get; set; } = string.Empty;

    /// <summary>Bit position of the signal start.</summary>
    [JsonPropertyName("start_bit")]
    public int StartBit
    {
        get; set;
    }

    /// <summary>Number of bits (1–64).</summary>
    [JsonPropertyName("bit_length")]
    public int BitLength
    {
        get; set;
    }

    /// <summary><c>big_endian</c> or <c>little_endian</c>.</summary>
    [JsonPropertyName("byte_order")]
    public string ByteOrder { get; set; } = "big_endian";

    /// <summary>Scaling factor.</summary>
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

    /// <summary>Optional min physical value (informational).</summary>
    [JsonPropertyName("min")]
    public double? Min
    {
        get; set;
    }

    /// <summary>Optional max physical value (informational).</summary>
    [JsonPropertyName("max")]
    public double? Max
    {
        get; set;
    }

    /// <summary>Map of raw integer values (decimal string keys) to display names.</summary>
    [JsonPropertyName("value_names")]
    public Dictionary<string, string>? ValueNames
    {
        get; set;
    }
}

/// <summary>Multiplexer selector signal.</summary>
internal sealed class MuxSignalConfig
{
    /// <summary>Registered mux field name (JSON already supplies the target form).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>UI name for the multiplexer container.</summary>
    [JsonPropertyName("ui_name")]
    public string UiName { get; set; } = string.Empty;

    /// <summary>Bit position of the mux selector.</summary>
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

    /// <summary>Byte order of the mux selector.</summary>
    [JsonPropertyName("byte_order")]
    public string ByteOrder { get; set; } = "big_endian";
}

/// <summary>Signals present when the mux selector equals <see cref="MuxValue"/>.</summary>
internal sealed class MuxGroupConfig
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

/// <summary>Source-generated JSON context for Signal Message configuration.</summary>
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
