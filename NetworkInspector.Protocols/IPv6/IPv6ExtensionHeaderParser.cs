// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
/// <para>
/// Design: the parser only builds the display fields; it does not decide where the transport payload starts (the
/// IPv6 protocol walks the chain itself). It is tolerant of malformed data because every length comes from the wire:
/// a header that does not fit the remaining bytes stops the walk, and the headers decoded so far stay. Nothing is
/// reported as an error in that case. Every read is checked against the slice that was handed in, so a hostile length
/// can neither read out of range nor cause a large allocation. The walk is limited to
/// <see cref="IPv6Protocol.MaxExtensionHeaders"/> headers.
/// </para>
/// <para><b>Thread-safety:</b> stateless; safe for concurrent use. It appends fields only to the container it is given.</para>
/// <para>
/// Raw byte fields (unknown routing data, unknown options, reserved bytes, the AH ICV) are copied with
/// <c>ToArray</c>: the parser receives a <see cref="ReadOnlySpan{T}"/>, which cannot be stored in a field value that
/// holds memory.
/// </para>
/// </summary>
internal static class IPv6ExtensionHeaderParser
{
    // IPv6 option type constants
    private const byte _Pad1 = 0x00;
    private const byte _PadN = 0x01;
    private const byte _RouterAlert = 0x05;
    private const byte _JumboPayload = 0xC2;
    private const byte _TunnelEncapsulationLimit = 0x04;
    private const byte _HomeAddress = 0xC9;

    // Extension header constants (format facts of RFC 8200 and RFC 4302/4303, hence constants)
    private const int _FragmentHeaderSize = 8;
    private const int _AhMinSize = 12; // next header, length, reserved, SPI, sequence number (3 x 4 bytes fixed part)
    private const int _EspHeaderSize = 8; // SPI + sequence number

    // Fragment header bitmasks over the 16-bit word at byte 2: fragment offset (13 bits, in 8-byte units),
    // 2 reserved bits, and the M flag in the lowest bit.
    private const ushort _FragOffsetMask = 0xFFF8;
    private const ushort _FragReservedMask = 0x0006;
    private const ushort _FragMoreMask = 0x0001;

    /// <summary>
    /// Parses all extension headers from the given data and appends their fields
    /// to the IPv6 protocol field.
    /// </summary>
    /// <param name="protoField">The IPv6 protocol container field.</param>
    /// <param name="data">Extension header data (starting after the 40-byte fixed header).</param>
    /// <param name="firstNextHeader">The next-header value from the IPv6 fixed header.</param>
    /// <param name="fields">Field IDs for extension header sub-fields.</param>
    /// <remarks>
    /// Ends silently when a header is truncated, when the next-header value is not an extension header, after an ESP
    /// header (the rest is encrypted), or after <see cref="IPv6Protocol.MaxExtensionHeaders"/> headers.
    /// </remarks>
    internal static void Parse(
        MutField protoField,
        ReadOnlySpan<byte> data,
        byte firstNextHeader,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        // One extension header per iteration. Each helper returns (next header, bytes consumed); consumed == 0 is the
        // failure sentinel (a valid header always consumes at least 8 bytes), so the loop makes progress or ends.
        // The depth cap bounds the work for a crafted chain of tiny headers (DoS protection, see MaxExtensionHeaders).
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
                        (byte nextHdr, int consumed) = _ParseHopByHopOptions(protoField, data, offset, remaining, in fields);
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
                        (byte nextHdr, int consumed) = _ParseDestinationOptions(protoField, data, offset, remaining, in fields);
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
                        (byte nextHdr, int consumed) = _ParseRoutingHeader(protoField, data, offset, remaining, in fields);
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
                        (byte nextHdr, int consumed) = _ParseFragmentHeader(protoField, data, offset, remaining, in fields);
                        if (consumed == 0)
                        {
                            return;
                        }

                        // A real fragment ends the chain: the bytes after its header are a slice of the fragmentable
                        // part of the original datagram, not a header chain. A non-first fragment starts in the middle
                        // of that part, so a payload byte that happens to equal an extension header number must not be
                        // read as a header. The same rule is applied by IPv6Protocol when it finds the payload start.
                        // consumed != 0 guarantees the 8 header bytes are present, so the flags word can be read.
                        ushort flagsWord = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
                        if (IsRealFragment(flagsWord))
                        {
                            return;
                        }

                        currentNh = nextHdr;
                        offset += consumed;
                        break;
                    }
                case IPv6Protocol.NhAh:
                    {
                        (byte nextHdr, int consumed) = _ParseAhHeader(protoField, data, offset, remaining, in fields);
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
                        // ESP terminates the chain — payload is encrypted. Whether the header fitted is not needed
                        // afterwards: either way the walk ends here.
                        _ParseEspHeader(protoField, data, offset, remaining, in fields);
                        return;
                    }
                default:
                    return; // Unknown ext header — stop
            }

            depth++;
        }
    }

    #region Hop-by-Hop Options

    /// <summary>Parses a Hop-by-Hop Options Header. Returns (nextHeader, consumed) or (0, 0) on failure.</summary>
    /// <remarks>
    /// Generic length encoding (RFC 8200 section 4.3): the Hdr Ext Len byte counts 8-octet units not including the
    /// first 8, so the total is (len + 1) * 8, at most 2048 bytes. It must fit the remaining data, otherwise the
    /// header is rejected.
    /// </remarks>
    private static (byte NextHeader, int Consumed) _ParseHopByHopOptions(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields)
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
            (string)ZA.String("Hop-by-Hop Options Header (", totalLen, " bytes)"));

        hopoptsField.AppendWithCustomText(fields.HopoptsNxtFieldId, FieldValue.NewU64(nextHeader), nxtText);
        hopoptsField.Append(fields.HopoptsLenFieldId, FieldValue.NewU64(hdrExtLen));
        hopoptsField.Append(fields.HopoptsLenOctFieldId, FieldValue.NewU64((ulong)totalLen));

        // Parse TLV options (bytes 2..totalLen relative to offset)
        _ParseTlvOptions(hopoptsField, data, offset + 2, offset + totalLen, in fields);

        return (nextHeader, totalLen);
    }

    #endregion

    #region Destination Options

    /// <summary>
    /// Parses a Destination Options Header. Returns (nextHeader, consumed) or (0, 0) on failure.
    /// Same layout and length encoding as the Hop-by-Hop Options header.
    /// </summary>
    private static (byte NextHeader, int Consumed) _ParseDestinationOptions(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields)
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
            (string)ZA.String("Destination Options Header (", totalLen, " bytes)"));

        dstoptsField.AppendWithCustomText(fields.DstoptsNxtFieldId, FieldValue.NewU64(nextHeader), nxtText);
        dstoptsField.Append(fields.DstoptsLenFieldId, FieldValue.NewU64(hdrExtLen));
        dstoptsField.Append(fields.DstoptsLenOctFieldId, FieldValue.NewU64((ulong)totalLen));

        // Parse TLV options (bytes 2..totalLen relative to offset)
        _ParseTlvOptions(dstoptsField, data, offset + 2, offset + totalLen, in fields);

        return (nextHeader, totalLen);
    }

    #endregion

    #region Routing Header

    /// <summary>
    /// Parses a Routing Header. Returns (nextHeader, consumed) or (0, 0) on failure.
    /// Common part: next header, Hdr Ext Len, routing type, segments left (4 bytes), then type-specific data. Types 2
    /// (Mobile IPv6) and 4 (Segment Routing) are decoded; any other type keeps its data as raw bytes.
    /// </summary>
    private static (byte NextHeader, int Consumed) _ParseRoutingHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields)
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
                ", Segments Left: ", segmentsLeft, ")"));

        routingField.AppendWithCustomText(fields.RoutingNxtFieldId, FieldValue.NewU64(nextHeader), nxtText);
        routingField.Append(fields.RoutingLenFieldId, FieldValue.NewU64(hdrExtLen));
        routingField.Append(fields.RoutingLenOctFieldId, FieldValue.NewU64((ulong)totalLen));
        routingField.AppendWithCustomText(fields.RoutingTypeFieldId, FieldValue.NewU64(routingType), rtDisplayText);
        routingField.Append(fields.RoutingSegleftFieldId, FieldValue.NewU64(segmentsLeft));

        // Parse type-specific data (bytes 4..totalLen relative to offset). totalLen was checked against the remaining
        // bytes above, so every slice below that stays within totalLen is in range.
        switch (routingType)
        {
            case 2:
                _ParseRoutingType2(routingField, data, offset, totalLen, in fields);
                break;
            case 4:
                _ParseRoutingSrh(routingField, data, offset, totalLen, in fields);
                break;
            default:
                // Unknown routing type — store raw type-specific data
                if (totalLen > 4)
                {
                    ReadOnlyMemory<byte> typeData = data.Slice(offset + 4, totalLen - 4).ToArray();
                    routingField.Append(fields.RoutingUnknownDataFieldId, FieldValue.NewBytes(typeData));
                }
                break;
        }

        return (nextHeader, totalLen);
    }

    /// <summary>Parses Routing Header Type 2 (Mobile IPv6): 4 bytes reserved + 16 bytes home address.</summary>
    private static void _ParseRoutingType2(
        MutField routingField, ReadOnlySpan<byte> data, int offset, int totalLen,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        // Need at least 24 bytes total (4 common + 4 reserved + 16 address)
        if (totalLen < 24)
        {
            return;
        }

        ReadOnlyMemory<byte> reserved = data.Slice(offset + 4, 4).ToArray();
        routingField.Append(fields.RoutingMipv6ReservedFieldId, FieldValue.NewBytes(reserved));

        IPv6Address addr = IPv6Address.FromBytes(data.Slice(offset + 8, 16));
        routingField.Append(fields.RoutingMipv6HomeAddressFieldId, FieldValue.NewIPv6(addr));
    }

    /// <summary>
    /// Parses Routing Header Type 4 (Segment Routing Header).
    /// Layout: last_entry(1) + flags(1) + tag(2) + segment list addresses (16 bytes each).
    /// </summary>
    private static void _ParseRoutingSrh(
        MutField routingField, ReadOnlySpan<byte> data, int offset, int totalLen,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        // Need at least 8 bytes for fixed part (4 common + last_entry + flags + tag)
        if (totalLen < 8)
        {
            return;
        }

        byte lastEntry = data[offset + 4];
        byte flags = data[offset + 5];

        routingField.Append(fields.RoutingSrhLastEntryFieldId, FieldValue.NewU64(lastEntry));
        routingField.AppendWithCustomText(
            fields.RoutingSrhFlagsFieldId, FieldValue.NewU64(flags),
            Helpers.DisplayTables.FormatHexU8(flags));

        ReadOnlyMemory<byte> tag = data.Slice(offset + 6, 2).ToArray();
        ushort tagValue = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 6, 2));
        routingField.AppendWithCustomText(
            fields.RoutingSrhTagFieldId, FieldValue.NewBytes(tag),
            Helpers.DisplayTables.FormatHexU16(tagValue));

        // Parse segment list addresses (each 16 bytes, starting at offset+8). Last Entry is the zero-based index of
        // the last segment (RFC 8754), so there are lastEntry + 1 of them (at most 256). The count comes from the wire
        // and is not trusted: the loop stops at the end of this header, so a count that is too large only yields
        // the addresses that are actually present. SRH TLVs after the segment list are not decoded.
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
            routingField.Append(fields.RoutingSrhAddrFieldId, FieldValue.NewIPv6(addr));
        }
    }

    #endregion

    #region Fragment Header

    /// <summary>
    /// Tells whether a fragment header describes a real fragment: a fragment offset other than 0 or the More Fragments
    /// flag set. A header with offset 0 and no More flag is an "atomic fragment" (RFC 8200 section 4.5) that carries the
    /// whole datagram and needs no reassembly. The single definition used by the chain walks of this parser and of
    /// <see cref="IPv6Protocol"/>.
    /// </summary>
    /// <param name="offsetFlagsWord">The 16-bit word at byte 2 of the fragment header (offset, reserved bits, M flag).</param>
    internal static bool IsRealFragment(ushort offsetFlagsWord) =>
        (offsetFlagsWord & (_FragOffsetMask | _FragMoreMask)) != 0;

    /// <summary>
    /// Parses a Fragment Header (fixed 8 bytes). Returns (nextHeader, 8) or (0, 0) on failure.
    /// Layout (RFC 8200 section 4.5): next header, reserved octet, fragment offset (13 bits) + 2 reserved bits + M flag,
    /// 32-bit identification. The fragment offset is reported in 8-octet units, as on the wire.
    /// </summary>
    private static (byte NextHeader, int Consumed) _ParseFragmentHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        if (remaining < _FragmentHeaderSize)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte reservedOctet = data[offset + 1];
        ushort offsetFlagsWord = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        ushort fragOffset = (ushort)((offsetFlagsWord & _FragOffsetMask) >> 3);
        byte reservedBits = (byte)((offsetFlagsWord & _FragReservedMask) >> 1);
        bool moreFragments = (offsetFlagsWord & _FragMoreMask) != 0;
        uint identification = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);
        string moreStr = moreFragments ? "true" : "false";

        MutField fragField = protoField.AppendWithCustomText(
            fields.FraghdrFieldId, FieldValue.None,
            (string)ZA.String("Fragment Header (Offset: ", fragOffset,
                ", More: ", moreStr, ", ID: 0x", new Hex8(identification), ")"));

        fragField.AppendWithCustomText(fields.FraghdrNxtFieldId, FieldValue.NewU64(nextHeader), nxtText);
        fragField.Append(fields.FraghdrReservedOctetFieldId, FieldValue.NewU64(reservedOctet));
        fragField.Append(fields.FraghdrOffsetFieldId, FieldValue.NewU64(fragOffset));
        fragField.Append(fields.FraghdrReservedBitsFieldId, FieldValue.NewU64(reservedBits));
        fragField.Append(fields.FraghdrMoreFieldId, FieldValue.NewBool(moreFragments));
        fragField.AppendWithCustomText(
            fields.FraghdrIdentFieldId, FieldValue.NewU64(identification),
            (string)ZA.String("0x", new Hex8(identification)));

        return (nextHeader, _FragmentHeaderSize);
    }

    #endregion

    #region Authentication Header (AH)

    /// <summary>
    /// Parses an AH. Returns (nextHeader, consumed) or (0, 0) on failure.
    /// Unlike the other extension headers, the length byte counts 4-octet units minus 2 (RFC 4302 section 2.2).
    /// </summary>
    private static (byte NextHeader, int Consumed) _ParseAhHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        if (remaining < _AhMinSize)
        {
            return (0, 0);
        }

        byte nextHeader = data[offset];
        byte payloadLen = data[offset + 1];
        // AH length: (payload_len + 2) * 4 bytes. Below 12 bytes (payload length 0) the header has no room for the
        // sequence number, so it is malformed; reading on would take the sequence number from the next header.
        int totalLen = (payloadLen + 2) * 4;
        if (totalLen < _AhMinSize || remaining < totalLen)
        {
            return (0, 0);
        }

        ushort reserved = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        uint spi = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8, 4));

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);

        MutField ahField = protoField.AppendWithCustomText(
            fields.AhFieldId, FieldValue.None,
            (string)ZA.String("Authentication Header (SPI: 0x", new Hex8(spi), ", Seq: ", seq, ")"));

        ahField.AppendWithCustomText(fields.AhNxtFieldId, FieldValue.NewU64(nextHeader), nxtText);
        ahField.Append(fields.AhLengthFieldId, FieldValue.NewU64(payloadLen));
        ahField.Append(fields.AhReservedFieldId, FieldValue.NewU64(reserved));
        ahField.AppendWithCustomText(
            fields.AhSpiFieldId, FieldValue.NewU64(spi),
            (string)ZA.String("0x", new Hex8(spi)));
        ahField.Append(fields.AhSeqFieldId, FieldValue.NewU64(seq));

        // ICV — remaining bytes after fixed 12 bytes
        int icvLen = totalLen - _AhMinSize;
        if (icvLen > 0)
        {
            ReadOnlyMemory<byte> icv = data.Slice(offset + _AhMinSize, icvLen).ToArray();
            ahField.Append(fields.AhIcvFieldId, FieldValue.NewBytes(icv));
        }

        return (nextHeader, totalLen);
    }

    #endregion

    #region ESP Header

    /// <summary>
    /// Parses an ESP header (SPI + Seq only). Appends nothing when fewer than 8 bytes remain. The bytes after the
    /// first 8 are ciphertext (and trailer/ICV) that cannot be split without the security association, so the chain walk
    /// ends here whether or not the header was present.
    /// </summary>
    private static void _ParseEspHeader(
        MutField protoField, ReadOnlySpan<byte> data, int offset, int remaining,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        if (remaining < _EspHeaderSize)
        {
            return;
        }

        uint spi = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));

        MutField espField = protoField.AppendWithCustomText(
            fields.EspFieldId, FieldValue.None,
            (string)ZA.String("Encapsulating Security Payload (SPI: 0x", new Hex8(spi),
                ", Seq: ", seq, ")"));

        espField.AppendWithCustomText(
            fields.EspSpiFieldId, FieldValue.NewU64(spi),
            (string)ZA.String("0x", new Hex8(spi)));
        espField.Append(fields.EspSeqFieldId, FieldValue.NewU64(seq));
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
    /// <remarks>
    /// Options are walked only inside [start, end): an option whose length runs past <paramref name="end"/> ends the
    /// walk, so the length byte of one option can never make the parser read the next header's bytes. The loop always
    /// advances by at least one byte per option, so it terminates.
    /// </remarks>
    private static void _ParseTlvOptions(
        MutField parentField, ReadOnlySpan<byte> data, int start, int end,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        int pos = start;

        while (pos < end)
        {
            byte optType = data[pos];

            if (optType == _Pad1)
            {
                // Pad1: single byte, no length field
                parentField.AppendWithCustomText(fields.OptPad1FieldId, FieldValue.None, "Pad1");
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

            if (optType == _PadN)
            {
                parentField.AppendWithCustomText(
                    fields.OptPadnFieldId, FieldValue.None,
                    (string)ZA.String("PadN (", optTotal, " bytes)"));
            }
            else
            {
                // Typed option with full decomposition
                _ParseTypedOption(parentField, data, pos, optType, optLen, in fields);
            }

            pos += optTotal;
        }
    }

    /// <summary>
    /// Parses a typed TLV option (not _Pad1/_PadN) and appends its fields. The caller has verified that
    /// <paramref name="optLen"/> bytes of option data exist after the two header bytes; the <c>when</c> guards below
    /// additionally require the length the option type defines before the data is read.
    /// </summary>
    private static void _ParseTypedOption(
        MutField parentField, ReadOnlySpan<byte> data, int pos, byte optType, byte optLen,
        in IPv6Protocol.ExtHeaderFieldIds fields)
    {
        string optDisplay = DisplayTables.GetIpv6OptionTypeDisplayText(optType);

        MutField optField = parentField.AppendWithCustomText(
            fields.OptFieldId, FieldValue.None, optDisplay);

        // Option type byte with display text
        optField.AppendWithCustomText(fields.OptTypeFieldId, FieldValue.NewU64(optType), optDisplay);

        // The option type byte encodes how a node must treat the option (RFC 8200 section 4.2): the two highest bits
        // are the action for an unrecognized option, bit 5 says whether the data may change en route.
        // Action (high 2 bits)
        byte action = (byte)((optType >> 6) & 0x03);
        string actionText = DisplayTables.GetIpv6OptActionDisplayText(action);
        optField.AppendWithCustomText(fields.OptTypeActionFieldId, FieldValue.NewU64(action), actionText);

        // May change en-route (bit 5)
        bool mayChange = (optType & 0x20) != 0;
        string changeText = mayChange ? "Yes" : "No";
        optField.AppendWithCustomText(fields.OptTypeChangeFieldId, FieldValue.NewBool(mayChange), changeText);

        // Low-order 5 bits
        byte rest = (byte)(optType & 0x1F);
        optField.Append(fields.OptTypeRestFieldId, FieldValue.NewU64(rest));

        // Option data length
        optField.Append(fields.OptLengthFieldId, FieldValue.NewU64(optLen));

        // Parse option-specific data
        int dataStart = pos + 2;
        switch (optType)
        {
            case _RouterAlert when optLen >= 2:
                {
                    ushort value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(dataStart, 2));
                    string? alertText = DisplayTables.GetIpv6RouterAlertDisplayText(value);
                    string display = alertText ?? string.Create(CultureInfo.InvariantCulture, $"Unknown ({value})");
                    optField.AppendWithCustomText(
                        fields.OptRouterAlertFieldId, FieldValue.NewU64(value), display);
                    break;
                }
            case _JumboPayload when optLen >= 4:
                {
                    uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(dataStart, 4));
                    optField.Append(fields.OptJumboFieldId, FieldValue.NewU64(payloadLength));
                    break;
                }
            case _TunnelEncapsulationLimit when optLen >= 1:
                {
                    byte limit = data[dataStart];
                    optField.Append(fields.OptTelFieldId, FieldValue.NewU64(limit));
                    break;
                }
            case _HomeAddress when optLen >= 16:
                {
                    IPv6Address addr = IPv6Address.FromBytes(data.Slice(dataStart, 16));
                    optField.Append(fields.OptHomeAddressFieldId, FieldValue.NewIPv6(addr));
                    break;
                }
            default:
                {
                    // Unknown option — store raw data
                    if (optLen > 0)
                    {
                        ReadOnlyMemory<byte> optData = data.Slice(dataStart, optLen).ToArray();
                        optField.Append(fields.OptUnknownFieldId, FieldValue.NewBytes(optData));
                    }
                    break;
                }
        }
    }
    #endregion
}





