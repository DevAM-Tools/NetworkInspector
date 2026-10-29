// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Config;

// The public data transfer objects of the SOME/IP service description (Schemas/someip-config.schema.json). They carry
// exactly what the file says and validate nothing: every cross-reference rule (type references, cycles, duplicates, value
// ranges) is checked once by SomeIpConfigCompiler, which reports each defect as one warning. They are public so that a host
// can register a description it built in memory (SomeIpRegistration.Register(builder, config)).
//
// Why integers are int and not narrower types: a value outside the format's range (a service id of 70000, a TLV key of
// 4096) must reach the compiler as a value so it can name the node and the rule in one warning; a narrow property type would
// make the JSON deserializer reject the whole file with a position and no node name.
//
// Thread-safety of every type in this file: mutable data objects without synchronization, owned by the thread that builds
// them. The compiled model that registration derives from them is immutable.

/// <summary>A SOME/IP service description file: transport ports, client names, and the services.</summary>
public sealed class SomeIpConfig
{
    /// <summary>Format version as "major.minor"; the loader accepts files whose major version it supports.</summary>
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "";

    /// <summary>UDP ports at which the parser registers.</summary>
    [JsonPropertyName("udp_ports")]
    public ushort[] UdpPorts { get; set; } = [];

    /// <summary>TCP ports at which the parser registers.</summary>
    [JsonPropertyName("tcp_ports")]
    public ushort[] TcpPorts { get; set; } = [];

    /// <summary>
    /// Ports of TLS connections whose plaintext is SOME/IP. Bound in table <c>tls.port</c> of a protocol that decrypts TLS; when no
    /// such protocol is in the stack, the registration reports one warning for the list and the ports have no effect. The TLS
    /// parser of this version does not decrypt.
    /// </summary>
    [JsonPropertyName("tls_ports")]
    public ushort[] TlsPorts { get; set; } = [];

    /// <summary>
    /// Ports of DTLS flows whose plaintext is SOME/IP. Bound in table <c>dtls.port</c> of a protocol that decrypts DTLS; when no
    /// such protocol is in the stack, the registration reports one warning for the list and the ports have no effect. The DTLS
    /// parser of this version does not decrypt.
    /// </summary>
    [JsonPropertyName("dtls_ports")]
    public ushort[] DtlsPorts { get; set; } = [];

    /// <summary>Optional mapping of client id (decimal string) to display name.</summary>
    [JsonPropertyName("clients")]
    public Dictionary<string, string>? Clients { get; set; }

    /// <summary>The service definitions.</summary>
    [JsonPropertyName("services")]
    public SomeIpServiceConfig[] Services { get; set; } = [];
}

/// <summary>One service at one major version.</summary>
public sealed class SomeIpServiceConfig
{
    /// <summary>The 16-bit service id (65535 is reserved for service discovery and refused by the compiler).</summary>
    [JsonPropertyName("service_id")]
    public int ServiceId { get; set; }

    /// <summary>The interface major version, selected by the message header's interface version.</summary>
    [JsonPropertyName("major_version")]
    public int MajorVersion { get; set; }

    /// <summary>The display name of the service.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The members: methods, events, fields, and eventgroups.</summary>
    [JsonPropertyName("methods")]
    public SomeIpMemberConfig[] Methods { get; set; } = [];

    /// <summary>The data type graph of this service version.</summary>
    [JsonPropertyName("data_types")]
    public SomeIpDataTypeConfig[] DataTypes { get; set; } = [];
}

/// <summary>A service member, discriminated by <c>kind</c> in the JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SomeIpMethodConfig), "method")]
[JsonDerivedType(typeof(SomeIpEventConfig), "event")]
[JsonDerivedType(typeof(SomeIpFieldConfig), "field")]
[JsonDerivedType(typeof(SomeIpEventGroupConfig), "event_group")]
public abstract class SomeIpMemberConfig
{
    /// <summary>The display name of the member.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>A method call with input and output parameter lists.</summary>
public sealed class SomeIpMethodConfig : SomeIpMemberConfig
{
    /// <summary>The 16-bit method id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>The request parameters.</summary>
    [JsonPropertyName("in")]
    public SomeIpParameterListConfig In { get; set; } = new();

    /// <summary>The response parameters.</summary>
    [JsonPropertyName("out")]
    public SomeIpParameterListConfig Out { get; set; } = new();

    /// <summary>The end-to-end protection profile ("04", "44", "08"), or null for unprotected.</summary>
    [JsonPropertyName("e2e_profile")]
    public string? E2eProfile { get; set; }

    /// <summary>Per-member serialization defaults.</summary>
    [JsonPropertyName("transformation_properties")]
    public SomeIpTransformationConfig? TransformationProperties { get; set; }
}

/// <summary>An event (notification) with one payload type.</summary>
public sealed class SomeIpEventConfig : SomeIpMemberConfig
{
    /// <summary>The 16-bit event id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>The payload type.</summary>
    [JsonPropertyName("type_reference")]
    public string TypeReference { get; set; } = "";

    /// <summary>The end-to-end protection profile, or null.</summary>
    [JsonPropertyName("e2e_profile")]
    public string? E2eProfile { get; set; }

    /// <summary>Per-member serialization defaults.</summary>
    [JsonPropertyName("transformation_properties")]
    public SomeIpTransformationConfig? TransformationProperties { get; set; }
}

/// <summary>A field with a getter, a setter, and a notifier id (at least one).</summary>
public sealed class SomeIpFieldConfig : SomeIpMemberConfig
{
    /// <summary>The getter method id, or null.</summary>
    [JsonPropertyName("get_id")]
    public int? GetId { get; set; }

    /// <summary>The setter method id, or null.</summary>
    [JsonPropertyName("set_id")]
    public int? SetId { get; set; }

    /// <summary>The notifier event id, or null.</summary>
    [JsonPropertyName("notifier_id")]
    public int? NotifierId { get; set; }

    /// <summary>The payload type.</summary>
    [JsonPropertyName("type_reference")]
    public string TypeReference { get; set; } = "";

    /// <summary>The end-to-end protection profile, or null.</summary>
    [JsonPropertyName("e2e_profile")]
    public string? E2eProfile { get; set; }

    /// <summary>Per-member serialization defaults.</summary>
    [JsonPropertyName("transformation_properties")]
    public SomeIpTransformationConfig? TransformationProperties { get; set; }
}

/// <summary>A named eventgroup, resolved by service discovery entries.</summary>
public sealed class SomeIpEventGroupConfig : SomeIpMemberConfig
{
    /// <summary>The 16-bit eventgroup id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

/// <summary>
/// A parameter list. The JSON shape selects the wire encoding: an array is sequential, an object keyed by TLV id is
/// TLV-encoded. A custom converter fills exactly one of the two properties.
/// </summary>
public sealed class SomeIpParameterListConfig
{
    /// <summary>The sequential parameters in declaration order, or null for a TLV list.</summary>
    public SomeIpUsageSiteConfig[]? Sequential { get; set; }

    /// <summary>The TLV members by id (decimal string), or null for a sequential list.</summary>
    public Dictionary<string, SomeIpUsageSiteConfig>? Tlv { get; set; }
}

/// <summary>
/// One usage of a data type. In the JSON a bare string is the type reference; an object adds names and overrides. A custom
/// converter accepts both.
/// </summary>
public sealed class SomeIpUsageSiteConfig
{
    /// <summary>The parameter name and path token; defaults to the referenced type's name.</summary>
    public string? Name { get; set; }

    /// <summary>The registered field name (filter string) override.</summary>
    public string? FilterName { get; set; }

    /// <summary>The display name override.</summary>
    public string? UiName { get; set; }

    /// <summary>The referenced type: a wire primitive or a type of the same service.</summary>
    public string TypeReference { get; set; } = "";

    /// <summary>Alignment after the value in bytes (structure members only); 0 or null for none.</summary>
    public int? PadTo { get; set; }
}

/// <summary>Per-member serialization defaults; sizes are in bytes.</summary>
public sealed class SomeIpTransformationConfig
{
    /// <summary>Size of string length fields (1, 2, 4; 0 or null means the format default 4).</summary>
    [JsonPropertyName("size_of_string_length")]
    public int? SizeOfStringLength { get; set; }

    /// <summary>Size of array length fields (1, 2, 4; 0 or null means the format default 4).</summary>
    [JsonPropertyName("size_of_array_length")]
    public int? SizeOfArrayLength { get; set; }

    /// <summary>Size of structure length fields (0 means none; null means the format default 4).</summary>
    [JsonPropertyName("size_of_struct_length")]
    public int? SizeOfStructLength { get; set; }
}

/// <summary>A data type definition, discriminated by <c>category</c> in the JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "category")]
[JsonDerivedType(typeof(SomeIpBaseTypeConfig), "base_type")]
[JsonDerivedType(typeof(SomeIpArrayTypeConfig), "array")]
[JsonDerivedType(typeof(SomeIpStructureTypeConfig), "structure")]
[JsonDerivedType(typeof(SomeIpTlvStructureTypeConfig), "tlv_structure")]
[JsonDerivedType(typeof(SomeIpUnionTypeConfig), "union")]
[JsonDerivedType(typeof(SomeIpBitfieldTypeConfig), "bitfield")]
[JsonDerivedType(typeof(SomeIpAliasTypeConfig), "alias")]
public abstract class SomeIpDataTypeConfig
{
    /// <summary>The reference name within the service.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>A named base type over one wire primitive.</summary>
public sealed class SomeIpBaseTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>The wire primitive (uint8 … uuid).</summary>
    [JsonPropertyName("type_reference")]
    public string TypeReference { get; set; } = "";

    /// <summary>"big_endian" (default) or "little_endian"; multi-byte numeric primitives only.</summary>
    [JsonPropertyName("byte_order")]
    public string? ByteOrder { get; set; }

    /// <summary>String encoding (string primitive only): utf-8, ascii, utf-16be, utf-16le.</summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>String primitive only: a value above 0 means exactly this many bytes and no length field.</summary>
    [JsonPropertyName("fixed_length")]
    public int? FixedLength { get; set; }

    /// <summary>String primitive only: size of this string's length field, overriding the member's transformation.</summary>
    [JsonPropertyName("size_of_length")]
    public int? SizeOfLength { get; set; }

    /// <summary>Raw integer value (decimal string) to display name.</summary>
    [JsonPropertyName("lookup_values")]
    public Dictionary<string, string>? LookupValues { get; set; }

    /// <summary>Alignment after the value in bytes.</summary>
    [JsonPropertyName("pad_to")]
    public int? PadTo { get; set; }
}

/// <summary>An array of one element type (variable or fixed length).</summary>
public sealed class SomeIpArrayTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>The element type.</summary>
    [JsonPropertyName("type_reference")]
    public string TypeReference { get; set; } = "";

    /// <summary>Fixed element count; 0 or null means variable length.</summary>
    [JsonPropertyName("size")]
    public int? Size { get; set; }

    /// <summary>Lower bound of the decoded element count (a diagnostic, not a refusal).</summary>
    [JsonPropertyName("min_elements")]
    public int? MinElements { get; set; }

    /// <summary>Upper bound of the decoded element count (a diagnostic, not a refusal).</summary>
    [JsonPropertyName("max_elements")]
    public int? MaxElements { get; set; }

    /// <summary>Size of this array's length field, overriding the member's transformation.</summary>
    [JsonPropertyName("size_of_length")]
    public int? SizeOfLength { get; set; }

    /// <summary>Alignment after the value in bytes.</summary>
    [JsonPropertyName("pad_to")]
    public int? PadTo { get; set; }
}

/// <summary>A plain structure: members in declaration order, optionally behind a length field.</summary>
public sealed class SomeIpStructureTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>Size of the leading length field (0 means none), overriding the member's transformation.</summary>
    [JsonPropertyName("size_of_length")]
    public int? SizeOfLength { get; set; }

    /// <summary>Alignment after the value in bytes.</summary>
    [JsonPropertyName("pad_to")]
    public int? PadTo { get; set; }

    /// <summary>The members in declaration order.</summary>
    [JsonPropertyName("members")]
    public SomeIpUsageSiteConfig[] Members { get; set; } = [];
}

/// <summary>A TLV structure: members by 12-bit TLV id, order-independent on the wire.</summary>
public sealed class SomeIpTlvStructureTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>The members by TLV id (decimal string).</summary>
    [JsonPropertyName("members")]
    public Dictionary<string, SomeIpUsageSiteConfig> Members { get; set; } = [];
}

/// <summary>A union: a length field, a selector field, then the alternative the selector names.</summary>
public sealed class SomeIpUnionTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>Size of the length field (default 4).</summary>
    [JsonPropertyName("size_of_length")]
    public int? SizeOfLength { get; set; }

    /// <summary>Size of the selector field (default 4).</summary>
    [JsonPropertyName("size_of_type")]
    public int? SizeOfType { get; set; }

    /// <summary>Alignment after the value in bytes.</summary>
    [JsonPropertyName("pad_to")]
    public int? PadTo { get; set; }

    /// <summary>The alternatives by selector (decimal string, 1 and up; 0 is the empty union).</summary>
    [JsonPropertyName("members")]
    public Dictionary<string, SomeIpUsageSiteConfig> Members { get; set; } = [];
}

/// <summary>A bitfield of 8, 16, 32, or 64 bits with named bits.</summary>
public sealed class SomeIpBitfieldTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>The width in bits (8, 16, 32, 64).</summary>
    [JsonPropertyName("bits")]
    public int Bits { get; set; }

    /// <summary>"big_endian" (default) or "little_endian".</summary>
    [JsonPropertyName("byte_order")]
    public string? ByteOrder { get; set; }

    /// <summary>The named bits by bit number (decimal string, 0 = least significant).</summary>
    [JsonPropertyName("members")]
    public Dictionary<string, SomeIpBitMemberConfig> Members { get; set; } = [];
}

/// <summary>One named bit of a bitfield.</summary>
public sealed class SomeIpBitMemberConfig
{
    /// <summary>The bit name and path token.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The registered field name (filter string) override.</summary>
    [JsonPropertyName("filter_name")]
    public string? FilterName { get; set; }

    /// <summary>The display name override.</summary>
    [JsonPropertyName("ui_name")]
    public string? UiName { get; set; }
}

/// <summary>A name for another type or primitive; decodes exactly as the referenced type.</summary>
public sealed class SomeIpAliasTypeConfig : SomeIpDataTypeConfig
{
    /// <summary>The referenced type.</summary>
    [JsonPropertyName("type_reference")]
    public string TypeReference { get; set; } = "";
}
