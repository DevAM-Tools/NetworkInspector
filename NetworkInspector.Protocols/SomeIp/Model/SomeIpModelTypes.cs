// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Model;

// The immutable result of compiling a SOME/IP service description (SomeIpConfigCompiler). Everything the parser does per
// packet is an array index or one frozen-dictionary probe into these types: the compiler resolves type names to indices,
// derives every registered field name, and checks every consistency rule once, so the parse path never validates the
// configuration. The types are public because a host can inspect a compiled model; nothing here is mutable after
// registration except the field and group ids, which the registration assigns exactly once on the single-threaded build
// phase (see SomeIpUsageSite).
//
// Why the member tree lives on the usage sites and not on the types: a type is defined once per service and referenced from
// many places, but every occurrence is its own filterable field (same type at two paths means two registered fields).
// The compiler therefore expands the type graph per usage: a structure used by two methods has one SomeIpTypeInfo and two
// site subtrees. The type says how to decode; the site says where in the message the value sits and what it is called.
//
// Thread-safety of every type in this file: immutable after construction (arrays and frozen dictionaries are never written
// again), except for the one-time id assignment named above; safe to read from any thread.

/// <summary>The seven type categories of the format, plus the marker for a type that cannot be decoded.</summary>
public enum SomeIpTypeCategory : byte
{
    /// <summary>A named wire primitive.</summary>
    Base,

    /// <summary>An array of one element type.</summary>
    Array,

    /// <summary>A plain structure.</summary>
    Structure,

    /// <summary>A TLV structure.</summary>
    TlvStructure,

    /// <summary>A union.</summary>
    Union,

    /// <summary>A bitfield.</summary>
    Bitfield,

    /// <summary>A name for another type; the compiler resolves it, so a compiled site never carries it.</summary>
    Alias,

    /// <summary>A type that cannot be decoded (cycle, unknown primitive, dangling reference); decoding shows a marker.</summary>
    Unavailable,
}

/// <summary>The wire primitives.</summary>
public enum SomeIpPrimitive : byte
{
    /// <summary>Unsigned 8 bits.</summary>
    UInt8,

    /// <summary>Unsigned 16 bits.</summary>
    UInt16,

    /// <summary>Unsigned 32 bits.</summary>
    UInt32,

    /// <summary>Unsigned 64 bits.</summary>
    UInt64,

    /// <summary>Signed 8 bits.</summary>
    Int8,

    /// <summary>Signed 16 bits.</summary>
    Int16,

    /// <summary>Signed 32 bits.</summary>
    Int32,

    /// <summary>Signed 64 bits.</summary>
    Int64,

    /// <summary>IEEE 754 single.</summary>
    Float,

    /// <summary>IEEE 754 double.</summary>
    Double,

    /// <summary>One byte, zero is false.</summary>
    Bool,

    /// <summary>A string; the encoding and length rules come from the type and the member.</summary>
    String,

    /// <summary>A 16-byte UUID.</summary>
    Uuid,
}

/// <summary>String encodings.</summary>
public enum SomeIpStringEncoding : byte
{
    /// <summary>UTF-8 (the default).</summary>
    Utf8,

    /// <summary>ASCII.</summary>
    Ascii,

    /// <summary>UTF-16 big endian.</summary>
    Utf16BigEndian,

    /// <summary>UTF-16 little endian.</summary>
    Utf16LittleEndian,
}

/// <summary>The kind of a service member; a field contributes up to three members.</summary>
public enum SomeIpMemberKind : byte
{
    /// <summary>A method call.</summary>
    Method,

    /// <summary>An event.</summary>
    Event,

    /// <summary>The getter of a field.</summary>
    FieldGetter,

    /// <summary>The setter of a field.</summary>
    FieldSetter,

    /// <summary>The notifier of a field.</summary>
    FieldNotifier,

    /// <summary>A named eventgroup (not a wire member; resolved by service discovery).</summary>
    EventGroup,
}

/// <summary>The end-to-end protection profile of a member.</summary>
public enum SomeIpE2eProfile : byte
{
    /// <summary>Unprotected.</summary>
    None,

    /// <summary>Profile 04 (12-byte header, CRC verifiable).</summary>
    Profile04,

    /// <summary>Profile 44 (12-byte header, CRC verifiable).</summary>
    Profile44,

    /// <summary>Profile 08 (16-byte header, CRC shown but not verified).</summary>
    Profile08,

    /// <summary>A profile name the build does not know; the payload is shown raw with a diagnostic.</summary>
    Unknown,
}

/// <summary>How a type is decoded. One instance per defined type (or per primitive used in a service); never per usage.</summary>
public sealed class SomeIpTypeInfo
{
    /// <summary>The category.</summary>
    public SomeIpTypeCategory Category { get; init; }

    /// <summary>The configured type name (the primitive name for a synthetic base type).</summary>
    public string Name { get; init; } = "";

    /// <summary>The wire primitive (base types).</summary>
    public SomeIpPrimitive Primitive { get; init; }

    /// <summary>True for little-endian multi-byte values (base types and bitfields).</summary>
    public bool LittleEndian { get; init; }

    /// <summary>The string encoding (string base types).</summary>
    public SomeIpStringEncoding Encoding { get; init; }

    /// <summary>Strings: a value above 0 is the exact byte count without a length field.</summary>
    public int FixedLength { get; init; }

    /// <summary>The type-level length-field size: -1 inherits the member's transformation; otherwise 0 (structures only), 1, 2, or 4.</summary>
    public sbyte LengthSizeOverride { get; init; } = -1;

    /// <summary>Alignment after the value in bytes, 0 for none.</summary>
    public byte PadTo { get; init; }

    /// <summary>Raw value to display name (base types), or null.</summary>
    public FrozenDictionary<long, string>? Lookup { get; init; }

    /// <summary>Arrays: a value above 0 is the fixed element count without a length field.</summary>
    public int FixedCount { get; init; }

    /// <summary>Arrays: the lower bound of the decoded element count, -1 for none.</summary>
    public int MinElements { get; init; } = -1;

    /// <summary>Arrays: the upper bound of the decoded element count, -1 for none.</summary>
    public int MaxElements { get; init; } = -1;

    /// <summary>Unions: the size of the length field (1, 2, 4).</summary>
    public byte UnionLengthSize { get; init; } = 4;

    /// <summary>Unions: the size of the selector field (1, 2, 4).</summary>
    public byte UnionTypeSize { get; init; } = 4;

    /// <summary>Bitfields: the width in bits (8, 16, 32, 64).</summary>
    public byte Bits { get; init; }
}

/// <summary>
/// One occurrence of a type in a message: a parameter, a structure member, a TLV member, a union alternative, an array
/// element, or a bit. The registered field name, the display name and the field identity belong to the occurrence.
/// </summary>
public sealed class SomeIpUsageSite
{
    /// <summary>
    /// The decoded type (an alias is resolved to its target); -1 when the reference is dangling, in which case
    /// <see cref="ReferenceName"/> names it.
    /// </summary>
    public int TypeIndex { get; init; } = -1;

    /// <summary>The referenced type name as written in the file; shown by the marker for a dangling reference.</summary>
    public string ReferenceName { get; init; } = "";

    /// <summary>The registered field name (the filter string).</summary>
    public string RegisteredName { get; init; } = "";

    /// <summary>The display name.</summary>
    public string UiName { get; init; } = "";

    /// <summary>Alignment after the value in bytes, 0 for none.</summary>
    public byte PadTo { get; init; }

    /// <summary>The TLV id, union selector, bit number, or parameter position of this site (0 for an array element).</summary>
    public uint Key { get; init; }

    /// <summary>The member this site belongs to (index into the service's members).</summary>
    public int MemberIndex { get; init; }

    /// <summary>True for a site of the response direction.</summary>
    public bool IsOut { get; init; }

    /// <summary>The index of the first child site (structure members, TLV members, alternatives, bits, the array element).</summary>
    public int ChildrenStart { get; init; }

    /// <summary>The number of child sites.</summary>
    public int ChildrenCount { get; init; }

    /// <summary>TLV structures, unions and bitfields: key (TLV id, selector, bit number) to child site index.</summary>
    public FrozenDictionary<uint, int>? ChildByKey { get; init; }

    /// <summary>
    /// The registered field; <see cref="FieldId.Invalid"/> until the registration assigns it (exactly once, on the
    /// single-threaded build phase, before any packet is parsed).
    /// </summary>
    public FieldId FieldId { get; set; } = FieldId.Invalid;

    /// <summary>The index group of the member direction this site belongs to; assigned like <see cref="FieldId"/>.</summary>
    public IndexGroupId GroupId { get; set; } = IndexGroupId.Invalid;
}

/// <summary>The top-level sites of one parameter list of a member direction.</summary>
/// <param name="IsTlv">True when the list is TLV-encoded.</param>
/// <param name="SitesStart">Index of the first site.</param>
/// <param name="SitesCount">Number of sites.</param>
/// <param name="ById">TLV lists: TLV id to site index; null for sequential lists.</param>
public readonly record struct SomeIpParameterList(bool IsTlv, int SitesStart, int SitesCount, FrozenDictionary<uint, int>? ById);

/// <summary>The resolved serialization defaults of a member, in bytes.</summary>
/// <param name="StringLength">Size of string length fields: 1, 2, or 4.</param>
/// <param name="ArrayLength">Size of array length fields: 1, 2, or 4.</param>
/// <param name="StructLength">Size of structure length fields: 0 (none), 1, 2, or 4.</param>
public readonly record struct SomeIpTransformation(byte StringLength, byte ArrayLength, byte StructLength);

/// <summary>A service member resolved for matching wire traffic.</summary>
public sealed class SomeIpMemberInfo
{
    /// <summary>The 16-bit member id on the wire.</summary>
    public ushort Id { get; init; }

    /// <summary>The kind.</summary>
    public SomeIpMemberKind Kind { get; init; }

    /// <summary>The configured display name (the field name for the members of a field).</summary>
    public string Name { get; init; } = "";

    /// <summary>The path token of the member in registered names, including the direction-independent accessor suffix.</summary>
    public string Token { get; init; } = "";

    /// <summary>The request payload: method input; setter: the type; getter: empty.</summary>
    public SomeIpParameterList In { get; init; }

    /// <summary>The response payload: method output; getter and setter response and notifier: the type.</summary>
    public SomeIpParameterList Out { get; init; }

    /// <summary>The end-to-end protection profile.</summary>
    public SomeIpE2eProfile E2e { get; init; }

    /// <summary>The configured profile text; shown for <see cref="SomeIpE2eProfile.Unknown"/>.</summary>
    public string E2eProfileText { get; init; } = "";

    /// <summary>The resolved transformation properties.</summary>
    public SomeIpTransformation Transform { get; init; }

    /// <summary>The index group of the request direction; assigned by the registration.</summary>
    public IndexGroupId InGroup { get; set; } = IndexGroupId.Invalid;

    /// <summary>The index group of the response direction; assigned by the registration.</summary>
    public IndexGroupId OutGroup { get; set; } = IndexGroupId.Invalid;

    /// <summary>
    /// The first of the two payload populator slots of this member (request direction at this index, response direction at the
    /// next); -1 until the registration assigns it. Assigned exactly once on the single-threaded build phase, like the group ids.
    /// </summary>
    internal int PayloadSlot { get; set; } = -1;
}

/// <summary>One service at one major version.</summary>
public sealed class SomeIpServiceInfo
{
    /// <summary>The service id.</summary>
    public ushort ServiceId { get; init; }

    /// <summary>The interface major version.</summary>
    public byte MajorVersion { get; init; }

    /// <summary>The display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The path token in registered names, <c>{token}.v{major}</c>.</summary>
    public string Token { get; init; } = "";

    /// <summary>The members in declaration order.</summary>
    public ReadOnlyMemory<SomeIpMemberInfo> Members { get; init; }

    /// <summary>Wire member id to index in <see cref="Members"/>.</summary>
    public FrozenDictionary<ushort, int> MemberById { get; init; } = FrozenDictionary<ushort, int>.Empty;

    /// <summary>Eventgroup id to name.</summary>
    public FrozenDictionary<ushort, string> EventGroupNames { get; init; } = FrozenDictionary<ushort, string>.Empty;
}
