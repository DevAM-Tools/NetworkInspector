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
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
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

    // Settings
    [BoolSetting("icmp.verify_checksum", "Verify Checksum", "icmp", Default = false)]
    private bool _VerifyChecksum;

    // Pre-allocated populator delegate
    private LazyPopulator _Populator = null!;

    partial void _OnStartCustom(Stack stack) =>
        _Populator = _PopulateIcmpFields;

    /// <summary>
    /// Populates ICMP child fields lazily from stored datagram bytes.
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
            // ICMP checksum covers the entire ICMP message (header + data)
            ushort computed = InternetChecksum.Compute(icmpData.Span);
            bool valid = computed == 0;
            string statusText = valid ? "[Good]" : "[Bad]";
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        // Echo Request/Reply: identifier and sequence number
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
    /// Parses and appends embedded IPv4 header fields from ICMP error messages.
    /// ICMP error types (3, 5, 11, 12) include the original IP header + 8 bytes of the
    /// original datagram payload after the ICMP header.
    /// </summary>
    private void _AppendEmbeddedIpHeader(in MutField container, ReadOnlySpan<byte> embeddedData)
    {
        // Minimum IPv4 header is 20 bytes
        const int MinIpHeaderSize = 20;
        if (embeddedData.Length < MinIpHeaderSize)
        {
            return;
        }

        // Verify it's actually IPv4 (version nibble = 4)
        byte versionIhl = embeddedData[0];
        if ((versionIhl >> 4) != 4)
        {
            return;
        }

        byte ttl = embeddedData[8];
        byte protocol = embeddedData[9];
        IPv4Address srcAddr = new(BinaryPrimitives.ReadUInt32BigEndian(embeddedData[12..16]));
        IPv4Address dstAddr = new(BinaryPrimitives.ReadUInt32BigEndian(embeddedData[16..20]));

        string protoText = DisplayTables.GetIpProtocolDisplayText(protocol);
        MutField ipContainer = container.AppendWithCustomText(
            _RespInIpFieldId, FieldValue.None,
            ZA.Lazy("Internet Protocol, Src: ", srcAddr, ", Dst: ", dstAddr));

        ipContainer.Append(_RespInIpSrcFieldId, FieldValue.NewIPv4(srcAddr));
        ipContainer.Append(_RespInIpDstFieldId, FieldValue.NewIPv4(dstAddr));
        ipContainer.AppendWithCustomText(_RespInIpProtoFieldId,
            FieldValue.NewU64(protocol), protoText);
        ipContainer.Append(_RespInIpTtlFieldId, FieldValue.NewU64(ttl));
    }

    /// <summary>
    /// Parses a Icmp protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
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
        byte code = span[1];

        // Record optional index groups
        bool isEcho = type == _TypeEchoRequest || type == _TypeEchoReply;
        if (isEcho)
        {
            context.RecordGroupPresence(_IcmpEchoGroupId);
        }

        bool isErrorType = type == _TypeDestUnreach || type == _TypeRedirect
            || type == _TypeTimeExceeded || type == _TypeParamProblem;
        if (isErrorType && data.Length > _HeaderSize)
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

        if (data.Length > _HeaderSize)
        {
            context.RecordGroupPresence(_IcmpDataGroupId);
        }

        // Summary text
        LazyString summary = ZA.Lazy(
            "Internet Control Message Protocol, ",
            DisplayTables.GetIcmpTypeDisplayText(type));

        // Packet info
        parentField.SetPacketInfo(new LazyString(
            DisplayTables.GetIcmpTypeDisplayText(type)));

        // Store entire ICMP message for lazy populator
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString("8 bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

        return data.Length;
    }
    #endregion
}
