// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Index groups of an NDP message; the result of <see cref="Icmpv6NdpParser.DetectGroups"/>. Each bit stands for
/// "at least one field of this index group is emitted", which is what the presence index must record.
/// </summary>
[Flags]
internal enum NdpGroups
{
    /// <summary>Nothing is emitted.</summary>
    None = 0,

    /// <summary>The Router Advertisement fixed fields.</summary>
    RouterAdvertisement = 1 << 0,

    /// <summary>A target address (Neighbor Solicitation, Neighbor Advertisement or Redirect).</summary>
    Target = 1 << 1,

    /// <summary>The Neighbor Advertisement flags.</summary>
    NeighborAdvertisement = 1 << 2,

    /// <summary>The Redirect destination address.</summary>
    Redirect = 1 << 3,

    /// <summary>At least one in-bounds option container exists.</summary>
    Option = 1 << 4,

    /// <summary>A complete Prefix Information option.</summary>
    Prefix = 1 << 5,

    /// <summary>A complete MTU option.</summary>
    Mtu = 1 << 6,

    /// <summary>A complete RDNSS option with at least one address.</summary>
    Rdnss = 1 << 7,

    /// <summary>A Redirected Header option that carries a packet.</summary>
    RedirectedHeader = 1 << 8,

    /// <summary>A complete Advertisement Interval option.</summary>
    AdvertisementInterval = 1 << 9,

    /// <summary>A complete Home Agent Information option.</summary>
    HomeAgent = 1 << 10,

    /// <summary>A Nonce option.</summary>
    Nonce = 1 << 11,

    /// <summary>A well-formed Route Information option.</summary>
    RouteInfo = 1 << 12,

    /// <summary>A complete RA Flags Extension option.</summary>
    FlagsExtension = 1 << 13,

    /// <summary>A complete DNS Search List option.</summary>
    Dnssl = 1 << 14,

    /// <summary>A Captive-Portal option.</summary>
    CaptivePortal = 1 << 15,

    /// <summary>A complete PREF64 option.</summary>
    Pref64 = 1 << 16,

    /// <summary>The option walk ended on a zero-length or truncated option.</summary>
    Error = 1 << 17,
}

/// <summary>
/// Parses ICMPv6 Neighbor Discovery Protocol (NDP) messages (RFC 4861) and their options.
/// Handles Router Solicitation/Advertisement, Neighbor Solicitation/Advertisement and Redirect messages.
/// <para>
/// <b>Eager and lazy agree by construction.</b> The presence index is filled when a packet is parsed
/// (<see cref="DetectGroups"/>), the fields are created when the children are first read (<see cref="Parse"/>).
/// A group recorded without a field, or a field without its group, would make a filter on that group lie. Instead
/// of keeping two copies of every length rule in step by hand, both paths ask the same three functions:
/// <c>_OptionsOffset</c> (is the fixed header complete), <c>_ReadOptionHeader</c> (where does the next option end,
/// and is it malformed) and <c>_OptionContentGroup</c> (does this option carry decodable content). The emitting
/// helpers run only after those functions accepted the option, so they can assume a well-formed input. The cost is
/// one extra scan of the option list per eagerly parsed NDP packet; options are at most a few hundred bytes.
/// </para>
/// <para>
/// <b>Thread safety:</b> static and stateless; all input is a read-only span or memory, so concurrent calls are safe.
/// Malformed input is never an exception: a zero-length or truncated option ends the walk and is reported through
/// the error field, because NDP bodies come from the network and the options decoded so far remain useful.
/// </para>
/// </summary>
internal static class Icmpv6NdpParser
{
    #region Constants

    // NDP message types (RFC 4861 section 4)
    private const byte _TypeRouterSolicitation = 133;
    private const byte _TypeRouterAdvertisement = 134;
    private const byte _TypeNeighborSolicitation = 135;
    private const byte _TypeNeighborAdvertisement = 136;
    private const byte _TypeRedirect = 137;

    // NDP option types. The option length byte counts 8-byte units and includes the type and length bytes themselves.
    // RFC 4861 section 4.6: 1, 2, 3, 4, 5; RFC 6275: 7, 8; RFC 3971: 14; RFC 4191: 24; RFC 8106: 25, 31;
    // RFC 5175: 26; RFC 8910: 37; RFC 8781: 38.
    private const byte _OptSourceLinkAddr = 1;
    private const byte _OptTargetLinkAddr = 2;
    private const byte _OptPrefixInfo = 3;
    private const byte _OptRedirectedHeader = 4;
    private const byte _OptMtu = 5;
    private const byte _OptAdvertisementInterval = 7;
    private const byte _OptHomeAgentInfo = 8;
    private const byte _OptNonce = 14;
    private const byte _OptRouteInfo = 24;
    private const byte _OptRdnss = 25;
    private const byte _OptFlagsExtension = 26;
    private const byte _OptDnssl = 31;
    private const byte _OptCaptivePortal = 37;
    private const byte _OptPref64 = 38;

    // Fixed body sizes of the five message types: the bytes after the 4-byte ICMPv6 header and before the options
    // (RFC 4861 sections 4.1 to 4.5). Router Solicitation: 4 reserved. Router Advertisement: hop limit (1), flags
    // (1), router lifetime (2), reachable time (4), retransmission timer (4). Neighbor Solicitation and
    // Advertisement: 4 reserved or flag bytes and a 16-byte target. Redirect: 4 reserved, target (16) and
    // destination (16).
    private const int _RouterSolicitationBodySize = 4;
    private const int _RouterAdvertisementBodySize = 12;
    private const int _NeighborBodySize = 20;
    private const int _RedirectBodySize = 36;

    /// <summary>Size of one option unit in bytes; option lengths are multiples of it.</summary>
    private const int _OptionUnit = 8;

    /// <summary>Size of the ICMPv6 header (type, code, checksum) that precedes the NDP body; used for message offsets in error texts.</summary>
    private const int _MessageHeaderSize = 4;

    /// <summary>Size of an Ethernet link-layer address option in bytes (type, length, six address bytes).</summary>
    private const int _EthernetLinkAddrOptionSize = 8;

    /// <summary>Size of an EUI-64 link-layer address option in bytes (type, length, eight address bytes, six pad bytes).</summary>
    private const int _Eui64LinkAddrOptionSize = 16;

    /// <summary>Longest DNS name in presentation form: 255 characters (RFC 1035 section 2.3.4).</summary>
    private const int _MaxDomainNameLength = 255;

    #endregion

    #region Message classification

    /// <summary>
    /// Returns true if the ICMPv6 type is a known NDP message type (133-137).
    /// </summary>
    /// <param name="type">ICMPv6 message type byte.</param>
    internal static bool IsNdpType(byte type) =>
        type is >= _TypeRouterSolicitation and <= _TypeRedirect;

    /// <summary>
    /// Returns the offset at which NDP options begin for the given message type, or -1 when the fixed header for that
    /// type is truncated. It is the only place that knows the fixed body sizes, so the eager detection and the lazy
    /// parse cannot disagree about whether a message is complete.
    /// </summary>
    /// <param name="type">NDP message type.</param>
    /// <param name="bodyLength">Length of the NDP body in bytes (after the ICMPv6 type, code and checksum).</param>
    private static int _OptionsOffset(byte type, int bodyLength)
    {
        int required = type switch
        {
            _TypeRouterSolicitation => _RouterSolicitationBodySize,
            _TypeRouterAdvertisement => _RouterAdvertisementBodySize,
            _TypeNeighborSolicitation => _NeighborBodySize,
            _TypeNeighborAdvertisement => _NeighborBodySize,
            _TypeRedirect => _RedirectBodySize,
            _ => -1,
        };

        // A negative size marks a type that is not NDP; a body shorter than the fixed part cannot be decoded.
        if (required < 0 || bodyLength < required)
        {
            return -1;
        }

        return required;
    }

    #endregion

    #region Eager detection

    /// <summary>
    /// Decides which index groups <see cref="Parse"/> will emit a field in, without creating any field. Used by the
    /// eager parse path so the presence index is complete and free of false positives.
    /// <para>
    /// This duplicate scan is the deliberate cost of recording content-dependent groups eagerly while keeping the
    /// field creation lazy. It is bounded by the body length and advances by at least 8 bytes per option, so a
    /// crafted message cannot make it loop longer than its own size allows.
    /// </para>
    /// </summary>
    /// <param name="body">NDP body (after the ICMPv6 type, code and checksum).</param>
    /// <param name="type">NDP message type (133 to 137).</param>
    /// <returns>The groups with at least one emitted field; <see cref="NdpGroups.None"/> for a truncated fixed header.</returns>
    internal static NdpGroups DetectGroups(ReadOnlySpan<byte> body, byte type)
    {
        int optionsOffset = _OptionsOffset(type, body.Length);
        if (optionsOffset < 0)
        {
            return NdpGroups.None;
        }

        // Fixed-header groups follow from the type alone once the header is known to be complete. They live here, not
        // in the protocol class, so that every new message layout has exactly one place to change.
        NdpGroups groups = type switch
        {
            _TypeRouterAdvertisement => NdpGroups.RouterAdvertisement,
            _TypeNeighborSolicitation => NdpGroups.Target,
            _TypeNeighborAdvertisement => NdpGroups.Target | NdpGroups.NeighborAdvertisement,
            _TypeRedirect => NdpGroups.Target | NdpGroups.Redirect,
            _ => NdpGroups.None,
        };

        // Walk the options exactly like _ParseOptions does, through the same header reader.
        ReadOnlySpan<byte> data = body[optionsOffset..];
        int offset = 0;
        while (true)
        {
            OptionStep step = _ReadOptionHeader(data, offset, out byte optType, out _, out int optLen);
            if (step == OptionStep.End)
            {
                break;
            }

            if (step != OptionStep.Option)
            {
                groups |= NdpGroups.Error;
                break;
            }

            // Every in-bounds option gets a container, so the generic option group is present as soon as one exists.
            groups |= NdpGroups.Option | _OptionContentGroup(optType, optLen);
            offset += optLen;
        }

        return groups;
    }

    /// <summary>
    /// Reads the target address of a Neighbor Solicitation or Advertisement and the Redirect target, for the eager
    /// packet-info text. Returns false when the fixed header is truncated or the type has no target.
    /// </summary>
    /// <param name="body">NDP body (after the ICMPv6 type, code and checksum).</param>
    /// <param name="type">NDP message type.</param>
    /// <param name="target">The 128-bit target address when the method returns true.</param>
    internal static bool TryReadTarget(ReadOnlySpan<byte> body, byte type, out IPv6Address target)
    {
        target = default;
        if (type is not (_TypeNeighborSolicitation or _TypeNeighborAdvertisement or _TypeRedirect))
        {
            return false;
        }

        if (_OptionsOffset(type, body.Length) < 0)
        {
            return false;
        }

        target = new IPv6Address(BinaryPrimitives.ReadUInt64BigEndian(body[4..12]), BinaryPrimitives.ReadUInt64BigEndian(body[12..20]));
        return true;
    }

    /// <summary>
    /// Reads the flags of a Neighbor Advertisement (router, solicited, override) for the eager packet-info text.
    /// Returns false when the message is not a complete Neighbor Advertisement.
    /// </summary>
    /// <param name="body">NDP body (after the ICMPv6 type, code and checksum).</param>
    /// <param name="type">NDP message type.</param>
    /// <param name="router">The R flag.</param>
    /// <param name="solicited">The S flag.</param>
    /// <param name="overrideFlag">The O flag.</param>
    internal static bool TryReadNeighborAdvertisementFlags(
        ReadOnlySpan<byte> body, byte type, out bool router, out bool solicited, out bool overrideFlag)
    {
        router = false;
        solicited = false;
        overrideFlag = false;
        if (type != _TypeNeighborAdvertisement || _OptionsOffset(type, body.Length) < 0)
        {
            return false;
        }

        // R, S and O are the top three bits of the 32-bit flags word (RFC 4861 section 4.4).
        uint flagsWord = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        router = (flagsWord & 0x80000000) != 0;
        solicited = (flagsWord & 0x40000000) != 0;
        overrideFlag = (flagsWord & 0x20000000) != 0;
        return true;
    }

    /// <summary>
    /// Finds the first source or target link-layer address option that holds an Ethernet address and returns the
    /// address, for the eager packet-info text ("from aa:bb:..." / "is at aa:bb:..."). Stops at the first malformed
    /// option exactly like the field walk.
    /// </summary>
    /// <param name="body">NDP body (after the ICMPv6 type, code and checksum).</param>
    /// <param name="type">NDP message type.</param>
    /// <param name="optionType">The option type (1 source, 2 target) when the method returns true.</param>
    /// <param name="address">The six-byte address when the method returns true.</param>
    internal static bool TryReadFirstEthernetLinkAddress(
        ReadOnlySpan<byte> body, byte type, out byte optionType, out MacAddress address)
    {
        optionType = 0;
        address = default;
        int optionsOffset = _OptionsOffset(type, body.Length);
        if (optionsOffset < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> data = body[optionsOffset..];
        int offset = 0;
        while (_ReadOptionHeader(data, offset, out byte optType, out _, out int optLen) == OptionStep.Option)
        {
            if (optType is _OptSourceLinkAddr or _OptTargetLinkAddr && optLen == _EthernetLinkAddrOptionSize)
            {
                optionType = optType;
                address = MacAddress.FromBytes(data.Slice(offset + 2, 6));
                return true;
            }

            offset += optLen;
        }

        return false;
    }

    #endregion

    #region Lazy field creation

    /// <summary>
    /// Parses an NDP message body (after the 4-byte ICMPv6 type/code/checksum header).
    /// The <paramref name="body"/> starts at offset 4 of the ICMPv6 message.
    /// </summary>
    /// <param name="container">The ICMPv6 container field to append NDP sub-fields to.</param>
    /// <param name="body">The NDP message body (starting after type/code/checksum).</param>
    /// <param name="type">The ICMPv6 type code (133-137).</param>
    /// <param name="f">The registered NDP field IDs.</param>
    internal static void Parse(
        in MutField container,
        ReadOnlyMemory<byte> body,
        byte type,
        in Icmpv6NdpFieldIds f)
    {
        // Each NDP message type has a fixed-length header. When it is truncated neither fixed fields nor options are
        // emitted; the helpers below may therefore assume the full fixed header is present.
        int optionsOffset = _OptionsOffset(type, body.Length);
        if (optionsOffset < 0)
        {
            return;
        }

        ReadOnlySpan<byte> span = body.Span;
        switch (type)
        {
            case _TypeRouterAdvertisement:
                _ParseRouterAdvertisement(in container, span, in f);
                break;
            case _TypeNeighborSolicitation:
                _AppendIpv6Address(in container, f.NsTargetAddress, span[4..20], "Target Address");
                break;
            case _TypeNeighborAdvertisement:
                _ParseNeighborAdvertisement(in container, span, in f);
                break;
            case _TypeRedirect:
                _AppendIpv6Address(in container, f.RdTargetAddress, span[4..20], "Target Address");
                _AppendIpv6Address(in container, f.RdDestinationAddress, span[20..36], "Destination Address");
                break;
        }

        // Options follow the fixed header; nothing to do when the message ends right after it.
        if (optionsOffset < body.Length)
        {
            _ParseOptions(in container, body[optionsOffset..], _MessageHeaderSize + optionsOffset, in f);
        }
    }

    /// <summary>
    /// Router Advertisement fixed part (12 bytes): current hop limit, the flags byte, router lifetime, reachable
    /// time and retransmission timer.
    /// </summary>
    private static void _ParseRouterAdvertisement(in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        byte curHopLimit = body[0];
        byte flags = body[1];
        ushort routerLifetime = BinaryPrimitives.ReadUInt16BigEndian(body[2..4]);
        uint reachableTime = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
        uint retransTimer = BinaryPrimitives.ReadUInt32BigEndian(body[8..12]);

        container.Append(f.RaCurHopLimit, FieldValue.NewU64(curHopLimit));

        // Flags byte: M 0x80, O 0x40, H 0x20, Prf 0x18, P 0x04, S 0x02, one reserved bit. The container text comes
        // from a 256-entry table indexed by the raw byte, so no string is built per packet. The default router
        // preference (RFC 4191) decides which of several routers a host picks, which is why it gets a decoded field
        // and not just a bit in a text.
        MutField raFlagsField = container.AppendWithCustomText(f.RaFlags, FieldValue.None, new LazyString(Icmpv6NdpFlagsFormatter.FormatRa(flags)));
        raFlagsField.Append(f.RaFlagM, FieldValue.NewBool((flags & 0x80) != 0));
        raFlagsField.Append(f.RaFlagO, FieldValue.NewBool((flags & 0x40) != 0));
        raFlagsField.Append(f.RaFlagH, FieldValue.NewBool((flags & 0x20) != 0));
        byte preference = (byte)((flags & 0x18) >> 3);
        raFlagsField.AppendWithCustomText(f.RaFlagPrf, FieldValue.NewU64(preference), new LazyString(Icmpv6NdpFlagsFormatter.RouterPreferenceText(preference)));
        raFlagsField.Append(f.RaFlagP, FieldValue.NewBool((flags & 0x04) != 0));
        raFlagsField.Append(f.RaFlagS, FieldValue.NewBool((flags & 0x02) != 0));
        raFlagsField.Append(f.RaFlagRsv, FieldValue.NewU64((ulong)(flags & 0x01)));

        // Lifetime (seconds) and timers (milliseconds); 0 in the timers means "unspecified by this router".
        container.AppendWithCustomText(f.RaRouterLifetime, FieldValue.NewU64(routerLifetime), routerLifetime, " seconds");
        container.AppendWithCustomText(f.RaReachableTime, FieldValue.NewU64(reachableTime), reachableTime, " ms");
        container.AppendWithCustomText(f.RaRetransTimer, FieldValue.NewU64(retransTimer), retransTimer, " ms");
    }

    /// <summary>
    /// Neighbor Advertisement fixed part (20 bytes): the 32-bit flags word and the 16-byte target address.
    /// </summary>
    private static void _ParseNeighborAdvertisement(in MutField container, ReadOnlySpan<byte> body, in Icmpv6NdpFieldIds f)
    {
        // The 32-bit word holds R, S, O in its top three bits (RFC 4861 section 4.4); the other 29 bits are reserved.
        uint flagsWord = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        bool router = (flagsWord & 0x80000000) != 0;
        bool solicited = (flagsWord & 0x40000000) != 0;
        bool overrideFlag = (flagsWord & 0x20000000) != 0;

        MutField naFlagsField = container.AppendWithCustomText(
            f.NaFlags, FieldValue.None, new LazyString(Icmpv6NdpFlagsFormatter.FormatNa(router, solicited, overrideFlag)));
        naFlagsField.Append(f.NaFlagR, FieldValue.NewBool(router));
        naFlagsField.Append(f.NaFlagS, FieldValue.NewBool(solicited));
        naFlagsField.Append(f.NaFlagO, FieldValue.NewBool(overrideFlag));
        naFlagsField.Append(f.NaFlagRsv, FieldValue.NewU64(flagsWord & 0x1FFFFFFF));

        _AppendIpv6Address(in container, f.NaTargetAddress, body[4..20], "Target Address");
    }

    #endregion

    #region Options

    /// <summary>Result of reading one option header.</summary>
    private enum OptionStep
    {
        /// <summary>A complete, in-bounds option starts here.</summary>
        Option,

        /// <summary>Fewer than two bytes remain: the list ends cleanly.</summary>
        End,

        /// <summary>The length byte is zero; the walk cannot advance (RFC 4861 section 4.6).</summary>
        ZeroLength,

        /// <summary>The option claims more bytes than remain in the message.</summary>
        Truncated,
    }

    /// <summary>
    /// Reads the type and length of the option at <paramref name="offset"/> and classifies it. The single header
    /// reader shared by the eager scan and the lazy walk, so both stop on exactly the same bytes.
    /// </summary>
    /// <param name="data">The bytes after the fixed NDP header.</param>
    /// <param name="offset">Offset of the option inside <paramref name="data"/>.</param>
    /// <param name="optType">The option type byte when a header could be read.</param>
    /// <param name="optLenUnits">The option length byte (8-byte units) when a header could be read.</param>
    /// <param name="optLen">The option length in bytes when the result is <see cref="OptionStep.Option"/>.</param>
    private static OptionStep _ReadOptionHeader(
        ReadOnlySpan<byte> data, int offset, out byte optType, out byte optLenUnits, out int optLen)
    {
        optType = 0;
        optLenUnits = 0;
        optLen = 0;

        // Fewer than two bytes cannot hold a type and a length. This also ends the loop on an exact fit.
        if (offset + 2 > data.Length)
        {
            return OptionStep.End;
        }

        optType = data[offset];
        optLenUnits = data[offset + 1];

        // Length 0 is invalid (RFC 4861 section 4.6) and would never advance the offset: reporting it instead of
        // looping is what keeps the walk bounded by the message length.
        if (optLenUnits == 0)
        {
            return OptionStep.ZeroLength;
        }

        // At most 255 x 8 = 2040, so the product cannot overflow an int.
        optLen = optLenUnits * _OptionUnit;
        if (offset + optLen > data.Length)
        {
            return OptionStep.Truncated;
        }

        return OptionStep.Option;
    }

    /// <summary>
    /// Decides whether an option of this type and length carries decodable content, and in which index group. The
    /// emitting helpers run only when this returns something other than <see cref="NdpGroups.None"/>, which is what
    /// keeps the eager groups and the lazy fields identical.
    /// </summary>
    /// <param name="optType">The option type byte.</param>
    /// <param name="optLen">The option length in bytes (a multiple of 8, at least 8).</param>
    private static NdpGroups _OptionContentGroup(byte optType, int optLen) => optType switch
    {
        _OptPrefixInfo when optLen >= 32 => NdpGroups.Prefix,
        _OptMtu => NdpGroups.Mtu, // 8 bytes is the smallest option, so every MTU option is complete
        _OptRdnss when optLen >= 24 => NdpGroups.Rdnss, // header unit plus at least one 16-byte address
        _OptRedirectedHeader when optLen > _OptionUnit => NdpGroups.RedirectedHeader, // only the 8-byte header carries nothing
        _OptAdvertisementInterval => NdpGroups.AdvertisementInterval,
        _OptHomeAgentInfo => NdpGroups.HomeAgent,
        _OptNonce => NdpGroups.Nonce,
        _OptRouteInfo when optLen is 8 or 16 or 24 => NdpGroups.RouteInfo, // RFC 4191: 0, 8 or 16 prefix bytes; always true for a valid length
        _OptFlagsExtension => NdpGroups.FlagsExtension,
        _OptDnssl when optLen >= 16 => NdpGroups.Dnssl, // header unit plus at least one unit of names
        _OptCaptivePortal => NdpGroups.CaptivePortal,
        _OptPref64 when optLen >= 16 => NdpGroups.Pref64,
        _ => NdpGroups.None,
    };

    /// <summary>
    /// Parses NDP options (TLV format: type=1 byte, length=1 byte in 8-byte units).
    /// Stops on the same bytes as <see cref="DetectGroups"/> because both use the same header reader.
    /// </summary>
    /// <param name="container">Message container that receives one option container per option.</param>
    /// <param name="data">Bytes after the fixed NDP header.</param>
    /// <param name="dataMessageOffset">Offset of <paramref name="data"/> inside the ICMPv6 message, for error texts.</param>
    /// <param name="f">The registered NDP field IDs.</param>
    private static void _ParseOptions(
        in MutField container, ReadOnlyMemory<byte> data, int dataMessageOffset, in Icmpv6NdpFieldIds f)
    {
        ReadOnlySpan<byte> span = data.Span;

        // Each iteration consumes at least 8 bytes of 'data', so the loop terminates and does work proportional to
        // the (already bounded) packet size; no separate option count limit is needed.
        int offset = 0;
        while (true)
        {
            OptionStep step = _ReadOptionHeader(span, offset, out byte optType, out byte optLenUnits, out int optLen);
            if (step == OptionStep.End)
            {
                return;
            }

            if (step != OptionStep.Option)
            {
                // A malformed option ends the walk. It is reported instead of dropped silently: a zero-length or
                // overlong option is a sign of a broken or hostile sender, and the options decoded so far stay.
                _AppendOptionError(in container, step, dataMessageOffset + offset, offset + (optLenUnits * _OptionUnit) - span.Length, in f);
                return;
            }

            // The name and the length text depend on one byte each, so both come from shared tables.
            MutField optField = container.AppendWithCustomText(
                f.Opt, FieldValue.None, new LazyString(DisplayTables.GetNdpOptionDisplayText(optType)));
            optField.Append(f.OptType, FieldValue.NewU64(optType));
            optField.AppendWithCustomText(
                f.OptLength, FieldValue.NewU64(optLenUnits), new LazyString(DisplayTables.GetNdpOptionLengthDisplayText(optLenUnits)));

            // Option-specific data starts at byte 2 of the option.
            ReadOnlyMemory<byte> optMemory = data.Slice(offset, optLen);
            ReadOnlySpan<byte> optData = optMemory.Span;
            if (optType is _OptSourceLinkAddr or _OptTargetLinkAddr)
            {
                _ParseLinkAddrOption(in optField, optMemory, in f);
            }
            else if (_OptionContentGroup(optType, optLen) != NdpGroups.None)
            {
                _ParseOptionContent(in optField, optType, optMemory, optData, in f);
            }

            offset += optLen;
        }
    }

    /// <summary>Appends the error field for a malformed option.</summary>
    private static void _AppendOptionError(
        in MutField container, OptionStep step, int messageOffset, int excess, in Icmpv6NdpFieldIds f)
    {
        // The strings are built on the malformed path only; a well-formed message never reaches this method.
        string text = step == OptionStep.ZeroLength
            ? string.Create(CultureInfo.InvariantCulture, $"Invalid option length (zero) at offset {messageOffset}")
            : string.Create(CultureInfo.InvariantCulture, $"Option at offset {messageOffset} exceeds the message by {excess} bytes");
        container.Append(f.ErrorOptionLength, FieldValue.NewString(text));
    }

    /// <summary>Dispatches one option whose content is decodable to its helper.</summary>
    private static void _ParseOptionContent(
        in MutField optField, byte optType, ReadOnlyMemory<byte> optMemory, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        switch (optType)
        {
            case _OptPrefixInfo:
                _ParsePrefixInfoOption(in optField, optData, in f);
                break;
            case _OptMtu:
                optField.Append(f.OptMtu, FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(optData[4..8])));
                break;
            case _OptRdnss:
                _ParseRdnssOption(in optField, optData, in f);
                break;
            case _OptRedirectedHeader:
                // 6 reserved bytes, then as much of the redirected packet as fits (RFC 4861 section 4.6.3).
                optField.Append(f.OptRedirectedPacket, FieldValue.NewBytes(optMemory[_OptionUnit..]));
                break;
            case _OptAdvertisementInterval:
                optField.AppendWithCustomText(
                    f.OptAdvertisementInterval,
                    FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(optData[4..8])),
                    BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]),
                    " ms");
                break;
            case _OptHomeAgentInfo:
                optField.Append(f.OptHomeAgentPreference, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(optData[4..6])));
                _AppendLifetime(in optField, f.OptHomeAgentLifetime, BinaryPrimitives.ReadUInt16BigEndian(optData[6..8]));
                break;
            case _OptNonce:
                optField.Append(f.OptNonce, FieldValue.NewBytes(optMemory[2..]));
                break;
            case _OptRouteInfo:
                _ParseRouteInfoOption(in optField, optData, in f);
                break;
            case _OptFlagsExtension:
                optField.Append(f.OptFlagsExtension, FieldValue.NewBytes(optMemory[2..]));
                break;
            case _OptDnssl:
                _ParseDnsslOption(in optField, optData, in f);
                break;
            case _OptCaptivePortal:
                _ParseCaptivePortalOption(in optField, optData, in f);
                break;
            case _OptPref64:
                _ParsePref64Option(in optField, optData, in f);
                break;
        }
    }

    /// <summary>
    /// Source/Target Link-Layer Address option (type 1/2). The address format is defined by the link type, so the
    /// length of the option selects the interpretation instead of assuming Ethernet.
    /// </summary>
    private static void _ParseLinkAddrOption(in MutField optField, ReadOnlyMemory<byte> optMemory, in Icmpv6NdpFieldIds f)
    {
        ReadOnlySpan<byte> optData = optMemory.Span;

        // Three cases, exactly one field each, so the generic option group stays content-consistent:
        //  - 8 bytes: an Ethernet MAC (the overwhelmingly common case).
        //  - 16 bytes whose last six bytes are zero: an EUI-64 (IEEE 802.15.4 / 6LoWPAN, RFC 4944) with padding.
        //  - anything else: the raw address bytes. Showing six of them as a MAC would invent an address, and a wrong
        //    address is worse than none because the field exists to identify the neighbour.
        if (optData.Length == _EthernetLinkAddrOptionSize)
        {
            optField.Append(f.OptLinkAddr, FieldValue.NewMacAddress(MacAddress.FromBytes(optData[2..8])));
        }
        else if (optData.Length == _Eui64LinkAddrOptionSize && !optData[10..16].ContainsAnyExcept((byte)0))
        {
            // The Eui64 value type formats itself as eight colon-separated bytes, so no display text is needed.
            optField.Append(f.OptLinkAddrEui64, FieldValue.NewEui64(Eui64.FromBytes(optData[2..10])));
        }
        else
        {
            optField.Append(f.OptLinkAddrRaw, FieldValue.NewBytes(optMemory[2..]));
        }
    }

    /// <summary>
    /// Prefix Information option (type 3, 32 bytes): prefix length, flags, valid and preferred lifetime, reserved
    /// word and the prefix itself.
    /// </summary>
    private static void _ParsePrefixInfoOption(in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        // Flags (RFC 4861 section 4.6.2): L (on-link) 0x80, A (autonomous address configuration) 0x40; the router
        // address flag R (RFC 6275) 0x20 and P 0x10 were added later; the low nibble is reserved.
        byte prefixLen = optData[2];
        byte flags = optData[3];
        uint validLifetime = BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]);
        uint preferredLifetime = BinaryPrimitives.ReadUInt32BigEndian(optData[8..12]);

        optField.Append(f.PrefixLength, FieldValue.NewU64(prefixLen));
        optField.Append(f.PrefixFlagL, FieldValue.NewBool((flags & 0x80) != 0));
        optField.Append(f.PrefixFlagA, FieldValue.NewBool((flags & 0x40) != 0));
        optField.Append(f.PrefixFlagR, FieldValue.NewBool((flags & 0x20) != 0));
        optField.Append(f.PrefixFlagP, FieldValue.NewBool((flags & 0x10) != 0));
        optField.Append(f.PrefixFlagReserved, FieldValue.NewU64((ulong)(flags & 0x0F)));
        _AppendLifetime(in optField, f.PrefixValidLifetime, validLifetime);
        _AppendLifetime(in optField, f.PrefixPreferredLifetime, preferredLifetime);

        // Prefix (16 bytes at offset 16).
        _AppendIpv6Address(in optField, f.Prefix, optData[16..32], "Prefix");
    }

    /// <summary>
    /// Recursive DNS Server option (type 25): reserved (2), lifetime (4), then one 16-byte address per two units.
    /// </summary>
    private static void _ParseRdnssOption(in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        _AppendLifetime(in optField, f.OptRdnssLifetime, BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]));

        // Each DNS server address is 16 bytes, starting at offset 8 (RFC 8106 section 5.1). A well-formed option has
        // an odd number of 8-byte units (1 header unit + 2 per address); trailing bytes that do not form a whole
        // address are ignored.
        int addrOffset = 8;
        while (addrOffset + 16 <= optData.Length)
        {
            _AppendIpv6Address(in optField, f.OptRdnss, optData[addrOffset..(addrOffset + 16)], "DNS Server");
            addrOffset += 16;
        }
    }

    /// <summary>
    /// Route Information option (type 24, RFC 4191): prefix length, preference, lifetime and a prefix of 0, 8 or 16
    /// bytes depending on the option length.
    /// </summary>
    private static void _ParseRouteInfoOption(in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        byte preference = (byte)((optData[3] & 0x18) >> 3);
        optField.Append(f.OptRouteInfoPrefixLength, FieldValue.NewU64(optData[2]));
        optField.AppendWithCustomText(
            f.OptRouteInfoPreference, FieldValue.NewU64(preference), new LazyString(Icmpv6NdpFlagsFormatter.RouterPreferenceText(preference)));
        _AppendLifetime(in optField, f.OptRouteLifetime, BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]));

        // The prefix field is shorter than 16 bytes in the compact forms; the missing bytes are zero by definition, so
        // the span is zero-extended on the stack (no allocation) and shown as a full address.
        Span<byte> prefix = stackalloc byte[16];
        optData[8..].CopyTo(prefix);
        _AppendIpv6Address(in optField, f.OptRouteInfoPrefix, prefix, "Prefix");
    }

    /// <summary>
    /// DNS Search List option (type 31, RFC 8106): reserved (2), lifetime (4), then domain names in uncompressed DNS
    /// wire format, zero-padded to a multiple of 8 bytes.
    /// </summary>
    private static void _ParseDnsslOption(in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        _AppendLifetime(in optField, f.OptDnsslLifetime, BinaryPrimitives.ReadUInt32BigEndian(optData[4..8]));

        // Names follow one after another; a zero byte where a name should start is padding and ends the list. A name
        // that does not parse (label too long, no terminator, longer than a DNS name may be) also ends it: the list
        // comes from the network, and half a name would be shown as a different domain.
        int offset = 8;
        while (offset < optData.Length && optData[offset] != 0)
        {
            if (!_TryReadDomainName(optData, ref offset, out string name))
            {
                return;
            }

            optField.Append(f.OptDnssl, FieldValue.NewString(name));
        }
    }

    /// <summary>
    /// Reads one uncompressed DNS name ("example.org") starting at <paramref name="offset"/> and advances past its
    /// terminating zero byte.
    /// </summary>
    /// <param name="data">The option bytes.</param>
    /// <param name="offset">Start of the name; advanced only on success.</param>
    /// <param name="name">The name in presentation form when the method returns true.</param>
    private static bool _TryReadDomainName(ReadOnlySpan<byte> data, ref int offset, out string name)
    {
        // 255 characters is the longest presentation-form name, so a stack buffer of that size always suffices and the
        // only allocation is the final string. DNS labels are ASCII in practice; every byte is widened to a char so
        // an invalid byte never throws.
        Span<char> buffer = stackalloc char[_MaxDomainNameLength];
        int written = 0;
        int position = offset;
        while (true)
        {
            if (position >= data.Length)
            {
                name = string.Empty;
                return false; // no terminator before the option ends
            }

            int labelLength = data[position++];
            if (labelLength == 0)
            {
                break;
            }

            // Labels are at most 63 bytes (the two top bits are the compression flags, which are not allowed here),
            // and the label plus the dot must still fit the buffer and the option.
            int dotLength = written > 0
                ? 1
                : 0;
            bool fits = labelLength <= 63
                && position + labelLength <= data.Length
                && written + labelLength + dotLength <= _MaxDomainNameLength;
            if (!fits)
            {
                name = string.Empty;
                return false;
            }

            if (written > 0)
            {
                buffer[written++] = '.';
            }

            for (int i = 0; i < labelLength; i++)
            {
                buffer[written++] = (char)data[position + i];
            }

            position += labelLength;
        }

        name = new string(buffer[..written]);
        offset = position;
        return true;
    }

    /// <summary>
    /// Captive-Portal option (type 37, RFC 8910): the URI as UTF-8, zero-padded to a multiple of 8 bytes.
    /// </summary>
    private static void _ParseCaptivePortalOption(in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        // The padding is zero bytes; the URI ends at the first zero. UTF-8 decoding replaces invalid sequences, so
        // hostile bytes cannot throw. The string is built on the lazy path of a rare option.
        ReadOnlySpan<byte> uri = optData[2..];
        int end = uri.IndexOf((byte)0);
        if (end >= 0)
        {
            uri = uri[..end];
        }

        optField.Append(f.OptCaptivePortal, FieldValue.NewString(Encoding.UTF8.GetString(uri)));
    }

    /// <summary>
    /// PREF64 option (type 38, RFC 8781): a 13-bit scaled lifetime, a 3-bit prefix length code and a 96-bit prefix.
    /// </summary>
    private static void _ParsePref64Option(in MutField optField, ReadOnlySpan<byte> optData, in Icmpv6NdpFieldIds f)
    {
        ushort word = BinaryPrimitives.ReadUInt16BigEndian(optData[2..4]);
        ushort scaledLifetime = (ushort)(word >> 3);
        byte plc = (byte)(word & 0x07);

        // The lifetime is transmitted in units of 8 seconds; the raw value stays the field value, the text is seconds.
        optField.AppendWithCustomText(f.OptPref64ScaledLifetime, FieldValue.NewU64(scaledLifetime), scaledLifetime * 8, " seconds");
        optField.AppendWithCustomText(f.OptPref64Plc, FieldValue.NewU64(plc), new LazyString(_Pref64LengthText(plc)));

        // 12 prefix bytes (96 bits) at offset 4, zero-extended on the stack to a full address.
        Span<byte> prefix = stackalloc byte[16];
        optData.Slice(4, 12).CopyTo(prefix);
        _AppendIpv6Address(in optField, f.OptPref64Prefix, prefix, "Prefix");
    }

    /// <summary>Prefix length for a PREF64 code (RFC 8781 table 1); precomputed strings.</summary>
    private static string _Pref64LengthText(byte plc) => plc switch
    {
        0 => "96 bits (0)",
        1 => "64 bits (1)",
        2 => "56 bits (2)",
        3 => "48 bits (3)",
        4 => "40 bits (4)",
        5 => "32 bits (5)",
        _ => DisplayTables.GetDecimalText(plc),
    };

    #endregion

    #region Shared helpers

    /// <summary>
    /// Appends a lifetime in seconds. 0xFFFFFFFF means "infinity" (RFC 4861 section 4.6.2); the numeric value stays the
    /// raw field value and only the display text changes. The "infinity" text is a shared constant; the other texts
    /// are formatted lazily, when somebody reads them.
    /// </summary>
    private static void _AppendLifetime(in MutField container, FieldId fieldId, uint seconds)
    {
        if (seconds == 0xFFFFFFFF)
        {
            container.AppendWithCustomText(fieldId, FieldValue.NewU64(seconds), new LazyString("Infinity"));
        }
        else
        {
            container.AppendWithCustomText(fieldId, FieldValue.NewU64(seconds), seconds, " seconds");
        }
    }

    /// <summary>
    /// Appends an IPv6 address field from a 16-byte span. The field value is the numeric address; the display text
    /// is "<paramref name="label"/>: address".
    /// </summary>
    /// <param name="container">Container that receives the field.</param>
    /// <param name="fieldId">Field to append.</param>
    /// <param name="addr">Exactly 16 address bytes; the callers slice them from a length-checked option or body.</param>
    /// <param name="label">Prefix of the display text.</param>
    private static void _AppendIpv6Address(in MutField container, FieldId fieldId, ReadOnlySpan<byte> addr, string label)
    {
        // Read the 128-bit address as two 64-bit halves (big-endian). IPv6Address is ISpanFormattable, so the lazy text
        // captures the 16-byte value inline and formats it only when somebody reads it. No System.Net.IPAddress is
        // created: that type copies the bytes into objects of its own, two allocations per address for a text nobody may read.
        IPv6Address addrValue = new(BinaryPrimitives.ReadUInt64BigEndian(addr[..8]), BinaryPrimitives.ReadUInt64BigEndian(addr[8..16]));
        container.AppendWithCustomText(fieldId, FieldValue.NewIPv6(addrValue), label, ": ", addrValue);
    }

    #endregion
}
