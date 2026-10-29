// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Payload;

/// <summary>
/// The tag in front of every value of a TLV-encoded list or structure: 16 bits, big endian. Bit 15 is reserved, bits 14 to 12 carry
/// the wire type that says how long the value is, and bits 11 to 0 carry the data id that names the member. The member order on
/// the wire is free, which is the point of the encoding: a receiver finds a member by its id, not by its position.
/// </summary>
/// <param name="WireType">The wire type, 0 to 7.</param>
/// <param name="DataId">The data id, 0 to 4095.</param>
/// <param name="Reserved">The reserved bit; a sender sets it to zero, and it is shown so that a sender that does not can be found.</param>
/// <param name="Raw">The 16 bits as they are on the wire.</param>
internal readonly record struct SomeIpTlvTag(byte WireType, ushort DataId, bool Reserved, ushort Raw)
{
    /// <summary>Splits the 16 bits of a tag into its three parts.</summary>
    /// <param name="raw">The tag as it is on the wire (already converted from big endian).</param>
    internal static SomeIpTlvTag Parse(ushort raw) =>
        new((byte)((raw >> 12) & 0x7), (ushort)(raw & 0x0FFF), (raw & 0x8000) != 0, raw);
}

/// <summary>The outcome of finding the extent of one TLV value.</summary>
internal enum SomeIpTlvExtent : byte
{
    /// <summary>The extent is known.</summary>
    Known,

    /// <summary>The wire type needs a length field and fewer bytes than its size are left.</summary>
    LengthFieldCut,

    /// <summary>
    /// Wire type 4 (the length field is the one of the member's type) on a member that has no such field, or on an unknown
    /// member. The extent cannot be known, and neither can the position of the next tag.
    /// </summary>
    NoLengthField,
}

/// <summary>
/// The extent of a TLV value, derived from the wire type of its tag.
/// <para>
/// <b>Wire types:</b> 0, 1, 2 and 3 are fixed-size values of 1, 2, 4 and 8 bytes without a length field. 5, 6 and 7 are values with
/// an explicit length field of 1, 2 and 4 bytes. 4 is a value whose length field is the one that its own type defines: the size of
/// a string, array or structure length field comes from the type or the member, a union has its own. A type without a length
/// field (a number, a bitfield) cannot be sent under wire type 4.
/// </para>
/// <para>
/// <b>Why the tag length wins over an inline length:</b> a value under wire types 4 to 7 is preceded by the length of its extent,
/// and a string, array or structure inside it would repeat that number in its own length field. The format sends one: the tag's.
/// The caller therefore decodes the value as a value of a known extent and reads no inline length.
/// </para>
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal static class SomeIpTlv
{
    /// <summary>
    /// Finds the length field size and the value length of the value behind a tag.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="afterTag">The bytes behind the tag, up to the end of the enclosing range.</param>
    /// <param name="memberType">The type of the member the data id names, or <see langword="null"/> for an unknown data id.</param>
    /// <param name="transform">The serialization defaults of the member.</param>
    /// <param name="lengthFieldSize">The size of the length field in bytes; 0 when the wire type has none.</param>
    /// <param name="valueLength">The length of the value in bytes, without the length field. Wide enough that no 32-bit value wraps.</param>
    /// <returns>Whether the extent is known and, if not, why.</returns>
    internal static SomeIpTlvExtent TryGetExtent(
        in SomeIpTlvTag tag,
        ReadOnlySpan<byte> afterTag,
        SomeIpTypeInfo? memberType,
        in SomeIpTransformation transform,
        out int lengthFieldSize,
        out long valueLength)
    {
        valueLength = 0;
        lengthFieldSize = 0;
        switch (tag.WireType)
        {
            case 0:
                valueLength = 1;
                return SomeIpTlvExtent.Known;
            case 1:
                valueLength = 2;
                return SomeIpTlvExtent.Known;
            case 2:
                valueLength = 4;
                return SomeIpTlvExtent.Known;
            case 3:
                valueLength = 8;
                return SomeIpTlvExtent.Known;
            case 5:
                lengthFieldSize = 1;
                break;
            case 6:
                lengthFieldSize = 2;
                break;
            case 7:
                lengthFieldSize = 4;
                break;
            default:
                lengthFieldSize = LengthFieldSizeOfType(memberType, in transform);
                if (lengthFieldSize <= 0)
                {
                    lengthFieldSize = 0;
                    return SomeIpTlvExtent.NoLengthField;
                }

                break;
        }

        if (afterTag.Length < lengthFieldSize)
        {
            return SomeIpTlvExtent.LengthFieldCut;
        }

        // The length field is big endian, like every field of the format; a string type declared little endian is the one
        // exception, because its length field follows the byte order of the type (see the string decoder).
        bool little = tag.WireType == 4 && memberType is { Category: SomeIpTypeCategory.Base, LittleEndian: true };
        ulong length = 0;
        for (int i = 0; i < lengthFieldSize; i++)
        {
            int index = i;
            if (little)
            {
                index = lengthFieldSize - 1 - i;
            }

            length = (length << 8) | afterTag[index];
        }

        valueLength = (long)length;
        return SomeIpTlvExtent.Known;
    }

    /// <summary>
    /// The size of the length field that the type of a member defines; 0 when it has none.
    /// </summary>
    /// <param name="type">The member type, or <see langword="null"/> when the member is unknown.</param>
    /// <param name="transform">The serialization defaults of the member.</param>
    internal static int LengthFieldSizeOfType(SomeIpTypeInfo? type, in SomeIpTransformation transform)
    {
        if (type is null)
        {
            return 0;
        }

        switch (type.Category)
        {
            case SomeIpTypeCategory.Base when type.Primitive == SomeIpPrimitive.String && type.FixedLength <= 0:
                return type.LengthSizeOverride > 0
                    ? type.LengthSizeOverride
                    : transform.StringLength;
            case SomeIpTypeCategory.Array when type.FixedCount <= 0:
                return type.LengthSizeOverride > 0
                    ? type.LengthSizeOverride
                    : transform.ArrayLength;
            case SomeIpTypeCategory.Structure:
            case SomeIpTypeCategory.TlvStructure:
                return type.LengthSizeOverride >= 0
                    ? type.LengthSizeOverride
                    : transform.StructLength;
            case SomeIpTypeCategory.Union:
                return type.UnionLengthSize;
            default:
                return 0;
        }
    }
}
