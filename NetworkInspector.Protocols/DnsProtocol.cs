// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Domain Name System (RFC 1035) protocol parser with lazy field population.
/// Supports both UDP and TCP transport (registered at port 53 on both).
/// <para>Field tree structure:</para>
/// <code>
/// dns: Domain Name System (query), Transaction ID: 0x1234
/// ├── dns.id: 0x1234
/// ├── dns.flags: 0x0100
/// │   ├── dns.flags.response: false (Query)
/// │   ├── dns.flags.opcode: 0 (Standard query)
/// │   ├── dns.flags.authoritative: false
/// │   ├── dns.flags.truncated: false
/// │   ├── dns.flags.recdesired: true
/// │   ├── dns.flags.recavail: false
/// │   ├── dns.flags.z: false
/// │   ├── dns.flags.authenticated: false
/// │   ├── dns.flags.checkdisable: false
/// │   └── dns.flags.rcode: 0 (No error)
/// ├── dns.count.queries: 1
/// ├── dns.count.answers: 0
/// ├── dns.count.auth_rr: 0
/// ├── dns.count.add_rr: 0
/// ├── [Queries section]
/// │   └── dns.qry.name: www.example.com
/// │       ├── dns.qry.type: A (1)
/// │       └── dns.qry.class: IN (1)
/// └── [Answers section]                          [optional]
///     └── dns.resp.name: www.example.com
///         ├── dns.resp.type: A (1)
///         ├── dns.resp.class: IN (1)
///         ├── dns.resp.ttl: 300
///         ├── dns.resp.len: 4
///         └── dns.a: 93.184.216.34               [type-specific]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("dns", "Domain Name System", Description = "DNS (RFC 1035)")]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortKey)]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPortKey)]
public sealed partial class DnsProtocol : IProtocol
{
    #region Constants

    /// <summary>UDP port for DNS.</summary>
    public const ulong UdpPortKey = 53;

    /// <summary>TCP port for DNS.</summary>
    public const ulong TcpPortKey = 53;

    /// <summary>Index group for always-present DNS fields.</summary>
    private const string _DnsIndexGroup = "dns";

    // Record type numbers and fixed-size layouts below are IANA / RFC 4034 / RFC 6891 facts, hence constants.
    // Resource-record TYPE codes (every type _ParseRData decodes) and minimum RDLENGTHs for the index-group-bearing types.
    // The constants of the index-group-bearing types are shared between the eager index detector (_DetectRrGroups) and the lazy
    // populator (_ParseResourceRecords/_ParseRData) so that the emission guard for each group lives in
    // exactly one place — a change here updates both paths simultaneously and prevents the detector
    // from silently drifting away from what the populator actually emits.
    private const ushort _RrTypeA = 1;          // A (RFC 1035): IPv4 address.
    private const ushort _RrTypeNs = 2;         // NS (RFC 1035): name server.
    private const ushort _RrTypeCname = 5;      // CNAME (RFC 1035): canonical name.
    private const ushort _RrTypeSoa = 6;        // SOA (RFC 1035): start of authority.
    private const ushort _RrTypePtr = 12;       // PTR (RFC 1035): domain name pointer.
    private const ushort _RrTypeMx = 15;        // MX (RFC 1035): mail exchange.
    private const ushort _RrTypeTxt = 16;       // TXT (RFC 1035): text strings.
    private const ushort _RrTypeAaaa = 28;      // AAAA (RFC 3596): IPv6 address.
    private const ushort _RrTypeSrv = 33;       // SRV (RFC 2782): service location.
    private const ushort _RrTypeOpt = 41;       // EDNS0 OPT (RFC 6891) — always emits dns.opt fields.
    private const ushort _RrTypeDs = 43;        // DS (RFC 4034).
    private const ushort _RrTypeRrsig = 46;     // RRSIG (RFC 4034).
    private const ushort _RrTypeNsec = 47;      // NSEC (RFC 4034).
    private const ushort _RrTypeDnskey = 48;    // DNSKEY (RFC 4034).
    private const int _DsMinRdLength = 4;       // Key Tag(2) + Algorithm(1) + Digest Type(1).
    private const int _RrsigMinRdLength = 18;   // Fixed RRSIG header before signer's name.
    private const int _NsecMinRdLength = 1;     // At least one byte of next-domain/bitmap data.
    private const int _NsecMaxBitmapLength = 32; // Longest type bit map block (RFC 4034 §4.1.2): 32 bytes cover 256 types.
    private const int _DnskeyMinRdLength = 4;   // Flags(2) + Protocol(1) + Algorithm(1).

    #endregion

    #region Protocol container

    [BytesField("dns", "Domain Name System", IndexGroup = _DnsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Header fields (always present)

    [U64Field("dns.id", "Transaction ID", IndexGroup = _DnsIndexGroup)]
    private FieldId _IdFieldId;

    [U64Field("dns.flags", "Flags", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("dns.flags.response", "Response", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsResponseFieldId;

    [U64Field("dns.flags.opcode", "Opcode", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsOpcodeFieldId;

    [BoolField("dns.flags.authoritative", "Authoritative", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsAuthFieldId;

    [BoolField("dns.flags.truncated", "Truncated", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsTruncFieldId;

    [BoolField("dns.flags.recdesired", "Recursion desired", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsRdFieldId;

    [BoolField("dns.flags.recavail", "Recursion available", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsRaFieldId;

    [BoolField("dns.flags.z", "Z (reserved)", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsZFieldId;

    [BoolField("dns.flags.authenticated", "Answer authenticated", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsAdFieldId;

    [BoolField("dns.flags.checkdisable", "Non-authenticated data: Acceptable", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsCdFieldId;

    [U64Field("dns.flags.rcode", "Reply code", IndexGroup = _DnsIndexGroup)]
    private FieldId _FlagsRcodeFieldId;

    [U64Field("dns.count.queries", "Questions", IndexGroup = _DnsIndexGroup)]
    private FieldId _QdCountFieldId;

    [U64Field("dns.count.answers", "Answer RRs", IndexGroup = _DnsIndexGroup)]
    private FieldId _AnCountFieldId;

    [U64Field("dns.count.auth_rr", "Authority RRs", IndexGroup = _DnsIndexGroup)]
    private FieldId _NsCountFieldId;

    [U64Field("dns.count.add_rr", "Additional RRs", IndexGroup = _DnsIndexGroup)]
    private FieldId _ArCountFieldId;

    #endregion

    #region Query fields (conditional)

    [NoneField("dns.qry", "Queries", IndexGroup = "dns.qry")]
    private FieldId _QueriesContainerFieldId;

    [StringField("dns.qry.name", "Name", IndexGroup = "dns.qry")]
    private FieldId _QryNameFieldId;

    [U64Field("dns.qry.name.len", "Name Length", IndexGroup = "dns.qry")]
    private FieldId _QryNameLenFieldId;

    [U64Field("dns.qry.type", "Type", IndexGroup = "dns.qry")]
    private FieldId _QryTypeFieldId;

    [U64Field("dns.qry.class", "Class", IndexGroup = "dns.qry")]
    private FieldId _QryClassFieldId;

    #endregion

    #region Answer/Authority/Additional fields (conditional)

    [NoneField("dns.resp", "Answers", IndexGroup = "dns.ans")]
    private FieldId _AnswersContainerFieldId;

    [StringField("dns.resp.name", "Name", IndexGroup = "dns.ans")]
    private FieldId _RespNameFieldId;

    [U64Field("dns.resp.type", "Type", IndexGroup = "dns.ans")]
    private FieldId _RespTypeFieldId;

    [U64Field("dns.resp.class", "Class", IndexGroup = "dns.ans")]
    private FieldId _RespClassFieldId;

    [U64Field("dns.resp.ttl", "Time to live", IndexGroup = "dns.ans")]
    private FieldId _RespTtlFieldId;

    [U64Field("dns.resp.len", "Data length", IndexGroup = "dns.ans")]
    private FieldId _RespLenFieldId;

    [IPv4Field("dns.a", "Address", IndexGroup = "dns.ans")]
    private FieldId _AFieldId;

    [IPv6Field("dns.aaaa", "AAAA Address", IndexGroup = "dns.ans")]
    private FieldId _AAAAFieldId;

    [StringField("dns.cname", "CNAME", IndexGroup = "dns.ans")]
    private FieldId _CnameFieldId;

    [StringField("dns.ns", "Name Server", IndexGroup = "dns.ans")]
    private FieldId _NsFieldId;

    [StringField("dns.ptr.domain_name", "Domain Name", IndexGroup = "dns.ans")]
    private FieldId _PtrFieldId;

    [U64Field("dns.mx.preference", "Preference", IndexGroup = "dns.ans")]
    private FieldId _MxPreferenceFieldId;

    [StringField("dns.mx.mail_exchange", "Mail Exchange", IndexGroup = "dns.ans")]
    private FieldId _MxExchangeFieldId;

    [StringField("dns.txt", "TXT", IndexGroup = "dns.ans")]
    private FieldId _TxtFieldId;

    [StringField("dns.soa.mname", "Primary name server", IndexGroup = "dns.ans")]
    private FieldId _SoaMnameFieldId;

    [StringField("dns.soa.rname", "Responsible authority's mailbox", IndexGroup = "dns.ans")]
    private FieldId _SoaRnameFieldId;

    [U64Field("dns.soa.serial_number", "Serial Number", IndexGroup = "dns.ans")]
    private FieldId _SoaSerialFieldId;

    [U64Field("dns.soa.refresh_interval", "Refresh Interval", IndexGroup = "dns.ans")]
    private FieldId _SoaRefreshFieldId;

    [U64Field("dns.soa.retry_interval", "Retry Interval", IndexGroup = "dns.ans")]
    private FieldId _SoaRetryFieldId;

    [U64Field("dns.soa.expire_limit", "Expire limit", IndexGroup = "dns.ans")]
    private FieldId _SoaExpireFieldId;

    [U64Field("dns.soa.minimum_ttl", "Minimum TTL", IndexGroup = "dns.ans")]
    private FieldId _SoaMinTtlFieldId;

    [U64Field("dns.srv.priority", "Priority", IndexGroup = "dns.ans")]
    private FieldId _SrvPriorityFieldId;

    [U64Field("dns.srv.weight", "Weight", IndexGroup = "dns.ans")]
    private FieldId _SrvWeightFieldId;

    [U64Field("dns.srv.port", "Port", IndexGroup = "dns.ans")]
    private FieldId _SrvPortFieldId;

    [StringField("dns.srv.name", "Target", IndexGroup = "dns.ans")]
    private FieldId _SrvNameFieldId;

    #endregion

    #region EDNS0 OPT (type 41) fields (conditional)

    [U64Field("dns.opt.udp_payload_size", "UDP payload size", IndexGroup = "dns.opt")]
    private FieldId _OptUdpPayloadSizeFieldId;

    [U64Field("dns.opt.ext_rcode", "Higher bits in extended RCODE", IndexGroup = "dns.opt")]
    private FieldId _OptExtRcodeFieldId;

    [U64Field("dns.opt.version", "EDNS0 version", IndexGroup = "dns.opt")]
    private FieldId _OptVersionFieldId;

    [BoolField("dns.opt.do", "DNSSEC answer OK", IndexGroup = "dns.opt")]
    private FieldId _OptDoFieldId;

    [U64Field("dns.opt.z", "Reserved", IndexGroup = "dns.opt")]
    private FieldId _OptZFieldId;

    [U64Field("dns.opt.data_length", "Data length", IndexGroup = "dns.opt")]
    private FieldId _OptDataLengthFieldId;

    [NoneField("dns.opt.option", "Option", IndexGroup = "dns.opt.option")]
    private FieldId _OptOptionFieldId;

    [U64Field("dns.opt.option.code", "Option Code", IndexGroup = "dns.opt.option")]
    private FieldId _OptOptionCodeFieldId;

    [U64Field("dns.opt.option.length", "Option Length", IndexGroup = "dns.opt.option")]
    private FieldId _OptOptionLengthFieldId;

    [BytesField("dns.opt.option.data", "Option Data", IndexGroup = "dns.opt.option")]
    private FieldId _OptOptionDataFieldId;

    #endregion

    #region DNSSEC fields (conditional, S11)

    // DS (Delegation Signer, type 43)
    [U64Field("dns.ds.key_tag", "Key Tag", IndexGroup = "dns.ds")]
    private FieldId _DsKeyTagFieldId;

    [U64Field("dns.ds.algorithm", "Algorithm", IndexGroup = "dns.ds")]
    private FieldId _DsAlgorithmFieldId;

    [U64Field("dns.ds.digest_type", "Digest Type", IndexGroup = "dns.ds")]
    private FieldId _DsDigestTypeFieldId;

    [BytesField("dns.ds.digest", "Digest", IndexGroup = "dns.ds")]
    private FieldId _DsDigestFieldId;

    // RRSIG (DNSSEC Signature, type 46)
    [U64Field("dns.rrsig.type_covered", "Type Covered", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigTypeCoveredFieldId;

    [U64Field("dns.rrsig.algorithm", "Algorithm", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigAlgorithmFieldId;

    [U64Field("dns.rrsig.labels", "Labels", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigLabelsFieldId;

    [U64Field("dns.rrsig.original_ttl", "Original TTL", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigOrigTtlFieldId;

    [U64Field("dns.rrsig.expiration", "Signature Expiration", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigExpirationFieldId;

    [U64Field("dns.rrsig.inception", "Signature Inception", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigInceptionFieldId;

    [U64Field("dns.rrsig.key_tag", "Key Tag", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigKeyTagFieldId;

    [StringField("dns.rrsig.signers_name", "Signer's Name", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigSignersNameFieldId;

    [BytesField("dns.rrsig.signature", "Signature", IndexGroup = "dns.rrsig")]
    private FieldId _RrsigSignatureFieldId;

    // NSEC (Next Secure, type 47)
    [StringField("dns.nsec.next_domain_name", "Next Domain Name", IndexGroup = "dns.nsec")]
    private FieldId _NsecNextDomainFieldId;

    [StringField("dns.nsec.type_bitmap", "Type Bit Maps", IndexGroup = "dns.nsec")]
    private FieldId _NsecTypeBitmapFieldId;

    // DNSKEY (type 48)
    [U64Field("dns.dnskey.flags", "Flags", IndexGroup = "dns.dnskey")]
    private FieldId _DnskeyFlagsFieldId;

    [U64Field("dns.dnskey.protocol", "Protocol", IndexGroup = "dns.dnskey")]
    private FieldId _DnskeyProtocolFieldId;

    [U64Field("dns.dnskey.algorithm", "Algorithm", IndexGroup = "dns.dnskey")]
    private FieldId _DnskeyAlgorithmFieldId;

    [BytesField("dns.dnskey.public_key", "Public Key", IndexGroup = "dns.dnskey")]
    private FieldId _DnskeyPublicKeyFieldId;

    // Pre-allocated populator: converting the instance method group _PopulateDnsFields to a delegate allocates a new
    // delegate on every conversion, so it is created once at stack start and every packet's container reuses it.
    // The field is read-only after _OnStartCustom, so concurrent Parse calls share it safely.
    private LazyPopulator _Populator = null!; // null-forgiving: always assigned in _OnStartCustom, which runs before the first Parse

    /// <summary>Creates the shared lazy populator delegate once, after the stack is built.</summary>
    partial void _OnStartCustom(Stack stack) =>
        _Populator = _PopulateDnsFields;

    /// <summary>
    /// Parses a DNS message from the supplied <paramref name="data"/> buffer. Only the container, the packet info
    /// and the index presence are produced eagerly; the field tree below the container is built lazily by
    /// <see cref="_PopulateDnsFields"/> the first time its children are read. DNS is a leaf protocol: nothing is
    /// dispatched further.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes: a DNS message, optionally preceded by the 2-byte TCP length prefix.</param>
    /// <param name="context">Parse context; used to record protocol and index-group presence.</param>
    /// <returns>
    /// The whole <paramref name="data"/> length (including a stripped length prefix), or a <see cref="ParseError"/>
    /// when the message is shorter than the 12-byte header. Malformed sections after the header are not an error
    /// here; they only truncate what the lazy populator shows.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ReadOnlyMemory<byte> dnsData = data;

        // RFC 1035 §4.2.2: DNS over TCP uses a 2-byte big-endian length prefix
        // before each DNS message. Detect and strip it if present.
        // This parser is registered for port 53 on both UDP and TCP and is not told which transport delivered the
        // payload, so it uses a heuristic: the prefix is assumed when the first two bytes equal the number of bytes
        // that follow. Known limits: a UDP message whose transaction ID happens to equal (length - 2) is misread as
        // prefixed, and a TCP segment that holds a partial or several messages (prefix not matching the segment
        // size) is parsed without stripping.
        if (data.Length >= 2)
        {
            ReadOnlySpan<byte> span = data.Span;
            int tcpLength = (span[0] << 8) | span[1];
            if (tcpLength > 0 && tcpLength == data.Length - 2)
            {
                dnsData = data.Slice(2);
            }
        }

        if (dnsData.Length < DnsHeader.Size)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DnsHeader.Size, (ulong)dnsData.Length);
        }

        // Presence is recorded only once the 12-byte header is known to be present.
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DnsGroupId);

        // Cannot fail after the length check above; kept because TryParse is the single place that decodes the header.
        if (!DnsHeader.TryParse(dnsData.Span, out DnsHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DnsHeader.Size, (ulong)dnsData.Length);
        }

        ushort transactionId = header.TransactionId;
        bool isResponse = header.IsResponse;

        // Record conditional index groups. These two are decided from the header counts alone (cheap), so a truncated
        // message that declares records still marks them; the record-type dependent groups below are content based.
        if (header.QuestionCount > 0)
        {
            context.RecordGroupPresence(_DnsQryGroupId);
        }
        if (header.AnswerCount > 0 || header.AuthorityCount > 0 || header.AdditionalCount > 0)
        {
            context.RecordGroupPresence(_DnsAnsGroupId);
        }

        // Eagerly walk the question and resource-record sections to record exactly the RR-type
        // dependent index groups whose fields the lazy populator will emit (OPT/DS/RRSIG/NSEC/
        // DNSKEY). The decision depends on each record's TYPE and RDLENGTH and requires resolving
        // compressed names to advance through the message, so the walk reuses the same name reader
        // the populator uses — keeping the presence index content-consistent with materialization
        // and free of false positives, at the deliberate cost of repeating the record walk.
        _DetectRrGroups(
            dnsData.Span, in header,
            out bool hasOpt, out bool hasOptOption, out bool hasDs,
            out bool hasRrsig, out bool hasNsec, out bool hasDnskey);

        if (hasOpt)
        {
            context.RecordGroupPresence(_DnsOptGroupId);
        }
        if (hasOptOption)
        {
            context.RecordGroupPresence(_DnsOptOptionGroupId);
        }
        if (hasDs)
        {
            context.RecordGroupPresence(_DnsDsGroupId);
        }
        if (hasRrsig)
        {
            context.RecordGroupPresence(_DnsRrsigGroupId);
        }
        if (hasNsec)
        {
            context.RecordGroupPresence(_DnsNsecGroupId);
        }
        if (hasDnskey)
        {
            context.RecordGroupPresence(_DnsDnskeyGroupId);
        }

        // Build summary text (the transaction ID text comes from a precomputed table, so no formatting happens here).
        // The table text already starts with "0x"; adding the prefix again here would show "0x0x1234".
        string hexId = DisplayTables.FormatHexU16(transactionId);
        string dnsKind = isResponse
            ? "Domain Name System (response), Transaction ID: "
            : "Domain Name System (query), Transaction ID: ";

        // Set packet info for the info column
        parentField.SetPacketInfo(isResponse
            ? ZA.Lazy("DNS Response ", hexId)
            : ZA.Lazy("DNS Query ", hexId));

        // The container value is the DNS message without the TCP length prefix; the children are created by the shared
        // populator on first access, which keeps the per-packet cost of an unexpanded DNS packet at this one node.
        // The shared delegate carries no per-packet state: the populator re-reads everything from the container bytes.
        FieldValue containerValue = FieldValue.NewBytes(dnsData);
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, dnsKind, hexId, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Populates all DNS fields from the stored packet bytes.
    /// Called lazily on first access of the DNS container's children, possibly on another thread than the one
    /// that parsed the packet and after <see cref="Parse"/> has returned: it therefore uses only the container's own
    /// bytes and the read-only field IDs, never any state captured during <see cref="Parse"/>. A lazy populator also
    /// cannot touch the packet index, which is why the index groups are decided eagerly in <see cref="Parse"/>.
    /// The returned value is not a consumed-byte count (by convention 0 on success); errors become a child of the
    /// container.
    /// </summary>
    private ParseResult _PopulateDnsFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> dnsData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        ReadOnlySpan<byte> span = dnsData.Span;

        if (!DnsHeader.TryParse(span, out DnsHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DnsHeader.Size, (ulong)dnsData.Length);
        }

        // Transaction ID
        string hexId = DisplayTables.FormatHexU16(header.TransactionId);
        container.AppendWithCustomText(_IdFieldId, FieldValue.NewU64(header.TransactionId), hexId);

        // Flags container — display text shows hex value followed by active boolean flags in brackets.
        // Opcode and RCODE are multi-bit numeric fields rendered separately as sub-fields.
        MutField flagsField = container.AppendWithCustomText(_FlagsFieldId,
            FieldValue.NewU64(header.Flags),
            DisplayTables.FormatHexU16(header.Flags), " ", DnsFlagsFormatter.Format(header.Flags));

        // Individual flag sub-fields
        string responseText = header.IsResponse ? "Message is a response" : "Message is a query";
        flagsField.AppendWithCustomText(_FlagsResponseFieldId,
            FieldValue.NewBool(header.IsResponse), responseText);

        string opcodeText = DnsDisplayTables.GetOpcodeDisplayText(header.Opcode);
        flagsField.AppendWithCustomText(_FlagsOpcodeFieldId,
            FieldValue.NewU64(header.Opcode), opcodeText);

        flagsField.Append(_FlagsAuthFieldId, FieldValue.NewBool(header.IsAuthoritative));
        flagsField.Append(_FlagsTruncFieldId, FieldValue.NewBool(header.IsTruncated));
        flagsField.Append(_FlagsRdFieldId, FieldValue.NewBool(header.RecursionDesired));
        flagsField.Append(_FlagsRaFieldId, FieldValue.NewBool(header.RecursionAvailable));
        flagsField.Append(_FlagsZFieldId, FieldValue.NewBool(header.Z));
        flagsField.Append(_FlagsAdFieldId, FieldValue.NewBool(header.AuthenticatedData));
        flagsField.Append(_FlagsCdFieldId, FieldValue.NewBool(header.CheckingDisabled));

        string rcodeText = DnsDisplayTables.GetRcodeDisplayText(header.ResponseCode);
        flagsField.AppendWithCustomText(_FlagsRcodeFieldId,
            FieldValue.NewU64(header.ResponseCode), rcodeText);

        // Section counts
        container.Append(_QdCountFieldId, FieldValue.NewU64(header.QuestionCount));
        container.Append(_AnCountFieldId, FieldValue.NewU64(header.AnswerCount));
        container.Append(_NsCountFieldId, FieldValue.NewU64(header.AuthorityCount));
        container.Append(_ArCountFieldId, FieldValue.NewU64(header.AdditionalCount));

        // Parse sections: one running offset is shared by all four sections, because they follow each other in the
        // message and compressed names may point back anywhere in the whole message (hence the full span is passed).
        // The counts come from the wire (untrusted, up to 65535 per section) but each loop below stops at the first
        // record that does not fit, so the work is bounded by the message size, not by the declared counts.
        // Authority and additional records reuse the answer field IDs (dns.resp.*); the three sections differ only by
        // their container title.
        // The first question or record that does not fit ends the whole walk, as in _DetectRrGroups: once a record is
        // cut off, the offset points into bytes that its own (too large) RDLENGTH claims, and reading the next section
        // from there would show records the eager index groups never saw. The section containers of the declared
        // counts are still appended (they are what the header-count based index groups stand for); they stay empty.
        int offset = DnsHeader.Size;
        bool intact = true;

        // Questions section
        if (header.QuestionCount > 0)
        {
            MutField queriesContainer = container.AppendWithCustomText(
                _QueriesContainerFieldId, FieldValue.None,
                "Queries (", header.QuestionCount, ")");

            intact = _ParseQuestions(in queriesContainer, span, ref offset, header.QuestionCount);
        }

        // Answers section
        if (header.AnswerCount > 0)
        {
            MutField answersContainer = container.AppendWithCustomText(
                _AnswersContainerFieldId, FieldValue.None,
                "Answers (", header.AnswerCount, ")");

            if (intact)
            {
                intact = _ParseResourceRecords(in answersContainer, span, dnsData, ref offset, header.AnswerCount);
            }
        }

        // Authority section
        if (header.AuthorityCount > 0)
        {
            MutField authContainer = container.AppendWithCustomText(
                _AnswersContainerFieldId, FieldValue.None,
                "Authoritative nameservers (", header.AuthorityCount, ")");

            if (intact)
            {
                intact = _ParseResourceRecords(in authContainer, span, dnsData, ref offset, header.AuthorityCount);
            }
        }

        // Additional section
        if (header.AdditionalCount > 0)
        {
            MutField addContainer = container.AppendWithCustomText(
                _AnswersContainerFieldId, FieldValue.None,
                "Additional records (", header.AdditionalCount, ")");

            if (intact)
            {
                _ = _ParseResourceRecords(in addContainer, span, dnsData, ref offset, header.AdditionalCount);
            }
        }

        return 0;
    }

    /// <summary>
    /// Parses the question section of a DNS packet. Stops silently at the first question that does not fit in the
    /// message; questions before it are kept. A malformed name is shown as an empty string (see
    /// <see cref="DnsNameParser.ReadName"/>) and does not produce an error.
    /// </summary>
    /// <returns>True when all <paramref name="count"/> questions were read; false when a question did not fit.</returns>
    private bool _ParseQuestions(
        in MutField container, ReadOnlySpan<byte> data, ref int offset, ushort count)
    {
        for (int i = 0; i < count; i++)
        {
            if (offset >= data.Length)
            {
                return false;
            }

            string name = DnsNameParser.ReadName(data, ref offset);

            // After the name: QType (2 bytes) + QClass (2 bytes)
            if (offset + 4 > data.Length)
            {
                return false;
            }

            ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4;

            // Create question entry as child of the queries container
            string typeName = DnsDisplayTables.GetTypeName(qtype);
            string className = DnsDisplayTables.GetClassDisplayText(qclass);
            MutField qryField = container.AppendWithCustomText(
                _QryNameFieldId, FieldValue.NewString(name),
                name, ": type ", typeName, ", class ", className);

            // Length of the decoded dotted name in characters (without the trailing dot), not the wire length.
            qryField.Append(_QryNameLenFieldId, FieldValue.NewU64((ulong)name.Length));
            qryField.AppendWithCustomText(_QryTypeFieldId,
                FieldValue.NewU64(qtype), DnsDisplayTables.GetTypeDisplayText(qtype));
            qryField.AppendWithCustomText(_QryClassFieldId,
                FieldValue.NewU64(qclass), DnsDisplayTables.GetClassDisplayText(qclass));
        }

        return true;
    }

    /// <summary>
    /// Parses resource records (Answer, Authority, or Additional sections).
    /// Each resource record: Name + Type(2) + Class(2) + TTL(4) + RDLENGTH(2) + RDATA(N).
    /// Stops silently at the first record whose fixed part or RDATA does not fit in the message; records before it
    /// are kept. The caller must not read any further section after a <c>false</c> result (the offset then points into
    /// the cut-off record), which is also where <c>_DetectRrGroups</c> stops.
    /// </summary>
    /// <returns>True when all <paramref name="count"/> records were read; false when a record did not fit.</returns>
    private bool _ParseResourceRecords(
        in MutField container, ReadOnlySpan<byte> data, ReadOnlyMemory<byte> fullMemory,
        ref int offset, ushort count)
    {
        for (int i = 0; i < count; i++)
        {
            if (offset >= data.Length)
            {
                return false;
            }

            string name = DnsNameParser.ReadName(data, ref offset);

            // Type(2) + Class(2) + TTL(4) + RDLENGTH(2) = 10 bytes minimum
            if (offset + 10 > data.Length)
            {
                return false;
            }

            ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;

            if (offset + rdLength > data.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> rdata = data[offset..(offset + rdLength)];

            // OPT (type 41) uses CLASS/TTL fields with different semantics (RFC 6891); its owner name is always the
            // root, so the name read above is discarded and a fixed "<Root>" is shown instead.
            if (rrType == _RrTypeOpt)
            {
                _ParseOptRecord(in container, rrClass, ttl, rdata, fullMemory, offset);
                offset += rdLength;
                continue;
            }

            // Create RR entry as child of the section container
            string typeName = DnsDisplayTables.GetTypeName(rrType);
            MutField rrField = container.AppendWithCustomText(
                _RespNameFieldId, FieldValue.NewString(name),
                name, ": type ", typeName);

            rrField.AppendWithCustomText(_RespTypeFieldId,
                FieldValue.NewU64(rrType), DnsDisplayTables.GetTypeDisplayText(rrType));
            rrField.AppendWithCustomText(_RespClassFieldId,
                FieldValue.NewU64(rrClass), DnsDisplayTables.GetClassDisplayText(rrClass));
            rrField.Append(_RespTtlFieldId, FieldValue.NewU64(ttl));
            rrField.Append(_RespLenFieldId, FieldValue.NewU64(rdLength));

            // Parse type-specific RDATA. The whole message and the RDATA start offset are passed along because names
            // inside RDATA (CNAME, NS, MX, ...) may use compression pointers into any earlier part of the message.
            _ParseRData(in rrField, rrType, rdata, data, fullMemory, offset);

            offset += rdLength;
        }

        return true;
    }

    /// <summary>
    /// Eagerly walks the question and resource-record sections to decide which RR-type dependent
    /// index groups apply, mirroring <see cref="_ParseResourceRecords"/>'s offset advancement and
    /// per-type emission guards without building any field. The same compression-aware name reader
    /// is used so the offset progression is identical to the populator's, guaranteeing the recorded
    /// groups match the fields that will be emitted. This duplicates the record walk; that is the
    /// accepted cost of keeping the field tree lazy while the presence index stays content-consistent.
    /// The walk has to happen in <see cref="Parse"/> because a lazy populator is not allowed to touch the packet
    /// index. Rejected alternative: deriving the groups from the header counts alone, which would mark OPT/DNSSEC
    /// groups for records that are truncated or too short to emit those fields.
    /// <para>
    /// Work is bounded by the message size, not by the attacker-controlled section counts: every iteration either
    /// returns or consumes at least the 10-byte fixed part of a record. Unlike the populator it gives up at the first
    /// record that does not fit instead of continuing with the next section.
    /// </para>
    /// </summary>
    private static void _DetectRrGroups(
        ReadOnlySpan<byte> span, in DnsHeader header,
        out bool hasOpt, out bool hasOptOption, out bool hasDs,
        out bool hasRrsig, out bool hasNsec, out bool hasDnskey)
    {
        hasOpt = false;
        hasOptOption = false;
        hasDs = false;
        hasRrsig = false;
        hasNsec = false;
        hasDnskey = false;

        int offset = DnsHeader.Size;

        // Skip the question section exactly as _ParseQuestions advances the offset.
        for (int i = 0; i < header.QuestionCount; i++)
        {
            if (offset >= span.Length)
            {
                return;
            }
            DnsNameParser.SkipName(span, ref offset);
            if (offset + 4 > span.Length)
            {
                return;
            }
            offset += 4;
        }

        // The answer, authority and additional sections are parsed back-to-back with a shared
        // offset, so a single continuous walk over their combined count mirrors the populator.
        int totalRecords = header.AnswerCount + header.AuthorityCount + header.AdditionalCount;
        for (int i = 0; i < totalRecords; i++)
        {
            if (offset >= span.Length)
            {
                return;
            }
            DnsNameParser.SkipName(span, ref offset);
            if (offset + 10 > span.Length)
            {
                return;
            }

            ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 8)..]);
            offset += 10;
            if (offset + rdLength > span.Length)
            {
                return;
            }

            switch (rrType)
            {
                case _RrTypeOpt: // OPT (EDNS0) — _ParseOptRecord always emits the dns.opt fields.
                    hasOpt = true;
                    if (_OptHasOption(span.Slice(offset, rdLength)))
                    {
                        hasOptOption = true;
                    }
                    break;
                case _RrTypeDs when rdLength >= _DsMinRdLength:
                    hasDs = true;
                    break;
                case _RrTypeRrsig when rdLength >= _RrsigMinRdLength:
                    hasRrsig = true;
                    break;
                case _RrTypeNsec when rdLength >= _NsecMinRdLength:
                    hasNsec = true;
                    break;
                case _RrTypeDnskey when rdLength >= _DnskeyMinRdLength:
                    hasDnskey = true;
                    break;
            }

            offset += rdLength;
        }
    }

    /// <summary>
    /// Returns true when an OPT record's RDATA carries at least one EDNS0 option that the populator
    /// would emit, mirroring the first TLV guard in <see cref="_ParseOptRecord"/> (a 4-byte option
    /// header followed by its declared option-length within bounds).
    /// </summary>
    private static bool _OptHasOption(ReadOnlySpan<byte> rdata)
    {
        if (rdata.Length < 4)
        {
            return false;
        }

        ushort optionLength = BinaryPrimitives.ReadUInt16BigEndian(rdata[2..]);
        return 4 + optionLength <= rdata.Length;
    }

    /// <summary>
    /// Parses an EDNS0 OPT pseudo-RR (type 41, RFC 6891).
    /// CLASS = UDP payload size, TTL = extended RCODE + version + flags.
    /// RDATA contains a sequence of {option-code(2), option-length(2), option-data(N)} TLVs.
    /// </summary>
    private void _ParseOptRecord(
        in MutField container, ushort udpPayloadSize, uint ttlField,
        ReadOnlySpan<byte> rdata, ReadOnlyMemory<byte> fullMemory, int rdataOffset)
    {
        MutField optField = container.AppendWithCustomText(
            _RespNameFieldId, FieldValue.NewString("<Root>"),
            "<Root>: type OPT");

        optField.AppendWithCustomText(_RespTypeFieldId,
            FieldValue.NewU64(_RrTypeOpt), DnsDisplayTables.GetTypeDisplayText(_RrTypeOpt));

        // CLASS = UDP payload size (not a DNS class)
        optField.Append(_OptUdpPayloadSizeFieldId, FieldValue.NewU64(udpPayloadSize));

        // TTL bytes (RFC 6891 §6.1.3): [0]=extended RCODE (upper 8 bits of the 12-bit RCODE), [1]=version,
        // [2-3]=flags where bit 15 is DO (DNSSEC OK) and the remaining 15 bits are reserved (Z).
        byte extRcode = (byte)(ttlField >> 24);
        byte version = (byte)((ttlField >> 16) & 0xFF);
        ushort flags = (ushort)(ttlField & 0xFFFF);
        bool doBit = (flags & 0x8000) != 0;
        ushort zBits = (ushort)(flags & 0x7FFF);

        optField.Append(_OptExtRcodeFieldId, FieldValue.NewU64(extRcode));
        optField.Append(_OptVersionFieldId, FieldValue.NewU64(version));
        optField.AppendWithCustomText(_OptDoFieldId,
            FieldValue.NewBool(doBit), doBit ? "DNSSEC answer OK" : "Not set");
        optField.Append(_OptZFieldId, FieldValue.NewU64(zBits));

        optField.Append(_OptDataLengthFieldId, FieldValue.NewU64((ulong)rdata.Length));

        // Parse EDNS0 options as TLV entries. The loop must keep the same bounds rules as _OptHasOption, which the
        // eager index detector uses to decide whether dns.opt.option fields exist; an option whose declared length
        // runs past the RDATA ends the walk (earlier options are kept).
        int pos = 0;
        while (pos + 4 <= rdata.Length)
        {
            ushort optionCode = BinaryPrimitives.ReadUInt16BigEndian(rdata[pos..]);
            ushort optionLength = BinaryPrimitives.ReadUInt16BigEndian(rdata[(pos + 2)..]);
            pos += 4;

            if (pos + optionLength > rdata.Length)
            {
                break;
            }

            MutField optionField = optField.AppendWithCustomText(
                _OptOptionFieldId, FieldValue.None,
                "Option: ", DnsDisplayTables.GetEdnsOptionName(optionCode));

            optionField.Append(_OptOptionCodeFieldId, FieldValue.NewU64(optionCode));
            optionField.Append(_OptOptionLengthFieldId, FieldValue.NewU64(optionLength));

            if (optionLength > 0)
            {
                optionField.Append(_OptOptionDataFieldId,
                    FieldValue.NewBytes(fullMemory.Slice(rdataOffset + pos, optionLength)));
            }

            pos += optionLength;
        }
    }

    /// <summary>Parses type-specific RDATA for common DNS record types.</summary>
    private void _ParseRData(
        in MutField rrField, ushort rrType,
        ReadOnlySpan<byte> rdata, ReadOnlySpan<byte> fullPacket,
        ReadOnlyMemory<byte> fullMemory, int rdataOffset)
    {
        switch (rrType)
        {
            case _RrTypeA when rdata.Length >= 4: // A record
                IPv4Address ipv4 = DnsNameParser.ParseARecord(rdata);
                rrField.Append(_AFieldId, FieldValue.NewIPv4(ipv4));
                break;

            case _RrTypeAaaa when rdata.Length >= 16: // AAAA record
                IPv6Address ipv6 = DnsNameParser.ParseAAAARecord(rdata);
                rrField.Append(_AAAAFieldId, FieldValue.NewIPv6(ipv6));
                break;

            case _RrTypeCname: // CNAME
                {
                    int pos = rdataOffset;
                    string cname = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_CnameFieldId, FieldValue.NewString(cname));
                    break;
                }

            case _RrTypeNs: // NS
                {
                    int pos = rdataOffset;
                    string ns = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_NsFieldId, FieldValue.NewString(ns));
                    break;
                }

            case _RrTypePtr: // PTR
                {
                    int pos = rdataOffset;
                    string ptr = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_PtrFieldId, FieldValue.NewString(ptr));
                    break;
                }

            case _RrTypeMx when rdata.Length >= 2: // MX
                {
                    ushort preference = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    rrField.Append(_MxPreferenceFieldId, FieldValue.NewU64(preference));
                    int pos = rdataOffset + 2;
                    string exchange = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_MxExchangeFieldId, FieldValue.NewString(exchange));
                    break;
                }

            case _RrTypeTxt when rdata.Length >= 1: // TXT
                {
                    // TXT records contain one or more length-prefixed strings; they are shown joined by single spaces
                    // and each byte is mapped to the char with the same code (Latin-1), so no encoding is guessed.
                    // A string whose length byte runs past the RDATA ends the list (strings before it are kept); both
                    // passes below apply the same rule, which is what keeps the computed length exact.
                    // First pass: compute total output length to enable single-allocation string.Create.
                    int totalLen = 0;
                    int scan = 0;
                    while (scan < rdata.Length)
                    {
                        byte strLen = rdata[scan++];
                        if (scan + strLen > rdata.Length)
                        {
                            break;
                        }
                        if (totalLen > 0)
                        {
                            totalLen++; // space separator
                        }
                        totalLen += strLen;
                        scan += strLen;
                    }

                    // Second pass: fill the string directly via string.Create (no StringBuilder, exactly one string).
                    // A span cannot be stored in the string.Create state, so the state is a ReadOnlyMemory slice of the
                    // message that covers exactly the RDATA: it avoids copying the RDATA into a new array.
                    ReadOnlyMemory<byte> rdataMemory = fullMemory.Slice(rdataOffset, rdata.Length);
                    string txtResult = string.Create(totalLen, rdataMemory,
                        static (chars, state) =>
                        {
                            ReadOnlySpan<byte> src = state.Span;
                            int pos = 0;
                            int written = 0;
                            while (pos < src.Length)
                            {
                                byte strLen = src[pos++];
                                if (pos + strLen > src.Length)
                                {
                                    break;
                                }
                                if (written > 0)
                                {
                                    chars[written++] = ' ';
                                }
                                for (int j = 0; j < strLen; j++)
                                {
                                    chars[written++] = (char)src[pos + j];
                                }
                                pos += strLen;
                            }
                        });
                    rrField.Append(_TxtFieldId, FieldValue.NewString(txtResult));
                    break;
                }

            case _RrTypeSoa when rdata.Length >= 20: // SOA
                {
                    int pos = rdataOffset;
                    string mname = DnsNameParser.ReadName(fullPacket, ref pos);
                    string rname = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_SoaMnameFieldId, FieldValue.NewString(mname));
                    rrField.Append(_SoaRnameFieldId, FieldValue.NewString(rname));

                    // Skip to the 5 fixed-size fields (20 bytes: serial, refresh, retry, expire, minttl)
                    // pos now points after rname within the full packet. The bound is checked against the whole
                    // message (not the RDATA end): it only protects the reads, a malformed record may still show
                    // numbers taken from the bytes that follow it.
                    int fixedOffset = pos;
                    if (fixedOffset + 20 <= fullPacket.Length)
                    {
                        rrField.Append(_SoaSerialFieldId,
                            FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(fullPacket[fixedOffset..])));
                        rrField.Append(_SoaRefreshFieldId,
                            FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(fullPacket[(fixedOffset + 4)..])));
                        rrField.Append(_SoaRetryFieldId,
                            FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(fullPacket[(fixedOffset + 8)..])));
                        rrField.Append(_SoaExpireFieldId,
                            FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(fullPacket[(fixedOffset + 12)..])));
                        rrField.Append(_SoaMinTtlFieldId,
                            FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(fullPacket[(fixedOffset + 16)..])));
                    }
                    break;
                }

            case _RrTypeSrv when rdata.Length >= 6: // SRV
                {
                    ushort priority = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    ushort weight = BinaryPrimitives.ReadUInt16BigEndian(rdata[2..]);
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(rdata[4..]);
                    rrField.Append(_SrvPriorityFieldId, FieldValue.NewU64(priority));
                    rrField.Append(_SrvWeightFieldId, FieldValue.NewU64(weight));
                    rrField.Append(_SrvPortFieldId, FieldValue.NewU64(port));
                    int pos = rdataOffset + 6;
                    string target = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_SrvNameFieldId, FieldValue.NewString(target));
                    break;
                }

            case _RrTypeDs when rdata.Length >= _DsMinRdLength: // DS (Delegation Signer, RFC 4034)
                {
                    // Key Tag(2) + Algorithm(1) + Digest Type(1) + Digest(variable)
                    ushort keyTag = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    byte algorithm = rdata[2];
                    byte digestType = rdata[3];
                    rrField.Append(_DsKeyTagFieldId, FieldValue.NewU64(keyTag));
                    rrField.AppendWithCustomText(_DsAlgorithmFieldId,
                        FieldValue.NewU64(algorithm), DnsDisplayTables.GetDnssecAlgorithmDisplayText(algorithm));
                    rrField.AppendWithCustomText(_DsDigestTypeFieldId,
                        FieldValue.NewU64(digestType), DnsDisplayTables.GetDsDigestTypeDisplayText(digestType));
                    if (rdata.Length > 4)
                    {
                        rrField.Append(_DsDigestFieldId,
                            FieldValue.NewBytes(fullMemory.Slice(rdataOffset + 4, rdata.Length - 4)));
                    }
                    break;
                }

            case _RrTypeRrsig when rdata.Length >= _RrsigMinRdLength: // RRSIG (RFC 4034)
                {
                    // TypeCovered(2) + Algorithm(1) + Labels(1) + OrigTTL(4) + Expiration(4) +
                    // Inception(4) + KeyTag(2) = 18 bytes fixed, then Signer's Name + Signature
                    ushort typeCovered = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    byte algorithm = rdata[2];
                    byte labels = rdata[3];
                    uint origTtl = BinaryPrimitives.ReadUInt32BigEndian(rdata[4..]);
                    uint expiration = BinaryPrimitives.ReadUInt32BigEndian(rdata[8..]);
                    uint inception = BinaryPrimitives.ReadUInt32BigEndian(rdata[12..]);
                    ushort keyTag = BinaryPrimitives.ReadUInt16BigEndian(rdata[16..]);

                    rrField.AppendWithCustomText(_RrsigTypeCoveredFieldId,
                        FieldValue.NewU64(typeCovered), DnsDisplayTables.GetTypeDisplayText(typeCovered));
                    rrField.AppendWithCustomText(_RrsigAlgorithmFieldId,
                        FieldValue.NewU64(algorithm), DnsDisplayTables.GetDnssecAlgorithmDisplayText(algorithm));
                    rrField.Append(_RrsigLabelsFieldId, FieldValue.NewU64(labels));
                    rrField.Append(_RrsigOrigTtlFieldId, FieldValue.NewU64(origTtl));
                    rrField.Append(_RrsigExpirationFieldId, FieldValue.NewU64(expiration));
                    rrField.Append(_RrsigInceptionFieldId, FieldValue.NewU64(inception));
                    rrField.Append(_RrsigKeyTagFieldId, FieldValue.NewU64(keyTag));

                    // Signer's Name is a DNS name starting at rdata[18] (in the full packet)
                    int pos = rdataOffset + 18;
                    string signersName = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_RrsigSignersNameFieldId, FieldValue.NewString(signersName));

                    // Signature is the remainder after the signer's name
                    int sigOffset = pos;
                    int sigLen = rdataOffset + rdata.Length - sigOffset;
                    if (sigLen > 0)
                    {
                        rrField.Append(_RrsigSignatureFieldId,
                            FieldValue.NewBytes(fullMemory.Slice(sigOffset, sigLen)));
                    }
                    break;
                }

            case _RrTypeNsec when rdata.Length >= _NsecMinRdLength: // NSEC (RFC 4034)
                {
                    // Next Domain Name (DNS-encoded) + Type Bit Maps
                    int pos = rdataOffset;
                    string nextDomain = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_NsecNextDomainFieldId, FieldValue.NewString(nextDomain));

                    // Parse type bit maps to produce a human-readable list of covered types
                    int bitmapOffset = pos - rdataOffset;
                    if (bitmapOffset < rdata.Length)
                    {
                        string typeList = _ParseNsecTypeBitMaps(rdata[bitmapOffset..]);
                        rrField.Append(_NsecTypeBitmapFieldId, FieldValue.NewString(typeList));
                    }
                    break;
                }

            case _RrTypeDnskey when rdata.Length >= _DnskeyMinRdLength: // DNSKEY (RFC 4034)
                {
                    // Flags(2) + Protocol(1) + Algorithm(1) + Public Key(variable)
                    ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    byte protocol = rdata[2];
                    byte algorithm = rdata[3];

                    // RFC 4034 §2.1.1 numbers the flag bits from the most significant bit: bit 7 (mask 0x0100) is
                    // Zone Key and bit 15 (mask 0x0001) is Secure Entry Point.
                    bool isZoneKey = (flags & 0x0100) != 0;
                    bool isSep = (flags & 0x0001) != 0;
                    string flagsText = (isZoneKey, isSep) switch
                    {
                        (true, true) => "Zone Key, Secure Entry Point",
                        (true, false) => "Zone Key",
                        (false, true) => "Secure Entry Point",
                        _ => "None"
                    };

                    rrField.AppendWithCustomText(_DnskeyFlagsFieldId,
                        FieldValue.NewU64(flags), flagsText);
                    rrField.Append(_DnskeyProtocolFieldId, FieldValue.NewU64(protocol));
                    rrField.AppendWithCustomText(_DnskeyAlgorithmFieldId,
                        FieldValue.NewU64(algorithm), DnsDisplayTables.GetDnssecAlgorithmDisplayText(algorithm));
                    if (rdata.Length > 4)
                    {
                        rrField.Append(_DnskeyPublicKeyFieldId,
                            FieldValue.NewBytes(fullMemory.Slice(rdataOffset + 4, rdata.Length - 4)));
                    }
                    break;
                }
        }
    }

    /// <summary>
    /// Parses an NSEC Type Bit Maps field (RFC 4034 §4.1.2) into a human-readable comma-separated list of RR type
    /// names. The NSEC3 bit map has the same format, but NSEC3 records are not decoded by <c>_ParseRData</c>.
    /// Format: {window(1)} {bitmap_length(1)} {bitmap(N)} repeated.
    /// Stops at the first block that is empty, longer than 32 bytes or runs past the data; earlier blocks are kept. This runs only when the
    /// tree is materialized and allocates a list plus one string, which is acceptable off the parse path.
    /// </summary>
    private static string _ParseNsecTypeBitMaps(ReadOnlySpan<byte> data)
    {
        List<string> types = [];
        int pos = 0;

        while (pos + 2 <= data.Length)
        {
            byte windowBlock = data[pos];
            byte bitmapLen = data[pos + 1];
            pos += 2;

            // RFC 4034 §4.1.2: a bit map block holds 1 to 32 bytes. A longer block is malformed: its types would pass
            // type 65535 and wrap in the ushort cast below, and a hostile record could list hundreds of thousands of
            // types (one string each) within a 64 KiB message. The list ends at such a block; earlier blocks are kept.
            if (bitmapLen == 0 || bitmapLen > _NsecMaxBitmapLength || pos + bitmapLen > data.Length)
            {
                break;
            }

            // Each bit in the bitmap represents a type number (the most significant bit of each byte is the lowest type):
            // type = windowBlock * 256 + byteIndex * 8 + bitIndex
            for (int i = 0; i < bitmapLen; i++)
            {
                byte b = data[pos + i];
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (0x80 >> bit)) != 0)
                    {
                        ushort typeNum = (ushort)(windowBlock * 256 + i * 8 + bit);
                        types.Add(DnsDisplayTables.GetTypeName(typeNum));
                    }
                }
            }

            pos += bitmapLen;
        }

        return string.Join(", ", types);
    }
    #endregion
}
