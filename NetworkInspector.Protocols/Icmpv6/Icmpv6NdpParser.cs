// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Parses ICMPv6 Neighbor Discovery Protocol (NDP) messages (RFC 4861).
/// Handles Router Solicitation/Advertisement, Neighbor Solicitation/Advertisement,
/// and Redirect messages, including NDP options parsing.
/// </summary>
internal static class Icmpv6NdpParser
{
    // NDP message types
    private const byte TypeRouterSolicitation = 133;
    private const byte TypeRouterAdvertisement = 134;
    private const byte TypeNeighborSolicitation = 135;
    private const byte TypeNeighborAdvertisement = 136;
    private const byte TypeRedirect = 137;

    // NDP option types
    private const byte OptSourceLinkAddr = 1;
    private const byte OptTargetLinkAddr = 2;
    private const byte OptPrefixInfo = 3;
    private const byte OptMtu = 5;
    private const byte OptRdnss = 25;

    /// <summary>
    /// Returns true if the ICMPv6 type is a known NDP message type (133-137).
    /// </summary>
    internal static bool IsNdpType(byte type) =>
        type is >= TypeRouterSolicitation and <= TypeRedirect;

    /// <summary>
    /// Parses an NDP message body (after the 4-byte ICMPv6 type/code/checksum header).
    /// The <paramref name="body"/> starts at offset 4 of the ICMPv6 packet.
    /// </summary>
    /// <param name="container">The ICMPv6 container field to append NDP sub-fields to.</param>
    /// <param name="body">The NDP message body (starting after type/code/checksum).</param>
    /// <param name="type">The ICMPv6 type code (133-137).</param>
    /// <param name="f">The registered NDP field IDs.</param>
    /// <param name="context">The parse context providing dispatch resolution and stack access.</param>
    internal static void Parse(
        in MutField container,
        ReadOnlySpan<byte> body,
        byte type,
        in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        // Each NDP message type has a fixed-length header after the 4-byte ICMPv6 header
        int optionsOffset = type switch
        {
            TypeRouterSolicitation => ParseRouterSolicitation(in container, body, in f, in context),
            TypeRouterAdvertisement => ParseRouterAdvertisement(in container, body, in f, in context),
            TypeNeighborSolicitation => ParseNeighborSolicitation(in container, body, in f, in context),
            TypeNeighborAdvertisement => ParseNeighborAdvertisement(in container, body, in f, in context),
            TypeRedirect => ParseRedirect(in container, body, in f, in context),
            _ => -1 // Should not happen — caller checks IsNdpType first
        };

        // Parse NDP options after the fixed header
        if (optionsOffset > 0 && optionsOffset < body.Length)
        {
            ParseOptions(in container, body[optionsOffset..], in f, in context);
        }
    }

    /// <summary>
    /// Router Solicitation (type 133): 4 reserved bytes, then options.
    /// </summary>
    private static int ParseRouterSolicitation(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f, in ParseContext context)
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
    private static int ParseRouterAdvertisement(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        // Body layout (12 bytes):
        //   Byte 0: Cur Hop Limit
        //   Byte 1: Flags (M=0x80, O=0x40)
        //   Bytes 2-3: Router Lifetime (seconds)
        //   Bytes 4-7: Reachable Time (ms)
        //   Bytes 8-11: Retrans Timer (ms)
        if (body.Length < 12)
        {
            return -1;
        }

        byte curHopLimit = body[0];
        byte flags = body[1];
        ushort routerLifetime = BinaryPrimitives.ReadUInt16BigEndian(body[2..4]);
        uint reachableTime = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
        uint retransTimer = BinaryPrimitives.ReadUInt32BigEndian(body[8..12]);

        container.Append(f.RaCurHopLimit, FieldValue.NewU64(curHopLimit), in context);

        // Flags
        bool managed = (flags & 0x80) != 0;
        bool other = (flags & 0x40) != 0;
        MutField raFlagsField = container.AppendWithCustomText(
            f.RaFlags, FieldValue.None,
            Icmpv6NdpFlagsFormatter.FormatRa(managed, other), in context);
        raFlagsField.Append(f.RaFlagManaged, FieldValue.NewBool(managed), in context);
        raFlagsField.Append(f.RaFlagOther, FieldValue.NewBool(other), in context);

        // Lifetime and timers
        container.AppendWithCustomText(f.RaRouterLifetime,
            FieldValue.NewU64(routerLifetime),
            ZA.Lazy(routerLifetime, " seconds"), in context); /* seconds */
        container.AppendWithCustomText(f.RaReachableTime,
            FieldValue.NewU64(reachableTime),
            ZA.Lazy(reachableTime, " ms"), in context); /* milliseconds */
        container.AppendWithCustomText(f.RaRetransTimer,
            FieldValue.NewU64(retransTimer),
            ZA.Lazy(retransTimer, " ms"), in context); /* milliseconds */

        return 12; // Options start at offset 12
    }

    /// <summary>
    /// Neighbor Solicitation (type 135): 4 reserved bytes + 16-byte target address, then options.
    /// </summary>
    private static int ParseNeighborSolicitation(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        // Body: 4 bytes reserved + 16 bytes target address = 20 bytes
        if (body.Length < 20)
        {
            return -1;
        }

        AppendIpv6Address(in container, f.TargetAddress, body[4..20], "Target Address", in context);
        return 20; // Options start at offset 20
    }

    /// <summary>
    /// Neighbor Advertisement (type 136): flags (4 bytes) + 16-byte target address, then options.
    /// </summary>
    private static int ParseNeighborAdvertisement(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        // Body: 4 bytes (flags + reserved) + 16 bytes target address = 20 bytes
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
            Icmpv6NdpFlagsFormatter.FormatNa(router, solicited, overrideFlag), in context);
        naFlagsField.Append(f.NaFlagRouter, FieldValue.NewBool(router), in context);
        naFlagsField.Append(f.NaFlagSolicited, FieldValue.NewBool(solicited), in context);
        naFlagsField.Append(f.NaFlagOverride, FieldValue.NewBool(overrideFlag), in context);

        AppendIpv6Address(in container, f.TargetAddress, body[4..20], "Target Address", in context);
        return 20; // Options start at offset 20
    }

    /// <summary>
    /// Redirect (type 137): 4 reserved bytes + 16-byte target + 16-byte destination, then options.
    /// </summary>
    private static int ParseRedirect(
        in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        // Body: 4 bytes reserved + 16 bytes target + 16 bytes destination = 36 bytes
        if (body.Length < 36)
        {
            return -1;
        }

        AppendIpv6Address(in container, f.TargetAddress, body[4..20], "Target Address", in context);
        AppendIpv6Address(in container, f.RedirectDstAddress, body[20..36], "Destination Address", in context);
        return 36; // Options start at offset 36
    }

    /// <summary>
    /// Parses NDP options (TLV format: type=1 byte, length=1 byte in 8-byte units).
    /// </summary>
    private static void ParseOptions(
        in MutField container, ReadOnlySpan<byte> data, in Icmpv6NdpFieldIds f, in ParseContext context)
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
            string optName = GetOptionName(optType);

            MutField optField = container.AppendWithCustomText(
                f.OptContainer, FieldValue.None,
                ZA.Lazy(optName, " (", optType, ")"), in context);

            optField.Append(f.OptType, FieldValue.NewU64(optType), in context);
            optField.AppendWithCustomText(f.OptLen, FieldValue.NewU64(optLenUnits),
                ZA.Lazy(optLenUnits, " (", optLen, " bytes)"), in context);

            // Parse option-specific data (starts at byte 2 within the option)
            switch (optType)
            {
                case OptSourceLinkAddr:
                case OptTargetLinkAddr:
                    ParseLinkAddrOption(in optField, optData, in f, in context);
                    break;
                case OptPrefixInfo:
                    ParsePrefixInfoOption(in optField, optData, in f, in context);
                    break;
                case OptMtu:
                    ParseMtuOption(in optField, optData, in f, in context);
                    break;
                case OptRdnss:
                    ParseRdnssOption(in optField, optData, in f, in context);
                    break;
            }

            offset += optLen;
        }
    }

    /// <summary>
    /// Parses Source/Target Link-Layer Address option (type 1/2).
    /// Format: type(1) + len(1) + link-layer addr (6 for Ethernet).
    /// </summary>
    private static void ParseLinkAddrOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        if (optData.Length >= 8) // 2-byte header + 6-byte MAC
        {
            MacAddress mac = MacAddress.FromBytes(optData[2..8]);
            optField.Append(f.OptLinkAddr, FieldValue.NewMacAddress(mac), in context);
        }
    }

    /// <summary>
    /// Parses Prefix Information option (type 3, 32 bytes).
    /// Format: type(1) + len(1) + prefix_length(1) + flags(1) +
    /// valid_lifetime(4) + preferred_lifetime(4) + reserved(4) + prefix(16).
    /// </summary>
    private static void ParsePrefixInfoOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f, in ParseContext context)
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

        optField.Append(f.OptPrefixLength, FieldValue.NewU64(prefixLen), in context);
        optField.Append(f.OptPrefixFlagOnLink, FieldValue.NewBool(onLink), in context);
        optField.Append(f.OptPrefixFlagAuto, FieldValue.NewBool(autonomous), in context);
        optField.AppendWithCustomText(f.OptPrefixValidLifetime,
            FieldValue.NewU64(validLifetime),
            validLifetime == 0xFFFFFFFF
                ? new LazyString("Infinity")
                : ZA.Lazy(validLifetime, " seconds"), in context); /* seconds */
        optField.AppendWithCustomText(f.OptPrefixPreferredLifetime,
            FieldValue.NewU64(preferredLifetime),
            preferredLifetime == 0xFFFFFFFF
                ? new LazyString("Infinity")
                : ZA.Lazy(preferredLifetime, " seconds"), in context); /* seconds */

        // Prefix (16 bytes at offset 16)
        AppendIpv6Address(in optField, f.OptPrefix, optData[16..32], "Prefix", in context);
    }

    /// <summary>
    /// Parses MTU option (type 5, 8 bytes).
    /// Format: type(1) + len(1) + reserved(2) + mtu(4).
    /// </summary>
    private static void ParseMtuOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f, in ParseContext context)
    {
        if (optData.Length < 8)
        {
            return;
        }

        uint mtu = BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]);
        optField.Append(f.OptMtu, FieldValue.NewU64(mtu), in context);
    }

    /// <summary>
    /// Parses Recursive DNS Server option (type 25).
    /// Format: type(1) + len(1) + reserved(2) + lifetime(4) + addresses(16 each).
    /// </summary>
    private static void ParseRdnssOption(
        in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f, in ParseContext context)
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
                : ZA.Lazy(lifetime, " seconds"), in context); /* seconds */

        // Each DNS server address is 16 bytes, starting at offset 8
        int addrOffset = 8;
        while (addrOffset + 16 <= optData.Length)
        {
            AppendIpv6Address(in optField, f.OptRdnssAddress,
                optData[addrOffset..(addrOffset + 16)], "DNS Server", in context);
            addrOffset += 16;
        }
    }

    /// <summary>
    /// Gets a human-readable name for an NDP option type.
    /// </summary>
    private static string GetOptionName(byte optType) => optType switch
    {
        OptSourceLinkAddr => "Source Link-Layer Address",
        OptTargetLinkAddr => "Target Link-Layer Address",
        OptPrefixInfo => "Prefix Information",
        OptMtu => "MTU",
        OptRdnss => "RDNSS",
        31 => "DNS Search List",
        _ => "Unknown Option"
    };

    /// <summary>
    /// Appends an IPv6 address field from a 16-byte span.
    /// </summary>
    private static void AppendIpv6Address(
        in MutField container, FieldId fieldId, ReadOnlySpan<byte> addr, string label, in ParseContext context)
    {
        // Read 128-bit IPv6 address as two 64-bit halves (big-endian)
        ulong high = BinaryPrimitives.ReadUInt64BigEndian(addr[..8]);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(addr[8..16]);
        IPv6Address addrValue = new(high, low);

        System.Net.IPAddress ipAddr = new(addr);

        container.AppendWithCustomText(fieldId,
            FieldValue.NewIPv6(addrValue), ZA.Lazy(label, ": ", ipAddr), in context);
    }
}
