// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Internet Control Message Protocol (RFC 792) parser with optional checksum validation.
/// <para>Field tree structure:</para>
/// <code>
/// icmp: Internet Control Message Protocol
/// ├── icmp.type: 8 (Echo Request)
/// ├── icmp.code: 0
/// ├── icmp.checksum: 0xabcd
/// ├── icmp.checksum.status: [Good]           [optional, when verification enabled]
/// ├── icmp.ident: 0x1234                     [optional, echo only]
/// ├── icmp.seq: 1                            [optional, echo only]
/// ├── icmp.seq_le: 256                       [optional, echo only]
/// ├── icmp.redirect_gw: 10.0.0.1              [optional, redirect type 5]
/// ├── icmp.data: (32 bytes)                  [optional, payload]
/// └── icmp.resp_in_ip: Internet Protocol       [optional, error types 3/5/11/12]
///     ├── icmp.resp_in_ip.src: 192.168.1.1
///     ├── icmp.resp_in_ip.dst: 10.0.0.1
///     ├── icmp.resp_in_ip.proto: 6 (TCP)
///     └── icmp.resp_in_ip.ttl: 64
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("icmp", "Internet Control Message Protocol", Description = "ICMP (RFC 792)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class IcmpProtocol : IProtocol
{
    #region Constants

    /// <summary>IP protocol number for ICMP (1).</summary>
    public const ulong IpProtoKey = 1;

    /// <summary>ICMP header size in bytes (always 8).</summary>
    private const int _HeaderSize = 8;

    /// <summary>Size of the smallest IPv4 header (no options) in bytes; the quoted header of an error message needs at least this much.</summary>
    private const int _MinEmbeddedIpHeaderSize = 20;

    /// <summary>Index group for always-present ICMP fields.</summary>
    private const string _IcmpIndexGroup = "icmp";

    /// <summary>ICMP type: Echo Reply.</summary>
    private const byte _TypeEchoReply = 0;

    /// <summary>ICMP type: Echo Request.</summary>
    private const byte _TypeEchoRequest = 8;

    /// <summary>ICMP type: Destination Unreachable.</summary>
    private const byte _TypeDestUnreach = 3;

    /// <summary>ICMP type: Redirect.</summary>
    private const byte _TypeRedirect = 5;

    /// <summary>ICMP type: Time Exceeded.</summary>
    private const byte _TypeTimeExceeded = 11;

    /// <summary>ICMP type: Parameter Problem.</summary>
    private const byte _TypeParamProblem = 12;

    #endregion

    #region Fields

    [BytesField("icmp", "ICMP", IndexGroup = _IcmpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("icmp.type", "Type", IndexGroup = _IcmpIndexGroup)]
    private FieldId _TypeFieldId;

    [U64Field("icmp.code", "Code", IndexGroup = _IcmpIndexGroup)]
    private FieldId _CodeFieldId;

    [U64Field("icmp.checksum", "Checksum", IndexGroup = _IcmpIndexGroup)]
    private FieldId _ChecksumFieldId;

    // Optional: checksum validation status
    [StringField("icmp.checksum.status", "Checksum Status", IndexGroup = "icmp.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    // Optional: echo request/reply fields
    [U64Field("icmp.ident", "Identifier (BE)", IndexGroup = "icmp.echo")]
    private FieldId _IdentFieldId;

    [U64Field("icmp.seq", "Sequence Number (BE)", IndexGroup = "icmp.echo")]
    private FieldId _SeqFieldId;

    [U64Field("icmp.seq_le", "Sequence Number (LE)", IndexGroup = "icmp.echo")]
    private FieldId _SeqLeFieldId;

    // Optional: payload data
    [BytesField("icmp.data", "Data", IndexGroup = "icmp.data")]
    private FieldId _DataFieldId;

    // Optional: redirect gateway address (type 5)
    [IPv4Field("icmp.redirect_gw", "Gateway Address", IndexGroup = "icmp.redirect")]
    private FieldId _RedirectGwFieldId;

    // Optional: embedded IP header in error messages (types 3, 5, 11, 12)
    [NoneField("icmp.resp_in_ip", "Internet Protocol (embedded)", IndexGroup = "icmp.resp_in_ip")]
    private FieldId _RespInIpFieldId;

    [IPv4Field("icmp.resp_in_ip.src", "Source Address", IndexGroup = "icmp.resp_in_ip")]
    private FieldId _RespInIpSrcFieldId;

    [IPv4Field("icmp.resp_in_ip.dst", "Destination Address", IndexGroup = "icmp.resp_in_ip")]
    private FieldId _RespInIpDstFieldId;

    [U64Field("icmp.resp_in_ip.proto", "Protocol", IndexGroup = "icmp.resp_in_ip")]
    private FieldId _RespInIpProtoFieldId;

    [U64Field("icmp.resp_in_ip.ttl", "Time to Live", IndexGroup = "icmp.resp_in_ip")]
    private FieldId _RespInIpTtlFieldId;

    // Settings. Loaded once when the stack starts and constant while it runs; Parse and the lazy populator both read
    // it and must therefore agree (the index groups recorded in Parse mirror what the populator emits).
    [BoolSetting("icmp.verify_checksum", "Verify Checksum", "icmp", Default = false)]
    private bool _VerifyChecksum;

    // Pre-allocated populator delegate: one instance for all packets, so Parse does not allocate a closure per packet.
    private LazyPopulator _Populator = null!;

    partial void _OnStartCustom(Stack stack) =>
        _Populator = _PopulateIcmpFields;

    /// <summary>
    /// Populates ICMP child fields lazily from stored datagram bytes.
    /// <para>
    /// Thread safety: reads only the immutable container bytes and start-time state, so it may run concurrently for
    /// different packets.
    /// </para>
    /// </summary>
    private ParseResult _PopulateIcmpFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> icmpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        if (icmpData.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)icmpData.Length);
        }

        // ICMP header (RFC 792): type(1), code(1), checksum(2), then 4 bytes whose meaning depends on the type.
        // All multi-byte values are big endian (network order).
        ReadOnlySpan<byte> span = icmpData.Span;
        byte type = span[0];
        byte code = span[1];
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);

        // Type with display text
        string typeText = DisplayTables.GetIcmpTypeDisplayText(type);
        container.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(type), typeText);

        // Code with display text (per type)
        string codeText = DisplayTables.GetIcmpCodeDisplayText(type, code);
        container.AppendWithCustomText(_CodeFieldId, FieldValue.NewU64(code), codeText);

        // Checksum
        string csumText = DisplayTables.FormatHexU16(checksum);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText);

        // Checksum validation
        if (_VerifyChecksum)
        {
            // ICMP checksum covers the entire ICMP message (header + data), unlike ICMPv6 there is no pseudo header.
            // Summing the message including the transmitted checksum yields 0 for a valid message (RFC 1071).
            ushort computed = InternetChecksum.Compute(icmpData.Span);
            bool valid = computed == 0;
            string statusText = valid ? "[Good]" : "[Bad]";
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        // Echo Request/Reply: identifier and sequence number. The sequence number is exposed in both byte orders
        // because RFC 792 sends it in network order, but some implementations write it in host (little endian)
        // order; showing both lets the reader recognize either kind of sender without a decoder setting.
        bool isEcho = type == _TypeEchoRequest || type == _TypeEchoReply;
        if (isEcho)
        {
            ushort ident = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
            ushort seqBe = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
            ushort seqLe = BinaryPrimitives.ReadUInt16LittleEndian(span[6..8]);

            string identHex = DisplayTables.FormatHexU16(ident);
            container.AppendWithCustomText(_IdentFieldId, FieldValue.NewU64(ident), identHex);
            container.Append(_SeqFieldId, FieldValue.NewU64(seqBe));
            container.Append(_SeqLeFieldId, FieldValue.NewU64(seqLe));
        }

        // Redirect (type 5): Gateway IP address at bytes 4-7
        if (type == _TypeRedirect)
        {
            IPv4Address gateway = new(BinaryPrimitives.ReadUInt32BigEndian(span[4..8]));
            container.Append(_RedirectGwFieldId, FieldValue.NewIPv4(gateway));
        }

        // Error messages (types 3, 5, 11, 12): parse embedded IP header after 8-byte ICMP header
        bool isErrorType = type == _TypeDestUnreach || type == _TypeRedirect
            || type == _TypeTimeExceeded || type == _TypeParamProblem;
        if (isErrorType && icmpData.Length > _HeaderSize)
        {
            _AppendEmbeddedIpHeader(in container, span[_HeaderSize..]);
        }

        // Payload data (bytes after the 8-byte header, for non-error types)
        if (!isErrorType && icmpData.Length > _HeaderSize)
        {
            ReadOnlyMemory<byte> payloadData = icmpData[_HeaderSize..];
            container.Append(_DataFieldId, FieldValue.NewBytes(payloadData));
        }

        return 0;
    }

    /// <summary>
    /// Tells whether the bytes quoted in an ICMP error message start with a complete IPv4 header. The single place for
    /// this decision: the eager index group and the lazy populator both call it, so they cannot drift apart.
    /// </summary>
    /// <param name="embeddedData">Bytes after the 8-byte ICMP header.</param>
    private static bool _HasEmbeddedIpv4Header(ReadOnlySpan<byte> embeddedData)
    {
        // The minimum IPv4 header is 20 bytes. Every field read in _AppendEmbeddedIpHeader lies inside these 20 bytes, so
        // the IHL and any IPv4 options are irrelevant here and are not parsed. The version nibble must be 4.
        return embeddedData.Length >= _MinEmbeddedIpHeaderSize
            && (embeddedData[0] >> 4) == 4;
    }

    /// <summary>
    /// Parses and appends embedded IPv4 header fields from ICMP error messages.
    /// ICMP error types (3, 5, 11, 12) include the original IP header + 8 bytes of the
    /// original datagram payload after the ICMP header. A truncated or non-IPv4 embedded datagram is skipped
    /// silently (no field is appended), because the quoted bytes come from the remote sender and are only
    /// informational.
    /// </summary>
    /// <param name="container">ICMP container that receives the embedded header container.</param>
    /// <param name="embeddedData">Bytes after the 8-byte ICMP header.</param>
    private void _AppendEmbeddedIpHeader(in MutField container, ReadOnlySpan<byte> embeddedData)
    {
        // Same test as the eager index group in Parse (see _HasEmbeddedIpv4Header).
        if (!_HasEmbeddedIpv4Header(embeddedData))
        {
            return;
        }

        // IPv4 header offsets: TTL at 8, protocol at 9, source at 12..15, destination at 16..19.
        byte ttl = embeddedData[8];
        byte protocol = embeddedData[9];
        IPv4Address srcAddr = new(BinaryPrimitives.ReadUInt32BigEndian(embeddedData[12..16]));
        IPv4Address dstAddr = new(BinaryPrimitives.ReadUInt32BigEndian(embeddedData[16..20]));

        string protoText = DisplayTables.GetIpProtocolDisplayText(protocol);
        MutField ipContainer = container.AppendWithCustomText(
            _RespInIpFieldId, FieldValue.None,
            "Internet Protocol, Src: ", srcAddr, ", Dst: ", dstAddr);

        ipContainer.Append(_RespInIpSrcFieldId, FieldValue.NewIPv4(srcAddr));
        ipContainer.Append(_RespInIpDstFieldId, FieldValue.NewIPv4(dstAddr));
        ipContainer.AppendWithCustomText(_RespInIpProtoFieldId,
            FieldValue.NewU64(protocol), protoText);
        ipContainer.Append(_RespInIpTtlFieldId, FieldValue.NewU64(ttl));
    }

    /// <summary>
    /// Parses an ICMP message from the supplied <paramref name="data"/> buffer. Only the type is read eagerly (for
    /// index groups and the packet info text); all other fields are created by the lazy populator when the
    /// container's children are first accessed. ICMP has no payload protocol, so nothing is dispatched.
    /// <para>Thread safety: stateless apart from start-time settings; may be called concurrently.</para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_IcmpGroupId);

        ReadOnlySpan<byte> span = data.Span;
        byte type = span[0];

        // Record optional index groups. The index must be complete without materializing the lazy fields, so the
        // decision is made here from the type byte and the length. The conditions mirror what _PopulateIcmpFields
        // emits (a recorded group without an emitted field would make a filter on that group report a packet that
        // shows nothing for it).
        bool isEcho = type == _TypeEchoRequest || type == _TypeEchoReply;
        if (isEcho)
        {
            context.RecordGroupPresence(_IcmpEchoGroupId);
        }

        // The embedded header group needs a quoted IPv4 header that is complete; the populator uses the same test.
        bool isErrorType = type == _TypeDestUnreach || type == _TypeRedirect
            || type == _TypeTimeExceeded || type == _TypeParamProblem;
        if (isErrorType && data.Length > _HeaderSize && _HasEmbeddedIpv4Header(span[_HeaderSize..]))
        {
            context.RecordGroupPresence(_IcmpResp_in_ipGroupId);
        }

        if (type == _TypeRedirect)
        {
            context.RecordGroupPresence(_IcmpRedirectGroupId);
        }

        if (_VerifyChecksum)
        {
            context.RecordGroupPresence(_IcmpChecksumStatusGroupId);
        }

        // The data field exists only for non-error types; the error types show their payload as the embedded header.
        if (!isErrorType && data.Length > _HeaderSize)
        {
            context.RecordGroupPresence(_IcmpDataGroupId);
        }

        // Packet info (summary column text): the type display text is the whole ICMP summary.
        parentField.SetPacketInfo(new LazyString(
            DisplayTables.GetIcmpTypeDisplayText(type)));

        // Store entire ICMP message for lazy populator; the checksum (which covers the whole message) and the data
        // field are derived from it later. The representation is a constant text, so no string is formatted per
        // packet; it names the fixed header size, not the length of the stored message.
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString("8 bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, "Internet Control Message Protocol, ",
            DisplayTables.GetIcmpTypeDisplayText(type), _Populator);

        return data.Length;
    }
    #endregion
}
