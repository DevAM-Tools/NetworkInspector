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
/// ├── icmp.ident: 0x1234                     [optional, echo, timestamp and address mask messages]
/// ├── icmp.ident_le: 0x3412                  [optional, echo, timestamp and address mask messages]
/// ├── icmp.seq: 1                            [optional, echo, timestamp and address mask messages]
/// ├── icmp.seq_le: 256                       [optional, echo, timestamp and address mask messages]
/// ├── icmp.originate_timestamp: 3600000 ms   [optional, types 13 and 14, as far as the message holds them]
/// ├── icmp.receive_timestamp: 3600005 ms     [optional, types 13 and 14]
/// ├── icmp.transmit_timestamp: 3600006 ms    [optional, types 13 and 14]
/// ├── icmp.address_mask: 255.255.255.0       [optional, types 17 and 18]
/// ├── icmp.num_addrs: 2                      [optional, router advertisement type 9]
/// ├── icmp.addr_entry_size: 2                [optional, type 9]
/// ├── icmp.lifetime: 1800 s                  [optional, type 9]
/// ├── icmp.router_address: 10.0.0.1          [optional, type 9 with entry size 2, one per complete entry]
/// ├── icmp.pref_level: 0                     [optional, type 9 with entry size 2, one per complete entry]
/// ├── icmp.redir_gw: 10.0.0.1                [optional, redirect type 5]
/// ├── icmp.mtu: 1400                         [optional, destination unreachable code 4]
/// ├── icmp.pointer: 20                       [optional, parameter problem type 12]
/// ├── icmp.length: 5 (20 bytes)              [optional, types 3, 11, 12 when the sender sets the length byte]
/// ├── icmp.unused: 00 00 00 00               [optional, error types without any other second-word field]
/// ├── icmp.orig_ip: Internet Protocol        [optional, error types 3/4/5/11/12 quoting an IPv4 header]
/// │   ├── icmp.orig_ip.src: 192.168.1.1
/// │   ├── icmp.orig_ip.dst: 10.0.0.1
/// │   ├── icmp.orig_ip.proto: 17 (UDP)
/// │   ├── icmp.orig_ip.ttl: 64
/// │   ├── icmp.orig_ip.srcport: 53           [optional, quoted TCP or UDP]
/// │   ├── icmp.orig_ip.dstport: 40000        [optional, quoted TCP or UDP]
/// │   ├── icmp.orig_ip.icmp_type: 8          [optional, quoted echo]
/// │   ├── icmp.orig_ip.ident: 0x1234         [optional, quoted echo]
/// │   └── icmp.orig_ip.seq: 1                [optional, quoted echo]
/// └── icmp.data: (32 bytes)                  [optional, payload of informational messages, or quoted bytes that
///                                             do not start with an IPv4 header]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// <para><b>Why the quoted datagram is decoded by hand:</b> an error message quotes the start of the datagram that
/// caused it (an IPv4 header and the first eight bytes of its payload). Handing those bytes to the regular IPv4, TCP
/// and UDP parsers would make them look like live traffic: stream tracking, reassembly and sessions would be fed
/// packets that were never on the wire at that position. The stack has no "this is a quote" notion, so the parser
/// reads the few fields that matter (addresses, protocol, TTL, the two ports or the echo identifier and sequence)
/// itself, without state and without dispatch.</para>
/// <para><b>Truncated captures:</b> the checksum covers the whole message, so a message cut by the capture length cannot be
/// verified and would read <c>[Bad]</c> although the sender did nothing wrong. The populator therefore compares the
/// payload length the enclosing IPv4 header declares with the message it received (<see cref="EnclosingIpHeader"/>) and
/// reports <c>[Unverified]</c> for a cut message.</para>
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

    /// <summary>ICMP type: Router Advertisement (RFC 1256).</summary>
    private const byte _TypeRouterAdvertisement = 9;

    /// <summary>ICMP type: Timestamp Request (RFC 792).</summary>
    private const byte _TypeTimestampRequest = 13;

    /// <summary>ICMP type: Timestamp Reply (RFC 792).</summary>
    private const byte _TypeTimestampReply = 14;

    /// <summary>ICMP type: Address Mask Request (RFC 950).</summary>
    private const byte _TypeAddressMaskRequest = 17;

    /// <summary>ICMP type: Address Mask Reply (RFC 950).</summary>
    private const byte _TypeAddressMaskReply = 18;

    /// <summary>Entry size, in 32-bit words, of a Router Advertisement entry that RFC 1256 defines: an address and a preference.</summary>
    private const byte _RouterEntryWords = 2;

    /// <summary>Bytes of one Router Advertisement entry (<see cref="_RouterEntryWords"/> words).</summary>
    private const int _RouterEntrySize = 8;

    /// <summary>Bytes of one timestamp or of the address mask: one 32-bit value.</summary>
    private const int _WordSize = 4;

    /// <summary>Timestamps a Timestamp message carries: originate, receive and transmit.</summary>
    private const int _TimestampCount = 3;

    /// <summary>ICMP type: Destination Unreachable.</summary>
    private const byte _TypeDestUnreach = 3;

    /// <summary>ICMP type: Source Quench (deprecated, but its payload still quotes a datagram).</summary>
    private const byte _TypeSourceQuench = 4;

    /// <summary>ICMP type: Redirect.</summary>
    private const byte _TypeRedirect = 5;

    /// <summary>ICMP type: Time Exceeded.</summary>
    private const byte _TypeTimeExceeded = 11;

    /// <summary>ICMP type: Parameter Problem.</summary>
    private const byte _TypeParamProblem = 12;

    /// <summary>ICMP code of Destination Unreachable that carries the next-hop MTU: Fragmentation Needed (RFC 1191).</summary>
    private const byte _CodeFragNeeded = 4;

    /// <summary>IP protocol number of TCP.</summary>
    private const byte _ProtocolTcp = 6;

    /// <summary>IP protocol number of UDP.</summary>
    private const byte _ProtocolUdp = 17;

    /// <summary>IP protocol number of ICMP.</summary>
    private const byte _ProtocolIcmp = 1;

    /// <summary>Bytes of a quoted TCP or UDP header that hold both ports.</summary>
    private const int _QuotedPortsLength = 4;

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

    // Optional: echo request/reply fields. The identifier and the sequence number are exposed in both byte orders
    // because RFC 792 sends them in network order, but some implementations write them in host (little endian) order;
    // showing both lets the reader recognise either kind of sender without a decoder setting.
    [U64Field("icmp.ident", "Identifier (BE)", IndexGroup = "icmp.echo")]
    private FieldId _IdentFieldId;

    [U64Field("icmp.ident_le", "Identifier (LE)", IndexGroup = "icmp.echo")]
    private FieldId _IdentLeFieldId;

    [U64Field("icmp.seq", "Sequence Number (BE)", IndexGroup = "icmp.echo")]
    private FieldId _SeqFieldId;

    [U64Field("icmp.seq_le", "Sequence Number (LE)", IndexGroup = "icmp.echo")]
    private FieldId _SeqLeFieldId;

    // Optional: Router Advertisement (type 9, RFC 1256). The count, the entry size and the lifetime lie in the second word, so
    // they are always present; the entries are present as far as the message reaches and the entry size is the defined one.
    [U64Field("icmp.num_addrs", "Number of addresses", IndexGroup = "icmp.router_adv")]
    private FieldId _NumAddrsFieldId;

    [U64Field("icmp.addr_entry_size", "Address entry size", IndexGroup = "icmp.router_adv")]
    private FieldId _AddrEntrySizeFieldId;

    [U64Field("icmp.lifetime", "Lifetime", IndexGroup = "icmp.router_adv")]
    private FieldId _LifetimeFieldId;

    [IPv4Field("icmp.router_address", "Router address", IndexGroup = "icmp.router_adv.entries")]
    private FieldId _RouterAddressFieldId;

    [I64Field("icmp.pref_level", "Preference level", IndexGroup = "icmp.router_adv.entries")]
    private FieldId _PrefLevelFieldId;

    // Optional: Timestamp Request and Reply (types 13 and 14, RFC 792): milliseconds since midnight UTC.
    [U64Field("icmp.originate_timestamp", "Originate timestamp", IndexGroup = "icmp.timestamp")]
    private FieldId _OriginateTimestampFieldId;

    [U64Field("icmp.receive_timestamp", "Receive timestamp", IndexGroup = "icmp.timestamp")]
    private FieldId _ReceiveTimestampFieldId;

    [U64Field("icmp.transmit_timestamp", "Transmit timestamp", IndexGroup = "icmp.timestamp")]
    private FieldId _TransmitTimestampFieldId;

    // Optional: Address Mask Request and Reply (types 17 and 18, RFC 950).
    [IPv4Field("icmp.address_mask", "Address mask", IndexGroup = "icmp.mask")]
    private FieldId _AddressMaskFieldId;

    // Optional: payload data
    [BytesField("icmp.data", "Data", IndexGroup = "icmp.data")]
    private FieldId _DataFieldId;

    // Optional: redirect gateway address (type 5)
    [IPv4Field("icmp.redir_gw", "Gateway Address", IndexGroup = "icmp.redirect")]
    private FieldId _RedirectGwFieldId;

    // Optional: second-word fields of the error messages (bytes 4-7). Which of them exist depends on type, code and the
    // length byte; _SecondWordFields is the single decision shared by Parse and the populator.
    [U64Field("icmp.mtu", "MTU of next hop", IndexGroup = "icmp.mtu")]
    private FieldId _MtuFieldId;

    [U64Field("icmp.pointer", "Pointer", IndexGroup = "icmp.pointer")]
    private FieldId _PointerFieldId;

    // RFC 4884: length of the quoted original datagram in 32-bit words; 0 when the sender predates RFC 4884.
    [U64Field("icmp.length", "Length of original datagram", IndexGroup = "icmp.length")]
    private FieldId _OrigLengthFieldId;

    [BytesField("icmp.unused", "Unused", IndexGroup = "icmp.unused")]
    private FieldId _UnusedFieldId;

    // Optional: the quoted IPv4 header of an error message (types 3, 4, 5, 11, 12). The group names are kept apart
    // so the ports and the echo fields are indexed exactly when they are emitted.
    [NoneField("icmp.orig_ip", "Internet Protocol (original datagram)", IndexGroup = "icmp.orig_ip")]
    private FieldId _OrigIpFieldId;

    [IPv4Field("icmp.orig_ip.src", "Source Address", IndexGroup = "icmp.orig_ip")]
    private FieldId _OrigIpSrcFieldId;

    [IPv4Field("icmp.orig_ip.dst", "Destination Address", IndexGroup = "icmp.orig_ip")]
    private FieldId _OrigIpDstFieldId;

    [U64Field("icmp.orig_ip.proto", "Protocol", IndexGroup = "icmp.orig_ip")]
    private FieldId _OrigIpProtoFieldId;

    [U64Field("icmp.orig_ip.ttl", "Time to Live", IndexGroup = "icmp.orig_ip")]
    private FieldId _OrigIpTtlFieldId;

    [U64Field("icmp.orig_ip.srcport", "Source Port", IndexGroup = "icmp.orig_ip.ports")]
    private FieldId _OrigIpSrcPortFieldId;

    [U64Field("icmp.orig_ip.dstport", "Destination Port", IndexGroup = "icmp.orig_ip.ports")]
    private FieldId _OrigIpDstPortFieldId;

    [U64Field("icmp.orig_ip.icmp_type", "ICMP Type", IndexGroup = "icmp.orig_ip.echo")]
    private FieldId _OrigIpIcmpTypeFieldId;

    [U64Field("icmp.orig_ip.ident", "Identifier (BE)", IndexGroup = "icmp.orig_ip.echo")]
    private FieldId _OrigIpIdentFieldId;

    [U64Field("icmp.orig_ip.seq", "Sequence Number (BE)", IndexGroup = "icmp.orig_ip.echo")]
    private FieldId _OrigIpSeqFieldId;

    // Settings. Loaded once when the stack starts and constant while it runs; Parse and the lazy populator both read
    // it and must therefore agree (the index groups recorded in Parse mirror what the populator emits). The status field
    // is emitted whenever the setting is on, so its index group follows the setting alone.
    [BoolSetting("icmp.verify_checksum", "Verify Checksum", "icmp", Default = false,
        Description = "Verifies the ICMP checksum over the whole message and shows [Good] or [Bad]. A message that the capture "
            + "length cut short cannot be verified and shows [Unverified].")]
    private bool _VerifyChecksum;

    // Pre-allocated populator delegate: one instance for all packets, so Parse does not allocate a closure per packet.
    private LazyPopulator _Populator = null!;

    /// <summary>Id of the IPv4 container ("ip"); invalid when IPv4 is not part of the stack. Resolved once at start.</summary>
    private FieldId _IpContainerFieldId;

    /// <summary>Id of the IPv6 container ("ipv6"); invalid when IPv6 is not part of the stack. Resolved once at start.</summary>
    private FieldId _Ipv6ContainerFieldId;

    /// <summary>
    /// Creates the shared populator delegate and resolves the IP container ids that the truncation check walks to. Runs
    /// once, single-threaded, before any packet is parsed; the results are only read afterwards.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _Populator = _PopulateIcmpFields;
        _IpContainerFieldId = stack.GetFieldId("ip") ?? FieldId.Invalid;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? FieldId.Invalid;
    }

    #endregion

    #region Shared decisions (eager index and lazy fields)

    /// <summary>Which transport fields of the quoted datagram can be decoded.</summary>
    private enum QuotedTransport
    {
        /// <summary>Nothing beyond the IPv4 header is decoded.</summary>
        None,

        /// <summary>The first four bytes hold the TCP or UDP ports.</summary>
        Ports,

        /// <summary>The first eight bytes are an ICMP echo request or reply.</summary>
        Echo,
    }

    /// <summary>Second-word fields an error message carries; see <see cref="_SecondWordFields"/>.</summary>
    [Flags]
    private enum SecondWord
    {
        /// <summary>No second-word field.</summary>
        None = 0,

        /// <summary>The next-hop MTU (bytes 6-7).</summary>
        Mtu = 1,

        /// <summary>The pointer (byte 4).</summary>
        Pointer = 2,

        /// <summary>The RFC 4884 length (byte 5).</summary>
        Length = 4,

        /// <summary>The whole second word as an unused field.</summary>
        Unused = 8,
    }

    /// <summary>
    /// Tells whether a type is an error message that quotes the datagram which caused it (RFC 792): Destination
    /// Unreachable, Source Quench, Redirect, Time Exceeded and Parameter Problem. The single definition for Parse and
    /// the populator.
    /// </summary>
    /// <param name="type">The ICMP type byte.</param>
    private static bool _IsErrorType(byte type) =>
        type is _TypeDestUnreach or _TypeSourceQuench or _TypeRedirect or _TypeTimeExceeded or _TypeParamProblem;

    /// <summary>
    /// Tells whether the second word of a type is an identifier and a sequence number (bytes 4-7): Echo, Timestamp and Address
    /// Mask, requests and replies. The single definition for Parse and the populator.
    /// </summary>
    /// <param name="type">The ICMP type byte.</param>
    private static bool _HasIdentifierAndSequence(byte type) =>
        type is _TypeEchoRequest or _TypeEchoReply or _TypeTimestampRequest or _TypeTimestampReply
            or _TypeAddressMaskRequest or _TypeAddressMaskReply;

    /// <summary>
    /// Counts the timestamps of a Timestamp message that the received bytes hold completely. A message the capture cut short
    /// shows the timestamps up to the last complete one; the rest is shown as data.
    /// </summary>
    /// <param name="type">The ICMP type byte.</param>
    /// <param name="messageLength">The received length of the message.</param>
    /// <returns>0 to 3; 0 for every other type.</returns>
    private static int _CompleteTimestamps(byte type, int messageLength) =>
        (type is _TypeTimestampRequest or _TypeTimestampReply)
            ? Math.Min(_TimestampCount, (messageLength - _HeaderSize) / _WordSize)
            : 0;

    /// <summary>Tells whether an Address Mask message holds its mask completely.</summary>
    /// <param name="type">The ICMP type byte.</param>
    /// <param name="messageLength">The received length of the message.</param>
    private static bool _HasAddressMask(byte type, int messageLength) =>
        type is _TypeAddressMaskRequest or _TypeAddressMaskReply
        && messageLength >= _HeaderSize + _WordSize;

    /// <summary>
    /// Counts the entries of a Router Advertisement that the received bytes hold completely. Only the entry size that RFC 1256
    /// defines (two words) is decoded; another size leaves the entries as data, because guessing at a layout would show
    /// values that are not there. The count is bounded by the received length, never by the count field alone, so a hostile
    /// count cannot make the parser read beyond the message or emit more fields than bytes.
    /// </summary>
    /// <param name="type">The ICMP type byte.</param>
    /// <param name="span">The received message; at least the 8-byte header.</param>
    /// <returns>0 or more; 0 for every other type.</returns>
    private static int _CompleteRouterEntries(byte type, ReadOnlySpan<byte> span) =>
        type == _TypeRouterAdvertisement && span[5] == _RouterEntryWords
            ? Math.Min(span[4], (span.Length - _HeaderSize) / _RouterEntrySize)
            : 0;

    /// <summary>
    /// The offset behind the last field that is decoded for a message that is not an error message: the header for every
    /// type without a body layout, otherwise the end of the last complete timestamp, mask or router entry. What lies behind
    /// is shown as <c>icmp.data</c>, so no byte of the message is missing from the tree. The single decision for the index
    /// group of the data field and for the populator.
    /// </summary>
    /// <param name="type">The ICMP type byte.</param>
    /// <param name="span">The received message; at least the 8-byte header.</param>
    private static int _BodyStart(byte type, ReadOnlySpan<byte> span)
    {
        int timestamps = _CompleteTimestamps(type, span.Length);
        if (timestamps > 0)
        {
            return _HeaderSize + (timestamps * _WordSize);
        }

        if (_HasAddressMask(type, span.Length))
        {
            return _HeaderSize + _WordSize;
        }

        return _HeaderSize + (_CompleteRouterEntries(type, span) * _RouterEntrySize);
    }

    /// <summary>
    /// Decides which fields of the second header word (bytes 4-7) an error message carries (RFC 792, RFC 1191,
    /// RFC 4884). The single place for the decision: the eager index groups and the lazy populator both call it, so a
    /// recorded group always has a field and an emitted field always has its group.
    /// </summary>
    /// <param name="type">The ICMP type byte.</param>
    /// <param name="code">The ICMP code byte.</param>
    /// <param name="lengthByte">Byte 5 of the message: the RFC 4884 length, 0 when the sender does not set it.</param>
    private static SecondWord _SecondWordFields(byte type, byte code, byte lengthByte)
    {
        // Redirect uses the second word for the gateway address and has its own field; non-error types have none.
        SecondWord fields = SecondWord.None;
        if (type == _TypeDestUnreach && code == _CodeFragNeeded)
        {
            fields = SecondWord.Mtu;
        }
        else if (type == _TypeParamProblem)
        {
            fields = SecondWord.Pointer;
        }

        // The RFC 4884 length byte exists in the three types that were extended; zero means "not used".
        if (type is _TypeDestUnreach or _TypeTimeExceeded or _TypeParamProblem && lengthByte != 0)
        {
            fields |= SecondWord.Length;
        }

        // A message that decodes none of its second word shows the four bytes as an unused field, so no byte of the
        // header is silently missing from the tree. Redirect shows the gateway instead.
        if (fields == SecondWord.None && _IsErrorType(type) && type != _TypeRedirect)
        {
            fields = SecondWord.Unused;
        }

        return fields;
    }

    /// <summary>
    /// Tells whether the bytes quoted in an ICMP error message start with a complete IPv4 header. The single place for
    /// this decision: the eager index group and the lazy populator both call it, so they cannot drift apart.
    /// </summary>
    /// <param name="embeddedData">Bytes after the 8-byte ICMP header.</param>
    private static bool _HasEmbeddedIpv4Header(ReadOnlySpan<byte> embeddedData)
    {
        // The minimum IPv4 header is 20 bytes. The address, protocol and TTL fields lie inside these 20 bytes, so
        // the IHL and any IPv4 options are irrelevant for them. The version nibble must be 4.
        return embeddedData.Length >= _MinEmbeddedIpHeaderSize
            && (embeddedData[0] >> 4) == 4;
    }

    /// <summary>
    /// Decides which transport fields of the quoted datagram can be read. The single place for the rule: Parse records
    /// the index groups from it and the populator emits exactly what it announces.
    /// </summary>
    /// <param name="embeddedData">Bytes after the 8-byte ICMP header; must satisfy <see cref="_HasEmbeddedIpv4Header"/>.</param>
    private static QuotedTransport _QuotedTransportKind(ReadOnlySpan<byte> embeddedData)
    {
        // The transport header starts after the quoted IPv4 header, whose length the IHL nibble gives (in 32-bit
        // words). A value below 5 is malformed and leaves the position of the transport header unknown.
        int headerLength = (embeddedData[0] & 0x0F) * 4;
        if (headerLength < _MinEmbeddedIpHeaderSize)
        {
            return QuotedTransport.None;
        }

        // A quoted fragment other than the first one does not start with a transport header, so its first bytes are
        // payload and must not be shown as ports.
        ushort flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(embeddedData[6..8]);
        if ((flagsAndOffset & 0x1FFF) != 0)
        {
            return QuotedTransport.None;
        }

        // RFC 792 quotes the header plus 64 bits of the original payload. The ports need 4 of them, the echo
        // identifier and sequence need all 8. Anything shorter is simply not decoded: the quote is informational.
        ReadOnlySpan<byte> transport = embeddedData[headerLength..];
        return embeddedData[9] switch
        {
            _ProtocolTcp or _ProtocolUdp when transport.Length >= _QuotedPortsLength => QuotedTransport.Ports,
            _ProtocolIcmp when transport.Length >= _HeaderSize && transport[0] is _TypeEchoRequest or _TypeEchoReply => QuotedTransport.Echo,
            _ => QuotedTransport.None,
        };
    }

    /// <summary>
    /// Tells whether the enclosing IPv4 header declares more payload than this message holds, i.e. the capture cut it.
    /// An enclosing header of another family or none at all is "not known to be cut".
    /// </summary>
    private bool _IsCutByTheCapture(in MutField container, int messageLength) =>
        container.HasFieldTree
        && EnclosingIpHeader.TryFindPrevious(in container, _IpContainerFieldId, _Ipv6ContainerFieldId, out EnclosingIpHeader ip)
        && !ip.IsIPv6
        && ip.IsTruncated(messageLength);

    #endregion

    #region Lazy populator

    /// <summary>
    /// Populates ICMP child fields lazily from stored datagram bytes.
    /// <para>
    /// Thread safety: reads only the immutable container bytes and start-time state, so it may run concurrently for
    /// different packets. The emission conditions are mirrored by the index groups recorded in <c>Parse</c> through
    /// the shared decision functions above; keep both in step by changing the functions, not the call sites.
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

        // Type, code and checksum texts come from precomputed tables. They are wrapped in LazyString on purpose: a bare
        // string argument would bind to the generic AppendWithCustomText<T0> overload, which boxes the string into a
        // lazy-format object per field and copies it again on first read. The LazyString struct only holds the
        // reference to the shared table string.
        container.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(type), new LazyString(DisplayTables.GetIcmpTypeDisplayText(type)));
        container.AppendWithCustomText(_CodeFieldId, FieldValue.NewU64(code), new LazyString(DisplayTables.GetIcmpCodeDisplayText(type, code)));
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), new LazyString(DisplayTables.FormatHexU16(checksum)));

        // Checksum validation
        if (_VerifyChecksum)
        {
            // ICMP checksum covers the entire ICMP message (header + data), unlike ICMPv6 there is no pseudo header.
            // Summing the message including the transmitted checksum yields 0 for a valid message (RFC 1071).
            // A message that the capture length cut cannot be verified (the missing bytes are part of the sum), so it is
            // "unverified" instead of "bad". The check needs the field tree to find the enclosing IPv4 header; without a
            // tree nothing is emitted anyway.
            string statusText;
            if (_IsCutByTheCapture(in container, icmpData.Length))
            {
                statusText = "[Unverified]";
            }
            else if (InternetChecksum.Compute(icmpData.Span) == 0)
            {
                statusText = "[Good]";
            }
            else
            {
                statusText = "[Bad]";
            }

            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        // Echo, Timestamp and Address Mask: identifier and sequence number in both byte orders (see the field declarations).
        if (_HasIdentifierAndSequence(type))
        {
            _AppendIdentifierAndSequence(in container, span);
        }

        // The fixed layouts behind the second word of the types without a quote. Each one stops at the last complete field.
        _AppendBody(in container, type, span);

        // Redirect (type 5): gateway IP address at bytes 4-7
        if (type == _TypeRedirect)
        {
            IPv4Address gateway = new(BinaryPrimitives.ReadUInt32BigEndian(span[4..8]));
            container.Append(_RedirectGwFieldId, FieldValue.NewIPv4(gateway));
        }

        // Second word of the error messages: pointer, RFC 4884 length, next-hop MTU, or the four unused bytes. The
        // order follows the wire: byte 4 (pointer), byte 5 (length), bytes 6-7 (MTU).
        SecondWord second = _SecondWordFields(type, code, span[5]);
        if ((second & SecondWord.Pointer) != 0)
        {
            container.Append(_PointerFieldId, FieldValue.NewU64(span[4]));
        }
        if ((second & SecondWord.Length) != 0)
        {
            // The byte counts 32-bit words; the text shows bytes, which is what a reader compares with the quote.
            container.AppendWithCustomText(_OrigLengthFieldId, FieldValue.NewU64(span[5]), span[5] * 4, " bytes");
        }
        if ((second & SecondWord.Mtu) != 0)
        {
            container.Append(_MtuFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(span[6..8])));
        }
        if ((second & SecondWord.Unused) != 0)
        {
            container.Append(_UnusedFieldId, FieldValue.NewBytes(icmpData[4.._HeaderSize]));
        }

        if (icmpData.Length > _HeaderSize)
        {
            if (_IsErrorType(type))
            {
                // The quote is shown as the original IP header when it starts with one; otherwise the bytes are not
                // dropped but shown as data, so an unusual or truncated quote stays inspectable.
                if (_HasEmbeddedIpv4Header(span[_HeaderSize..]))
                {
                    _AppendEmbeddedIpHeader(in container, span[_HeaderSize..]);
                }
                else
                {
                    container.Append(_DataFieldId, FieldValue.NewBytes(icmpData[_HeaderSize..]));
                }
            }
            else
            {
                // Payload data (the bytes behind the fields decoded above, for non-error types); the same offset as the index group.
                int bodyStart = _BodyStart(type, span);
                if (icmpData.Length > bodyStart)
                {
                    container.Append(_DataFieldId, FieldValue.NewBytes(icmpData[bodyStart..]));
                }
            }
        }

        return 0;
    }

    /// <summary>Appends the identifier and the sequence number (bytes 4-7), each in both byte orders.</summary>
    /// <param name="container">The ICMP container.</param>
    /// <param name="span">The message; at least the 8-byte header.</param>
    private void _AppendIdentifierAndSequence(in MutField container, ReadOnlySpan<byte> span)
    {
        ushort ident = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
        container.AppendWithCustomText(_IdentFieldId, FieldValue.NewU64(ident), new LazyString(DisplayTables.FormatHexU16(ident)));
        ushort identLe = BinaryPrimitives.ReadUInt16LittleEndian(span[4..6]);
        container.AppendWithCustomText(_IdentLeFieldId, FieldValue.NewU64(identLe), new LazyString(DisplayTables.FormatHexU16(identLe)));
        container.Append(_SeqFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(span[6..8])));
        container.Append(_SeqLeFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16LittleEndian(span[6..8])));
    }

    /// <summary>
    /// Appends the fields of the fixed layouts that follow the header: the Router Advertisement (RFC 1256), the three
    /// timestamps of a Timestamp message (RFC 792) and the mask of an Address Mask message (RFC 950). Every read is bounded by
    /// the received length through the shared counting functions, so a truncated or hostile message ends at its last complete
    /// field and never reads beyond the bytes.
    /// </summary>
    /// <param name="container">The ICMP container.</param>
    /// <param name="type">The ICMP type byte.</param>
    /// <param name="span">The message; at least the 8-byte header.</param>
    private void _AppendBody(in MutField container, byte type, ReadOnlySpan<byte> span)
    {
        if (type == _TypeRouterAdvertisement)
        {
            // Count, entry size (in 32-bit words) and lifetime lie in the second word; the entries follow as address and preference.
            container.Append(_NumAddrsFieldId, FieldValue.NewU64(span[4]));
            container.Append(_AddrEntrySizeFieldId, FieldValue.NewU64(span[5]));
            ushort lifetime = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
            container.AppendWithCustomText(_LifetimeFieldId, FieldValue.NewU64(lifetime), lifetime, " s");
            int entries = _CompleteRouterEntries(type, span);
            for (int i = 0; i < entries; i++)
            {
                int offset = _HeaderSize + (i * _RouterEntrySize);
                IPv4Address router = new(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, _WordSize)));
                int preference = BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset + _WordSize, _WordSize));
                container.Append(_RouterAddressFieldId, FieldValue.NewIPv4(router));
                container.Append(_PrefLevelFieldId, FieldValue.NewI64(preference));
            }

            return;
        }

        // Milliseconds since midnight UTC (RFC 792), as far as the message holds them.
        int timestamps = _CompleteTimestamps(type, span.Length);
        for (int i = 0; i < timestamps; i++)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(_HeaderSize + (i * _WordSize), _WordSize));
            FieldId field = i switch
            {
                0 => _OriginateTimestampFieldId,
                1 => _ReceiveTimestampFieldId,
                _ => _TransmitTimestampFieldId,
            };
            container.AppendWithCustomText(field, FieldValue.NewU64(value), value, " ms");
        }

        if (_HasAddressMask(type, span.Length))
        {
            IPv4Address mask = new(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(_HeaderSize, _WordSize)));
            container.Append(_AddressMaskFieldId, FieldValue.NewIPv4(mask));
        }
    }

    /// <summary>
    /// Appends the quoted IPv4 header of an ICMP error message and, when they are present, the first transport
    /// fields behind it. The caller has checked <see cref="_HasEmbeddedIpv4Header"/>.
    /// <para>
    /// A quote that does not start with an IPv4 header is not handled here: the caller shows it as <c>icmp.data</c>.
    /// The quoted bytes come from the remote sender and are only informational, so nothing in them is trusted for
    /// control flow: every read is bounds-checked against the received length before it happens.
    /// </para>
    /// </summary>
    /// <param name="container">ICMP container that receives the original-datagram container.</param>
    /// <param name="embeddedData">Bytes after the 8-byte ICMP header.</param>
    private void _AppendEmbeddedIpHeader(in MutField container, ReadOnlySpan<byte> embeddedData)
    {
        // IPv4 header offsets: TTL at 8, protocol at 9, source at 12..15, destination at 16..19.
        byte ttl = embeddedData[8];
        byte protocol = embeddedData[9];
        IPv4Address srcAddr = new(BinaryPrimitives.ReadUInt32BigEndian(embeddedData[12..16]));
        IPv4Address dstAddr = new(BinaryPrimitives.ReadUInt32BigEndian(embeddedData[16..20]));

        MutField ipContainer = container.AppendWithCustomText(
            _OrigIpFieldId, FieldValue.None,
            "Internet Protocol, Src: ", srcAddr, ", Dst: ", dstAddr);

        ipContainer.Append(_OrigIpSrcFieldId, FieldValue.NewIPv4(srcAddr));
        ipContainer.Append(_OrigIpDstFieldId, FieldValue.NewIPv4(dstAddr));
        ipContainer.AppendWithCustomText(_OrigIpProtoFieldId, FieldValue.NewU64(protocol), new LazyString(DisplayTables.GetIpProtocolDisplayText(protocol)));
        ipContainer.Append(_OrigIpTtlFieldId, FieldValue.NewU64(ttl));

        // The transport header starts after the quoted IP header; its offset moves with IPv4 options (IHL > 5).
        // The rule that decides whether anything can be read is the shared _QuotedTransportKind.
        QuotedTransport transportKind = _QuotedTransportKind(embeddedData);
        if (transportKind == QuotedTransport.None)
        {
            return;
        }

        ReadOnlySpan<byte> transport = embeddedData[((embeddedData[0] & 0x0F) * 4)..];
        if (transportKind == QuotedTransport.Ports)
        {
            ipContainer.Append(_OrigIpSrcPortFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(transport[..2])));
            ipContainer.Append(_OrigIpDstPortFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(transport[2..4])));
        }
        else
        {
            // A traceroute or a failed ping quotes the echo request: its identifier and sequence number match the
            // error to the probe that caused it.
            ipContainer.AppendWithCustomText(
                _OrigIpIcmpTypeFieldId, FieldValue.NewU64(transport[0]), new LazyString(DisplayTables.GetIcmpTypeDisplayText(transport[0])));
            ipContainer.Append(_OrigIpIdentFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(transport[4..6])));
            ipContainer.Append(_OrigIpSeqFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(transport[6..8])));
        }
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses an ICMP message from the supplied <paramref name="data"/> buffer. Only the type, code and the echo
    /// identifier are read eagerly (for index groups and the packet info text); all other fields are created by the
    /// lazy populator when the container's children are first accessed. ICMP has no payload protocol, so nothing is
    /// dispatched.
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
        byte code = span[1];

        // Record optional index groups. The index must be complete without materializing the lazy fields, so the
        // decision is made here from the header bytes and the length. Every condition comes from the same functions
        // the populator uses (a recorded group without an emitted field would make a filter on that group report a
        // packet that shows nothing for it).
        bool isEcho = type == _TypeEchoRequest || type == _TypeEchoReply;
        if (_HasIdentifierAndSequence(type))
        {
            context.RecordGroupPresence(_IcmpEchoGroupId);
        }

        if (type == _TypeRouterAdvertisement)
        {
            context.RecordGroupPresence(_IcmpRouter_advGroupId);
            if (_CompleteRouterEntries(type, span) > 0)
            {
                context.RecordGroupPresence(_IcmpRouter_advEntriesGroupId);
            }
        }

        if (_CompleteTimestamps(type, data.Length) > 0)
        {
            context.RecordGroupPresence(_IcmpTimestampGroupId);
        }

        if (_HasAddressMask(type, data.Length))
        {
            context.RecordGroupPresence(_IcmpMaskGroupId);
        }

        bool isErrorType = _IsErrorType(type);
        bool hasQuote = isErrorType && data.Length > _HeaderSize && _HasEmbeddedIpv4Header(span[_HeaderSize..]);
        if (hasQuote)
        {
            context.RecordGroupPresence(_IcmpOrig_ipGroupId);
            switch (_QuotedTransportKind(span[_HeaderSize..]))
            {
                case QuotedTransport.Ports:
                    context.RecordGroupPresence(_IcmpOrig_ipPortsGroupId);
                    break;
                case QuotedTransport.Echo:
                    context.RecordGroupPresence(_IcmpOrig_ipEchoGroupId);
                    break;
            }
        }

        if (type == _TypeRedirect)
        {
            context.RecordGroupPresence(_IcmpRedirectGroupId);
        }

        SecondWord second = _SecondWordFields(type, code, span[5]);
        if ((second & SecondWord.Mtu) != 0)
        {
            context.RecordGroupPresence(_IcmpMtuGroupId);
        }
        if ((second & SecondWord.Pointer) != 0)
        {
            context.RecordGroupPresence(_IcmpPointerGroupId);
        }
        if ((second & SecondWord.Length) != 0)
        {
            context.RecordGroupPresence(_IcmpLengthGroupId);
        }
        if ((second & SecondWord.Unused) != 0)
        {
            context.RecordGroupPresence(_IcmpUnusedGroupId);
        }

        if (_VerifyChecksum)
        {
            context.RecordGroupPresence(_IcmpChecksumStatusGroupId);
        }

        // The data field holds the payload of informational messages and the quoted bytes of an error message that
        // does not start with an IPv4 header (so they are never lost); an error message with a usable quote shows it
        // as the original-datagram container instead.
        if (data.Length > _BodyStart(type, span) && !hasQuote)
        {
            context.RecordGroupPresence(_IcmpDataGroupId);
        }

        _SetPacketInfo(in parentField, type, code, isEcho, isErrorType, span);

        // Store entire ICMP message for lazy populator; the checksum (which covers the whole message) and the data
        // field are derived from it later. The representation names the stored length from a shared table, so no
        // string is formatted per packet for messages up to 2 KiB; the title depends on the type byte only and is
        // also a shared string, so the eager path boxes nothing for it.
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString(DisplayTables.GetByteCountDisplayText(data.Length)));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, new LazyString(DisplayTables.GetIcmpContainerTitle(type)), _Populator);

        return data.Length;
    }

    /// <summary>
    /// Sets the packet-info (summary column) text. Echo shows the identifier and both byte orders of the sequence
    /// number, which is how request and reply are matched by eye; an error message shows the code name, which is the
    /// diagnosis; everything else shows the type name only.
    /// </summary>
    /// <remarks>
    /// The text is built lazily from a small value state, so the eager parse pays one box for echo and error
    /// messages and nothing for the other types. The time to live of the enclosing IP header is not part of the
    /// text: the IP parser stores it in its lazy fields, and materialising them here would defeat lazy parsing.
    /// </remarks>
    private static void _SetPacketInfo(
        in MutField parentField, byte type, byte code, bool isEcho, bool isErrorType, ReadOnlySpan<byte> span)
    {
        if (isEcho)
        {
            ushort ident = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
            ushort seq = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
            parentField.SetPacketInfo(LazyString.FormatLazy(
                (type, ident, seq),
                static s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DisplayTables.GetIcmpTypeDisplayText(s.type)} id=0x{s.ident:x4}, seq={s.seq}/{BinaryPrimitives.ReverseEndianness(s.seq)}")));
        }
        else if (isErrorType)
        {
            parentField.SetPacketInfo(LazyString.FormatLazy(
                (type, code),
                static s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DisplayTables.GetIcmpTypeDisplayText(s.type)}: {DisplayTables.GetIcmpCodeDisplayText(s.type, s.code)}")));
        }
        else
        {
            parentField.SetPacketInfo(new LazyString(DisplayTables.GetIcmpTypeDisplayText(type)));
        }
    }

    #endregion
}
