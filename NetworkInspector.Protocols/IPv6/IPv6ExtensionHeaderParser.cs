// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Fields;
using NetworkInspector.Protocols.Helpers;
using NetworkInspector.Values;

namespace NetworkInspector.Protocols;

/// <summary>
/// Parses IPv6 extension headers from the payload area after the 40-byte fixed header.
/// <para>Supported extension headers (RFC 8200 and related RFCs):</para>
/// <list type="bullet">
/// <item>Hop-by-Hop Options (next header 0) — RFC 8200</item>
/// <item>Routing Header (next header 43) — RFC 8200, RFC 6275 (type 2), RFC 8754 (SRH type 4)</item>
/// <item>Fragment Header (next header 44) — RFC 8200</item>
/// <item>Authentication Header (next header 51) — RFC 4302</item>
/// <item>ESP Header (next header 50) — RFC 4303 (header only, payload encrypted)</item>
/// <item>Destination Options (next header 60) — RFC 8200</item>
/// </list>
/// </summary>
internal static class IPv6ExtensionHeaderParser
{
    // IPv6 option type constants
    private const byte Pad1 = 0x00;
    private const byte PadN = 0x01;
    private const byte RouterAlert = 0x05;
    private const byte JumboPayload = 0xC2;
    private const byte TunnelEncapsulationLimit = 0x04;
    private const byte HomeAddress = 0xC9;

    // Extension header constants
    private const int FragmentHeaderSize = 8;
    private const int AhMinSize = 12;
    private const int EspHeaderSize = 8;

    // Fragment header bitmasks
    private const ushort FragOffsetMask = 0xFFF8;
    private const ushort FragReservedMask = 0x0006;
    private const ushort FragMoreMask = 0x0001;

    /// <summary>
    /// Parses all extension headers from the given data and appends their fields
    /// to the IPv6 protocol field.
    /// </summary>
    /// <param name="protoField">The IPv6 protocol container field.</param>
    /// <param name="data">Extension header data (starting after the 40-byte fixed header).</param>
    /// <param name="firstNextHeader">The next-header value from the IPv6 fixed header.</param>
    /// <param name="fields">Field IDs for extension header sub-fields.</param>
    /// <param name="context">The parse context providing dispatch resolution and stack access.</param>
    internal static void Parse(
        MutField protoField,
        ReadOnlySpan<byte> data,
        byte firstNextHeader,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        int offset = 0;
        byte currentNh = firstNextHeader;
        int depth = 0;

        while (depth < IPv6Protocol.MaxExtensionHeaders && IPv6Protocol.IsExtensionHeader(currentNh))
        {
            int remaining = data.Length - offset;
            if (remaining < 2)
            {
                break;
            }

            switch (currentNh)
            {
                case IPv6Protocol.NhHopByHop:
                    {
                        (byte nextHdr, int consumed) = ParseHopByHopOptions(protoField, data, offset, remaining, in fields, in context);
                        if (consumed == 0)
                        {
                            return;
                        }
                        currentNh = nextHdr;
                        offset += consumed;
                        break;
                    }
                case IPv6Protocol.NhDestination:
                    {
                        (byte nextHdr, int consumed) = ParseDestinationOptions(protoField, data, offset, remaining, in fields, in context);
                        if (consumed == 0)
                        {
                            return;
                        }
                        currentNh = nextHdr;
                        offset += consumed;
                        break;
                    }
                case IPv6Protocol.NhRouting:
                    {
                        (byte nextHdr, int consumed) = ParseRoutingHeader(protoField, data, offset, remaining, in fields, in context);
                        if (consumed == 0)
                        {
                            return;
                        }
                        currentNh = nextHdr;
                        offset += consumed;
                        break;
                    }
                case IPv6Protocol.NhFragment:
                    {
                        (byte nextHdr, int consumed) = ParseFragmentHeader(protoField, data, offset, remaining, in fields, in context);
                        if (consumed == 0)
                        {
                            return;
                        }
                        currentNh = nextHdr;
                        offset += consumed;
                        break;
                    }
                case IPv6Protocol.NhAh:
                    {
                        (byte nextHdr, int consumed) = ParseAhHeader(protoField, data, offset, remaining, in fields, in context);
                        if (consumed == 0)
                        {
                            return;
                        }
                        currentNh = nextHdr;
                        offset += consumed;
                        break;
                    }
                case IPv6Protocol.NhEsp:
                    {
                        // ESP terminates the chain — payload is encrypted
                        int consumed = ParseEspHeader(protoField, data, offset, remaining, in fields, in context);
                        return; // ESP terminates extension header chain
                    }
                default:
                    return; // Unknown ext header — stop
            }

            depth++;
        }
    }

    #region Hop-by-Hop Options

    /// <summary>Parses a Hop-by-Hop Options Header. Returns (nextHeader, consumed) or (0, 0) on failure.</summary>
    private static (byte NextHeader, int Consumed) ParseHopByHopOptions(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        if (remaining < 8)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte hdrExtLen = data[offset + 1];
        int totalLen = (hdrExtLen + 1) * 8; // bytes
        if (remaining < totalLen)
        {
            return (0, 0);
        }

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);

        MutField hopoptsField = protoField.AppendWithCustomText(
            fields.HopoptsFieldId, FieldValue.None,
            (string)ZA.String("Hop-by-Hop Options Header (", totalLen, " bytes)"), in context);

        hopoptsField.AppendWithCustomText(fields.HopoptsNxtFieldId, FieldValue.NewU64(nextHeader), nxtText, in context);
        hopoptsField.Append(fields.HopoptsLenFieldId, FieldValue.NewU64(hdrExtLen), in context);
        hopoptsField.Append(fields.HopoptsLenOctFieldId, FieldValue.NewU64((ulong)totalLen), in context);

        // Parse TLV options (bytes 2..totalLen relative to offset)
        ParseTlvOptions(hopoptsField, data, offset + 2, offset + totalLen, in fields, in context);

        return (nextHeader, totalLen);
    }

    #endregion

    #region Destination Options

    /// <summary>Parses a Destination Options Header. Returns (nextHeader, consumed) or (0, 0) on failure.</summary>
    private static (byte NextHeader, int Consumed) ParseDestinationOptions(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        if (remaining < 8)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte hdrExtLen = data[offset + 1];
        int totalLen = (hdrExtLen + 1) * 8;
        if (remaining < totalLen)
        {
            return (0, 0);
        }

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);

        MutField dstoptsField = protoField.AppendWithCustomText(
            fields.DstoptsFieldId, FieldValue.None,
            (string)ZA.String("Destination Options Header (", totalLen, " bytes)"), in context);

        dstoptsField.AppendWithCustomText(fields.DstoptsNxtFieldId, FieldValue.NewU64(nextHeader), nxtText, in context);
        dstoptsField.Append(fields.DstoptsLenFieldId, FieldValue.NewU64(hdrExtLen), in context);
        dstoptsField.Append(fields.DstoptsLenOctFieldId, FieldValue.NewU64((ulong)totalLen), in context);

        // Parse TLV options (bytes 2..totalLen relative to offset)
        ParseTlvOptions(dstoptsField, data, offset + 2, offset + totalLen, in fields, in context);

        return (nextHeader, totalLen);
    }

    #endregion

    #region Routing Header

    /// <summary>Parses a Routing Header. Returns (nextHeader, consumed) or (0, 0) on failure.</summary>
    private static (byte NextHeader, int Consumed) ParseRoutingHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        if (remaining < 8)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte hdrExtLen = data[offset + 1];
        int totalLen = (hdrExtLen + 1) * 8;
        if (remaining < totalLen)
        {
            return (0, 0);
        }

        byte routingType = data[offset + 2];
        byte segmentsLeft = data[offset + 3];

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);
        string rtDisplayText = DisplayTables.GetIpv6RoutingTypeDisplayText(routingType);
        string rtName = DisplayTables.GetIpv6RoutingTypeName(routingType);
        string rtDisplay = rtName.Length > 0 ? rtName : "Unknown";

        MutField routingField = protoField.AppendWithCustomText(
            fields.RoutingFieldId, FieldValue.None,
            (string)ZA.String("Routing Header (Type ", routingType, ": ", rtDisplay,
                ", Segments Left: ", segmentsLeft, ")"), in context);

        routingField.AppendWithCustomText(fields.RoutingNxtFieldId, FieldValue.NewU64(nextHeader), nxtText, in context);
        routingField.Append(fields.RoutingLenFieldId, FieldValue.NewU64(hdrExtLen), in context);
        routingField.Append(fields.RoutingLenOctFieldId, FieldValue.NewU64((ulong)totalLen), in context);
        routingField.AppendWithCustomText(fields.RoutingTypeFieldId, FieldValue.NewU64(routingType), rtDisplayText, in context);
        routingField.Append(fields.RoutingSegleftFieldId, FieldValue.NewU64(segmentsLeft), in context);

        // Parse type-specific data (bytes 4..totalLen relative to offset)
        switch (routingType)
        {
            case 2:
                ParseRoutingType2(routingField, data, offset, totalLen, in fields, in context);
                break;
            case 4:
                ParseRoutingSrh(routingField, data, offset, totalLen, in fields, in context);
                break;
            default:
                // Unknown routing type — store raw type-specific data
                if (totalLen > 4)
                {
                    ReadOnlyMemory<byte> typeData = data.Slice(offset + 4, totalLen - 4).ToArray();
                    routingField.Append(fields.RoutingUnknownDataFieldId, FieldValue.NewBytes(typeData), in context);
                }
                break;
        }

        return (nextHeader, totalLen);
    }

    /// <summary>Parses Routing Header Type 2 (Mobile IPv6): 4 bytes reserved + 16 bytes home address.</summary>
    private static void ParseRoutingType2(
        MutField routingField, ReadOnlySpan<byte> data, int offset, int totalLen,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        // Need at least 24 bytes total (4 common + 4 reserved + 16 address)
        if (totalLen < 24)
        {
            return;
        }

        ReadOnlyMemory<byte> reserved = data.Slice(offset + 4, 4).ToArray();
        routingField.Append(fields.RoutingMipv6ReservedFieldId, FieldValue.NewBytes(reserved), in context);

        IPv6Address addr = IPv6Address.FromBytes(data.Slice(offset + 8, 16));
        routingField.Append(fields.RoutingMipv6HomeAddressFieldId, FieldValue.NewIPv6(addr), in context);
    }

    /// <summary>
    /// Parses Routing Header Type 4 (Segment Routing Header).
    /// Layout: last_entry(1) + flags(1) + tag(2) + segment list addresses (16 bytes each).
    /// </summary>
    private static void ParseRoutingSrh(
        MutField routingField, ReadOnlySpan<byte> data, int offset, int totalLen,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        // Need at least 8 bytes for fixed part (4 common + last_entry + flags + tag)
        if (totalLen < 8)
        {
            return;
        }

        byte lastEntry = data[offset + 4];
        byte flags = data[offset + 5];

        routingField.Append(fields.RoutingSrhLastEntryFieldId, FieldValue.NewU64(lastEntry), in context);
        routingField.AppendWithCustomText(
            fields.RoutingSrhFlagsFieldId, FieldValue.NewU64(flags),
            Helpers.DisplayTables.FormatHexU8(flags), in context);

        ReadOnlyMemory<byte> tag = data.Slice(offset + 6, 2).ToArray();
        ushort tagValue = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 6, 2));
        routingField.AppendWithCustomText(
            fields.RoutingSrhTagFieldId, FieldValue.NewBytes(tag),
            Helpers.DisplayTables.FormatHexU16(tagValue), in context);

        // Parse segment list addresses (each 16 bytes, starting at offset+8)
        int addrStart = offset + 8;
        int numAddrs = lastEntry + 1;
        for (int i = 0; i < numAddrs; i++)
        {
            int addrOffset = addrStart + i * 16;
            if (addrOffset + 16 > offset + totalLen)
            {
                break;
            }
            IPv6Address addr = IPv6Address.FromBytes(data.Slice(addrOffset, 16));
            routingField.Append(fields.RoutingSrhAddrFieldId, FieldValue.NewIPv6(addr), in context);
        }
    }

    #endregion

    #region Fragment Header

    /// <summary>Parses a Fragment Header (fixed 8 bytes). Returns (nextHeader, 8) or (0, 0) on failure.</summary>
    private static (byte NextHeader, int Consumed) ParseFragmentHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        if (remaining < FragmentHeaderSize)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte reservedOctet = data[offset + 1];
        ushort offsetFlagsWord = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        ushort fragOffset = (ushort)((offsetFlagsWord & FragOffsetMask) >> 3);
        byte reservedBits = (byte)((offsetFlagsWord & FragReservedMask) >> 1);
        bool moreFragments = (offsetFlagsWord & FragMoreMask) != 0;
        uint identification = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);
        string moreStr = moreFragments ? "true" : "false";

        MutField fragField = protoField.AppendWithCustomText(
            fields.FraghdrFieldId, FieldValue.None,
            (string)ZA.String("Fragment Header (Offset: ", fragOffset,
                ", More: ", moreStr, ", ID: 0x", new Hex8(identification), ")"), in context);

        fragField.AppendWithCustomText(fields.FraghdrNxtFieldId, FieldValue.NewU64(nextHeader), nxtText, in context);
        fragField.Append(fields.FraghdrReservedOctetFieldId, FieldValue.NewU64(reservedOctet), in context);
        fragField.Append(fields.FraghdrOffsetFieldId, FieldValue.NewU64(fragOffset), in context);
        fragField.Append(fields.FraghdrReservedBitsFieldId, FieldValue.NewU64(reservedBits), in context);
        fragField.Append(fields.FraghdrMoreFieldId, FieldValue.NewBool(moreFragments), in context);
        fragField.AppendWithCustomText(
            fields.FraghdrIdentFieldId, FieldValue.NewU64(identification),
            (string)ZA.String("0x", new Hex8(identification)), in context);

        return (nextHeader, FragmentHeaderSize);
    }

    #endregion

    #region Authentication Header (AH)

    /// <summary>Parses an AH. Returns (nextHeader, consumed) or (0, 0) on failure.</summary>
    private static (byte NextHeader, int Consumed) ParseAhHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        if (remaining < AhMinSize)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte payloadLen = data[offset + 1];
        // AH length: (payload_len + 2) * 4 bytes
        int totalLen = (payloadLen + 2) * 4;
        if (remaining < totalLen)
        {
            return (0, 0);
        }

        ushort reserved = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        uint spi = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8, 4));

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);

        MutField ahField = protoField.AppendWithCustomText(
            fields.AhFieldId, FieldValue.None,
            (string)ZA.String("Authentication Header (SPI: 0x", new Hex8(spi), ", Seq: ", seq, ")"), in context);

        ahField.AppendWithCustomText(fields.AhNxtFieldId, FieldValue.NewU64(nextHeader), nxtText, in context);
        ahField.Append(fields.AhLengthFieldId, FieldValue.NewU64(payloadLen), in context);
        ahField.Append(fields.AhReservedFieldId, FieldValue.NewU64(reserved), in context);
        ahField.AppendWithCustomText(
            fields.AhSpiFieldId, FieldValue.NewU64(spi),
            (string)ZA.String("0x", new Hex8(spi)), in context);
        ahField.Append(fields.AhSeqFieldId, FieldValue.NewU64(seq), in context);

        // ICV — remaining bytes after fixed 12 bytes
        int icvLen = totalLen - AhMinSize;
        if (icvLen > 0)
        {
            ReadOnlyMemory<byte> icv = data.Slice(offset + AhMinSize, icvLen).ToArray();
            ahField.Append(fields.AhIcvFieldId, FieldValue.NewBytes(icv), in context);
        }

        return (nextHeader, totalLen);
    }

    #endregion

    #region ESP Header

    /// <summary>Parses an ESP header (SPI + Seq only). Returns consumed bytes (all remaining, since ESP is encrypted).</summary>
    private static int ParseEspHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        if (remaining < EspHeaderSize)
        {
            return 0;
        }

        uint spi = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));

        MutField espField = protoField.AppendWithCustomText(
            fields.EspFieldId, FieldValue.None,
            (string)ZA.String("Encapsulating Security Payload (SPI: 0x", new Hex8(spi),
                ", Seq: ", seq, ")"), in context);

        espField.AppendWithCustomText(
            fields.EspSpiFieldId, FieldValue.NewU64(spi),
            (string)ZA.String("0x", new Hex8(spi)), in context);
        espField.Append(fields.EspSeqFieldId, FieldValue.NewU64(seq), in context);

        // ESP payload is opaque/encrypted — return all remaining bytes as consumed
        return remaining;
    }

    #endregion

    #region TLV Option Parsing (shared for Hop-by-Hop and Destination Options)

    /// <summary>
    /// Parses TLV-encoded options within a Hop-by-Hop or Destination Options header.
    /// </summary>
    /// <param name="parentField">The container field (hopopts or dstopts).</param>
    /// <param name="data">The full extension header data.</param>
    /// <param name="start">Start offset of TLV options data.</param>
    /// <param name="end">End offset of TLV options data.</param>
    /// <param name="fields">Field IDs for option sub-fields.</param>
    /// <param name="context">The parse context providing dispatch resolution and stack access.</param>
    private static void ParseTlvOptions(
        MutField parentField, ReadOnlySpan<byte> data, int start, int end,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        int pos = start;

        while (pos < end)
        {
            byte optType = data[pos];

            if (optType == Pad1)
            {
                // Pad1: single byte, no length field
                parentField.AppendWithCustomText(fields.OptPad1FieldId, FieldValue.None, "Pad1", in context);
                pos += 1;
                continue;
            }

            // All other options have type + length + data
            if (pos + 1 >= end)
            {
                break;
            }

            byte optLen = data[pos + 1];
            int optTotal = 2 + optLen; // type + length + data
            if (pos + optTotal > end)
            {
                break;
            }

            if (optType == PadN)
            {
                parentField.AppendWithCustomText(
                    fields.OptPadnFieldId, FieldValue.None,
                    (string)ZA.String("PadN (", optTotal, " bytes)"), in context);
            }
            else
            {
                // Typed option with full decomposition
                ParseTypedOption(parentField, data, pos, optType, optLen, in fields, in context);
            }

            pos += optTotal;
        }
    }

    /// <summary>Parses a typed TLV option (not Pad1/PadN) and appends its fields.</summary>
    private static void ParseTypedOption(
        MutField parentField, ReadOnlySpan<byte> data, int pos, byte optType, byte optLen,
        in IPv6Protocol.ExtHeaderFieldIds fields, in ParseContext context)
    {
        string optDisplay = DisplayTables.GetIpv6OptionTypeDisplayText(optType);

        MutField optField = parentField.AppendWithCustomText(
            fields.OptFieldId, FieldValue.None, optDisplay, in context);

        // Option type byte with display text
        optField.AppendWithCustomText(fields.OptTypeFieldId, FieldValue.NewU64(optType), optDisplay, in context);

        // Action (high 2 bits)
        byte action = (byte)((optType >> 6) & 0x03);
        string actionText = DisplayTables.GetIpv6OptActionDisplayText(action);
        optField.AppendWithCustomText(fields.OptTypeActionFieldId, FieldValue.NewU64(action), actionText, in context);

        // May change en-route (bit 5)
        bool mayChange = (optType & 0x20) != 0;
        string changeText = mayChange ? "Yes" : "No";
        optField.AppendWithCustomText(fields.OptTypeChangeFieldId, FieldValue.NewBool(mayChange), changeText, in context);

        // Low-order 5 bits
        byte rest = (byte)(optType & 0x1F);
        optField.Append(fields.OptTypeRestFieldId, FieldValue.NewU64(rest), in context);

        // Option data length
        optField.Append(fields.OptLengthFieldId, FieldValue.NewU64(optLen), in context);

        // Parse option-specific data
        int dataStart = pos + 2;
        switch (optType)
        {
            case RouterAlert when optLen >= 2:
                {
                    ushort value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(dataStart, 2));
                    string? alertText = DisplayTables.GetIpv6RouterAlertDisplayText(value);
                    string display = alertText ?? $"Unknown ({value})";
                    optField.AppendWithCustomText(
                        fields.OptRouterAlertFieldId, FieldValue.NewU64(value), display, in context);
                    break;
                }
            case JumboPayload when optLen >= 4:
                {
                    uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(dataStart, 4));
                    optField.Append(fields.OptJumboFieldId, FieldValue.NewU64(payloadLength), in context);
                    break;
                }
            case TunnelEncapsulationLimit when optLen >= 1:
                {
                    byte limit = data[dataStart];
                    optField.Append(fields.OptTelFieldId, FieldValue.NewU64(limit), in context);
                    break;
                }
            case HomeAddress when optLen >= 16:
                {
                    IPv6Address addr = IPv6Address.FromBytes(data.Slice(dataStart, 16));
                    optField.Append(fields.OptHomeAddressFieldId, FieldValue.NewIPv6(addr), in context);
                    break;
                }
            default:
                {
                    // Unknown option — store raw data
                    if (optLen > 0)
                    {
                        ReadOnlyMemory<byte> optData = data.Slice(dataStart, optLen).ToArray();
                        optField.Append(fields.OptUnknownFieldId, FieldValue.NewBytes(optData), in context);
                    }
                    break;
                }
        }
    }
    #endregion
}
