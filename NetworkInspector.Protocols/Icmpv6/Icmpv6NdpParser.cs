// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Parses ICMPv6 Neighbor Discovery Protocol (NDP) messages (RFC 4861).
/// Handles Router Solicitation/Advertisement, Neighbor Solicitation/Advertisement,
/// and Redirect messages, including NDP options parsing.
/// </summary>
internal static class Icmpv6NdpParser
{
    // NDP message types
    private const byte _TypeRouterSolicitation = 133;
    private const byte _TypeRouterAdvertisement = 134;
    private const byte _TypeNeighborSolicitation = 135;
    private const byte _TypeNeighborAdvertisement = 136;
    private const byte _TypeRedirect = 137;

    // NDP option types
    private const byte _OptSourceLinkAddr = 1;
    private const byte _OptTargetLinkAddr = 2;
    private const byte _OptPrefixInfo = 3;
    private const byte _OptMtu = 5;
    private const byte _OptRdnss = 25;

    /// <summary>
    /// Returns true if the ICMPv6 type is a known NDP message type (133-137).
    /// </summary>
    internal static bool IsNdpType(byte type) =>
        type is >= _TypeRouterSolicitation and <= _TypeRedirect;

    /// <summary>
    /// Returns the offset at which NDP options begin for the given message type, or -1 when the
    /// fixed header for that type is truncated. Mirrors the per-type length guards in the emitting
    /// Parse* helpers so the eager detection path agrees with what the populator actually emits.
    /// </summary>
    private static int _OptionsOffset(byte type, int bodyLength)
    {
        switch (type)
        {
            case _TypeRouterSolicitation:
                if (bodyLength >= 4)
                {
                    return 4;
                }

                return -1;
            case _TypeRouterAdvertisement:
                if (bodyLength >= 12)
                {
                    return 12;
                }

                return -1;
            case _TypeNeighborSolicitation:
                if (bodyLength >= 20)
                {
                    return 20;
                }

                return -1;
            case _TypeNeighborAdvertisement:
                if (bodyLength >= 20)
                {
                    return 20;
                }

                return -1;
            case _TypeRedirect:
                if (bodyLength >= 36)
                {
                    return 36;
                }

                return -1;
            default:
                return -1;
        }
    }

    /// <summary>
    /// Scans an NDP message body to decide which option-specific presence groups apply, mirroring
    /// the option walk in <see cref="_ParseOptions"/> without emitting any fields. Used by the eager
    /// parse path so the presence index records icmpv6.nd.opt / .prefix / .mtu / .rdnss only when the
    /// lazy populator will actually emit a field in that group (content-consistent, no false positives).
    /// This duplicate walk is the deliberate cost of recording content-dependent groups eagerly while
    /// keeping option field emission lazy.
    /// </summary>
    internal static void DetectGroups(
        ReadOnlySpan<byte> body, byte type,
        out bool hasAnyOption, out bool hasPrefix, out bool hasMtu, out bool hasRdnss)
    {
        hasAnyOption = false;
        hasPrefix = false;
        hasMtu = false;
        hasRdnss = false;

        int optionsOffset = _OptionsOffset(type, body.Length);
        if (optionsOffset <= 0 || optionsOffset >= body.Length)
        {
            return;
        }

        ReadOnlySpan<byte> data = body[optionsOffset..];
        int offset = 0;
        while (offset + 2 <= data.Length)
        {
            byte optType = data[offset];
            byte optLenUnits = data[offset + 1]; // length in 8-byte units

            if (optLenUnits == 0)
            {
                break; // invalid length — matches the _ParseOptions guard
            }

            int optLen = optLenUnits * 8;
            if (offset + optLen > data.Length)
            {
                break; // truncated option
            }

            // _ParseOptions appends an option container for every in-bounds option.
            hasAnyOption = true;

            // Option-specific helpers emit their fields only when the option is long enough,
            // so the group thresholds here match those guards exactly.
            switch (optType)
            {
                case _OptPrefixInfo when optLen >= 32:
                    hasPrefix = true;
                    break;
                case _OptMtu when optLen >= 8:
                    hasMtu = true;
                    break;
                case _OptRdnss when optLen >= 24:
                    hasRdnss = true;
                    break;
            }

            offset += optLen;
        }
    }

    /// <summary>
    /// Parses an NDP message body (after the 4-byte ICMPv6 type/code/checksum header).
    /// The <paramref name="body"/> starts at offset 4 of the ICMPv6 packet.
    /// </summary>
    /// <param name="container">The ICMPv6 container field to append NDP sub-fields to.</param>
    /// <param name="body">The NDP message body (starting after type/code/checksum).</param>
    /// <param name="type">The ICMPv6 type code (133-137).</param>
    /// <param name="f">The registered NDP field IDs.</param>
    internal static void Parse(
        in MutField container,
        ReadOnlySpan<byte> body,
        byte type,
        in Icmpv6NdpFieldIds f)
    {
        // Each NDP message type has a fixed-length header after the 4-byte ICMPv6 header
        int optionsOffset = type switch
        {
            _TypeRouterSolicitation => _ParseRouterSolicitation(in container, body, in f),
            _TypeRouterAdvertisement => _ParseRouterAdvertisement(in container, body, in f),
            _TypeNeighborSolicitation => _ParseNeighborSolicitation(in container, body, in f),
            _TypeNeighborAdvertisement => _ParseNeighborAdvertisement(in container, body, in f),
            _TypeRedirect => _ParseRedirect(in container, body, in f),
            _ => -1 // Should not happen — caller checks IsNdpType first
        };

        // Parse NDP options after the fixed header
        if (optionsOffset > 0 && optionsOffset < body.Length)
        {
            _ParseOptions(in container, body[optionsOffset..], in f);
        }
    }

    /// <summary>
    /// Router Solicitation (type 133): 4 reserved bytes, then options.
    /// </summary>
    private static int _ParseRouterSolicitation(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        // Body: 4 bytes reserved (already consumed as part of body[0..4])
        if (body.Length < 4)
        {
            return -1;
        }
        return 4; // Options start at offset 4
    }

    /// <summary>
    /// Router Advertisement (type 134): cur_hop_limit, M/O flags, router lifetime,
    /// reachable time, retrans timer (12 bytes), then options.
    /// </summary>
    private static int _ParseRouterAdvertisement(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        if (body.Length < 12)
        {
            return -1;
        }

        byte curHopLimit = body[0];
        byte flags = body[1];
        ushort routerLifetime = BinaryPrimitives.ReadUInt16BigEndian(body[2..4]);
        uint reachableTime = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
        uint retransTimer = BinaryPrimitives.ReadUInt32BigEndian(body[8..12]);

        container.Append(f.RaCurHopLimit, FieldValue.NewU64(curHopLimit));

        // Flags
        bool managed = (flags & 0x80) != 0;
        bool other = (flags & 0x40) != 0;
        MutField raFlagsField = container.AppendWithCustomText(
            f.RaFlags, FieldValue.None,
            Icmpv6NdpFlagsFormatter.FormatRa(managed, other));
        raFlagsField.Append(f.RaFlagManaged, FieldValue.NewBool(managed));
        raFlagsField.Append(f.RaFlagOther, FieldValue.NewBool(other));

        // Lifetime and timers
        container.AppendWithCustomText(f.RaRouterLifetime,
            FieldValue.NewU64(routerLifetime),
            ZA.Lazy(routerLifetime, " seconds")); /* seconds */
        container.AppendWithCustomText(f.RaReachableTime,
            FieldValue.NewU64(reachableTime),
            ZA.Lazy(reachableTime, " ms")); /* milliseconds */
        container.AppendWithCustomText(f.RaRetransTimer,
            FieldValue.NewU64(retransTimer),
            ZA.Lazy(retransTimer, " ms")); /* milliseconds */

        return 12; // Options start at offset 12
    }

    /// <summary>
    /// Neighbor Solicitation (type 135): 4 reserved bytes + 16-byte target address, then options.
    /// </summary>
    private static int _ParseNeighborSolicitation(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        if (body.Length < 20)
        {
            return -1;
        }

        _AppendIpv6Address(in container, f.TargetAddress, body[4..20], "Target Address");
        return 20; // Options start at offset 20
    }

    /// <summary>
    /// Neighbor Advertisement (type 136): flags (4 bytes) + 16-byte target address, then options.
    /// </summary>
    private static int _ParseNeighborAdvertisement(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        if (body.Length < 20)
        {
            return -1;
        }

        uint flagsWord = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        bool router = (flagsWord & 0x80000000) != 0;
        bool solicited = (flagsWord & 0x40000000) != 0;
        bool overrideFlag = (flagsWord & 0x20000000) != 0;

        MutField naFlagsField = container.AppendWithCustomText(
            f.NaFlags, FieldValue.None,
            Icmpv6NdpFlagsFormatter.FormatNa(router, solicited, overrideFlag));
        naFlagsField.Append(f.NaFlagRouter, FieldValue.NewBool(router));
        naFlagsField.Append(f.NaFlagSolicited, FieldValue.NewBool(solicited));
        naFlagsField.Append(f.NaFlagOverride, FieldValue.NewBool(overrideFlag));

        _AppendIpv6Address(in container, f.TargetAddress, body[4..20], "Target Address");
        return 20; // Options start at offset 20
    }

    /// <summary>
    /// Redirect (type 137): 4 reserved bytes + 16-byte target + 16-byte destination, then options.
    /// </summary>
    private static int _ParseRedirect(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        if (body.Length < 36)
        {
            return -1;
        }

        _AppendIpv6Address(in container, f.TargetAddress, body[4..20], "Target Address");
        _AppendIpv6Address(in container, f.RedirectDstAddress, body[20..36], "Destination Address");
        return 36; // Options start at offset 36
    }

    /// <summary>
    /// Parses NDP options (TLV format: type=1 byte, length=1 byte in 8-byte units).
    /// </summary>
    private static void _ParseOptions(
        in MutField container, ReadOnlySpan<byte> data, in Icmpv6NdpFieldIds f)
    {
        int offset = 0;
        while (offset + 2 <= data.Length)
        {
            byte optType = data[offset];
            byte optLenUnits = data[offset + 1]; // Length in 8-byte units

            // Length 0 is invalid — prevent infinite loop
            if (optLenUnits == 0)
            {
                break;
            }

            int optLen = optLenUnits * 8; // Convert to bytes (includes type + length fields)
            if (offset + optLen > data.Length)
            {
                break; // Truncated option
            }

            ReadOnlySpan<byte> optData = data.Slice(offset, optLen);
            string optName = _GetOptionName(optType);

            MutField optField = container.AppendWithCustomText(
                f.OptContainer, FieldValue.None,
                ZA.Lazy(optName, " (", optType, ")"));

            optField.Append(f.OptType, FieldValue.NewU64(optType));
            optField.AppendWithCustomText(f.OptLen, FieldValue.NewU64(optLenUnits),
                ZA.Lazy(optLenUnits, " (", optLen, " bytes)"));

            // Parse option-specific data (starts at byte 2 within the option)
            switch (optType)
            {
                case _OptSourceLinkAddr:
                case _OptTargetLinkAddr:
                    _ParseLinkAddrOption(in optField, optData, in f);
                    break;
                case _OptPrefixInfo:
                    _ParsePrefixInfoOption(in optField, optData, in f);
                    break;
                case _OptMtu:
                    _ParseMtuOption(in optField, optData, in f);
                    break;
                case _OptRdnss:
                    _ParseRdnssOption(in optField, optData, in f);
                    break;
            }

            offset += optLen;
        }
    }

    /// <summary>
    /// Parses Source/Target Link-Layer Address option (type 1/2).
    /// Format: type(1) + len(1) + link-layer addr (6 for Ethernet).
    /// </summary>
    private static void _ParseLinkAddrOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        if (optData.Length >= 8) // 2-byte header + 6-byte MAC
        {
            MacAddress mac = MacAddress.FromBytes(optData[2..8]);
            optField.Append(f.OptLinkAddr, FieldValue.NewMacAddress(mac));
        }
    }

    /// <summary>
    /// Parses Prefix Information option (type 3, 32 bytes).
    /// Format: type(1) + len(1) + prefix_length(1) + flags(1) +
    /// valid_lifetime(4) + preferred_lifetime(4) + reserved(4) + prefix(16).
    /// </summary>
    private static void _ParsePrefixInfoOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        if (optData.Length < 32)
        {
            return; // Prefix info option is exactly 32 bytes
        }

        byte prefixLen = optData[2];
        byte flags = optData[3];
        bool onLink = (flags & 0x80) != 0;
        bool autonomous = (flags & 0x40) != 0;
        uint validLifetime = BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]); /* seconds */
        uint preferredLifetime = BinaryPrimitives.ReadUInt32BigEndian(optData[8..12]); /* seconds */

        optField.Append(f.OptPrefixLength, FieldValue.NewU64(prefixLen));
        optField.Append(f.OptPrefixFlagOnLink, FieldValue.NewBool(onLink));
        optField.Append(f.OptPrefixFlagAuto, FieldValue.NewBool(autonomous));
        optField.AppendWithCustomText(f.OptPrefixValidLifetime,
            FieldValue.NewU64(validLifetime),
            validLifetime == 0xFFFFFFFF
                ? new LazyString("Infinity")
                : ZA.Lazy(validLifetime, " seconds")); /* seconds */
        optField.AppendWithCustomText(f.OptPrefixPreferredLifetime,
            FieldValue.NewU64(preferredLifetime),
            preferredLifetime == 0xFFFFFFFF
                ? new LazyString("Infinity")
                : ZA.Lazy(preferredLifetime, " seconds")); /* seconds */

        // Prefix (16 bytes at offset 16)
        _AppendIpv6Address(in optField, f.OptPrefix, optData[16..32], "Prefix");
    }

    /// <summary>
    /// Parses MTU option (type 5, 8 bytes).
    /// Format: type(1) + len(1) + reserved(2) + mtu(4).
    /// </summary>
    private static void _ParseMtuOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        if (optData.Length < 8)
        {
            return;
        }

        uint mtu = BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]);
        optField.Append(f.OptMtu, FieldValue.NewU64(mtu));
    }

    /// <summary>
    /// Parses Recursive DNS Server option (type 25).
    /// Format: type(1) + len(1) + reserved(2) + lifetime(4) + addresses(16 each).
    /// </summary>
    private static void _ParseRdnssOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        if (optData.Length < 24) // Minimum: 8-byte header + at least one 16-byte address
        {
            return;
        }

        uint lifetime = BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]); /* seconds */
        optField.AppendWithCustomText(f.OptRdnssLifetime,
            FieldValue.NewU64(lifetime),
            lifetime == 0xFFFFFFFF
                ? new LazyString("Infinity")
                : ZA.Lazy(lifetime, " seconds")); /* seconds */

        // Each DNS server address is 16 bytes, starting at offset 8
        int addrOffset = 8;
        while (addrOffset + 16 <= optData.Length)
        {
            _AppendIpv6Address(in optField, f.OptRdnssAddress,
                optData[addrOffset..(addrOffset + 16)], "DNS Server");
            addrOffset += 16;
        }
    }

    /// <summary>
    /// Gets a human-readable name for an NDP option type.
    /// </summary>
    private static string _GetOptionName(byte optType) => optType switch
    {
        _OptSourceLinkAddr => "Source Link-Layer Address",
        _OptTargetLinkAddr => "Target Link-Layer Address",
        _OptPrefixInfo => "Prefix Information",
        _OptMtu => "MTU",
        _OptRdnss => "RDNSS",
        31 => "DNS Search List",
        _ => "Unknown Option"
    };

    /// <summary>
    /// Appends an IPv6 address field from a 16-byte span.
    /// </summary>
    private static void _AppendIpv6Address(
        in MutField container, FieldId fieldId, ReadOnlySpan<byte> addr, string label)
    {
        // Read 128-bit IPv6 address as two 64-bit halves (big-endian)
        ulong high = BinaryPrimitives.ReadUInt64BigEndian(addr[..8]);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(addr[8..16]);
        IPv6Address addrValue = new(high, low);

        System.Net.IPAddress ipAddr = new(addr);

        container.AppendWithCustomText(fieldId,
            FieldValue.NewIPv6(addrValue), ZA.Lazy(label, ": ", ipAddr));
    }
}
