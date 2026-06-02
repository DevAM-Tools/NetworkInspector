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
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
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
    private const string DnsIndexGroup = "dns";

    // Resource-record TYPE codes and minimum RDLENGTHs for the index-group-bearing record types.
    // These constants are shared between the eager index detector (DetectRrGroups) and the lazy
    // populator (ParseResourceRecords/ParseRData) so that the emission guard for each group lives in
    // exactly one place — a change here updates both paths simultaneously and prevents the detector
    // from silently drifting away from what the populator actually emits.
    private const ushort RrTypeOpt = 41;       // EDNS0 OPT (RFC 6891) — always emits dns.opt fields.
    private const ushort RrTypeDs = 43;        // DS (RFC 4034).
    private const ushort RrTypeRrsig = 46;     // RRSIG (RFC 4034).
    private const ushort RrTypeNsec = 47;      // NSEC (RFC 4034).
    private const ushort RrTypeDnskey = 48;    // DNSKEY (RFC 4034).
    private const int DsMinRdLength = 4;       // Key Tag(2) + Algorithm(1) + Digest Type(1).
    private const int RrsigMinRdLength = 18;   // Fixed RRSIG header before signer's name.
    private const int NsecMinRdLength = 1;     // At least one byte of next-domain/bitmap data.
    private const int DnskeyMinRdLength = 4;   // Flags(2) + Protocol(1) + Algorithm(1).

    #endregion

    #region Protocol container

    [BytesField("dns", "Domain Name System", IndexGroup = DnsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Header fields (always present)

    [U64Field("dns.id", "Transaction ID", IndexGroup = DnsIndexGroup)]
    private FieldId _IdFieldId;

    [U64Field("dns.flags", "Flags", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("dns.flags.response", "Response", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsResponseFieldId;

    [U64Field("dns.flags.opcode", "Opcode", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsOpcodeFieldId;

    [BoolField("dns.flags.authoritative", "Authoritative", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsAuthFieldId;

    [BoolField("dns.flags.truncated", "Truncated", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsTruncFieldId;

    [BoolField("dns.flags.recdesired", "Recursion desired", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsRdFieldId;

    [BoolField("dns.flags.recavail", "Recursion available", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsRaFieldId;

    [BoolField("dns.flags.z", "Z (reserved)", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsZFieldId;

    [BoolField("dns.flags.authenticated", "Answer authenticated", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsAdFieldId;

    [BoolField("dns.flags.checkdisable", "Non-authenticated data: Acceptable", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsCdFieldId;

    [U64Field("dns.flags.rcode", "Reply code", IndexGroup = DnsIndexGroup)]
    private FieldId _FlagsRcodeFieldId;

    [U64Field("dns.count.queries", "Questions", IndexGroup = DnsIndexGroup)]
    private FieldId _QdCountFieldId;

    [U64Field("dns.count.answers", "Answer RRs", IndexGroup = DnsIndexGroup)]
    private FieldId _AnCountFieldId;

    [U64Field("dns.count.auth_rr", "Authority RRs", IndexGroup = DnsIndexGroup)]
    private FieldId _NsCountFieldId;

    [U64Field("dns.count.add_rr", "Additional RRs", IndexGroup = DnsIndexGroup)]
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

    // Pre-allocated populator
    private LazyPopulator _Populator = null!;

    partial void OnStartCustom(Stack stack) =>
        _Populator = PopulateDnsFields;

    /// <summary>
    /// Parses a Dns protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ReadOnlyMemory<byte> dnsData = data;

        // RFC 1035 §4.2.2: DNS over TCP uses a 2-byte big-endian length prefix
        // before each DNS message. Detect and strip it if present.
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

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DnsGroupId);

        if (!DnsHeader.TryParse(dnsData.Span, out DnsHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DnsHeader.Size, (ulong)dnsData.Length);
        }

        ushort transactionId = header.TransactionId;
        bool isResponse = header.IsResponse;

        // Record conditional index groups
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
        DetectRrGroups(
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

        // Build summary text
        string hexId = DisplayTables.FormatHexU16(transactionId);
        LazyString summary = isResponse
            ? ZA.Lazy("Domain Name System (response), Transaction ID: 0x", hexId)
            : ZA.Lazy("Domain Name System (query), Transaction ID: 0x", hexId);

        // Set packet info for the info column
        parentField.SetPacketInfo(isResponse
            ? ZA.Lazy("DNS Response 0x", hexId)
            : ZA.Lazy("DNS Query 0x", hexId));

        FieldValue containerValue = FieldValue.NewBytes(dnsData);
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Populates all DNS fields from the stored packet bytes.
    /// Called lazily on first access of the DNS container's children.
    /// </summary>
    private ParseResult PopulateDnsFields(in MutField container)
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
            ZA.Lazy(DisplayTables.FormatHexU16(header.Flags), " ", DnsFlagsFormatter.Format(header.Flags)));

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

        // Parse sections
        int offset = DnsHeader.Size;

        // Questions section
        if (header.QuestionCount > 0)
        {
            MutField queriesContainer = container.AppendWithCustomText(
                _QueriesContainerFieldId, FieldValue.None,
                ZA.Lazy("Queries (", header.QuestionCount, ")"));

            ParseQuestions(in queriesContainer, span, ref offset, header.QuestionCount);
        }

        // Answers section
        if (header.AnswerCount > 0)
        {
            MutField answersContainer = container.AppendWithCustomText(
                _AnswersContainerFieldId, FieldValue.None,
                ZA.Lazy("Answers (", header.AnswerCount, ")"));

            ParseResourceRecords(in answersContainer, span, dnsData, ref offset, header.AnswerCount);
        }

        // Authority section
        if (header.AuthorityCount > 0)
        {
            MutField authContainer = container.AppendWithCustomText(
                _AnswersContainerFieldId, FieldValue.None,
                ZA.Lazy("Authoritative nameservers (", header.AuthorityCount, ")"));

            ParseResourceRecords(in authContainer, span, dnsData, ref offset, header.AuthorityCount);
        }

        // Additional section
        if (header.AdditionalCount > 0)
        {
            MutField addContainer = container.AppendWithCustomText(
                _AnswersContainerFieldId, FieldValue.None,
                ZA.Lazy("Additional records (", header.AdditionalCount, ")"));

            ParseResourceRecords(in addContainer, span, dnsData, ref offset, header.AdditionalCount);
        }

        return 0;
    }

    /// <summary>Parses the question section of a DNS packet.</summary>
    private void ParseQuestions(
        in MutField container, ReadOnlySpan<byte> data, ref int offset, ushort count)
    {
        for (int i = 0; i < count; i++)
        {
            if (offset >= data.Length)
            {
                break;
            }

            string name = DnsNameParser.ReadName(data, ref offset);

            // After the name: QType (2 bytes) + QClass (2 bytes)
            if (offset + 4 > data.Length)
            {
                break;
            }

            ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4;

            // Create question entry as child of the queries container
            string typeName = DnsDisplayTables.GetTypeName(qtype);
            string className = DnsDisplayTables.GetClassDisplayText(qclass);
            MutField qryField = container.AppendWithCustomText(
                _QryNameFieldId, FieldValue.NewString(name),
                ZA.Lazy(name, ": type ", typeName, ", class ", className));

            qryField.Append(_QryNameLenFieldId, FieldValue.NewU64((ulong)name.Length));
            qryField.AppendWithCustomText(_QryTypeFieldId,
                FieldValue.NewU64(qtype), DnsDisplayTables.GetTypeDisplayText(qtype));
            qryField.AppendWithCustomText(_QryClassFieldId,
                FieldValue.NewU64(qclass), DnsDisplayTables.GetClassDisplayText(qclass));
        }
    }

    /// <summary>
    /// Parses resource records (Answer, Authority, or Additional sections).
    /// Each resource record: Name + Type(2) + Class(2) + TTL(4) + RDLENGTH(2) + RDATA(N).
    /// </summary>
    private void ParseResourceRecords(
        in MutField container, ReadOnlySpan<byte> data, ReadOnlyMemory<byte> fullMemory,
        ref int offset, ushort count)
    {
        for (int i = 0; i < count; i++)
        {
            if (offset >= data.Length)
            {
                break;
            }

            string name = DnsNameParser.ReadName(data, ref offset);

            // Type(2) + Class(2) + TTL(4) + RDLENGTH(2) = 10 bytes minimum
            if (offset + 10 > data.Length)
            {
                break;
            }

            ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;

            if (offset + rdLength > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> rdata = data[offset..(offset + rdLength)];

            // OPT (type 41) uses CLASS/TTL fields with different semantics (RFC 6891)
            if (rrType == RrTypeOpt)
            {
                ParseOptRecord(in container, rrClass, ttl, rdata, fullMemory, offset);
                offset += rdLength;
                continue;
            }

            // Create RR entry as child of the section container
            string typeName = DnsDisplayTables.GetTypeName(rrType);
            MutField rrField = container.AppendWithCustomText(
                _RespNameFieldId, FieldValue.NewString(name),
                ZA.Lazy(name, ": type ", typeName));

            rrField.AppendWithCustomText(_RespTypeFieldId,
                FieldValue.NewU64(rrType), DnsDisplayTables.GetTypeDisplayText(rrType));
            rrField.AppendWithCustomText(_RespClassFieldId,
                FieldValue.NewU64(rrClass), DnsDisplayTables.GetClassDisplayText(rrClass));
            rrField.Append(_RespTtlFieldId, FieldValue.NewU64(ttl));
            rrField.Append(_RespLenFieldId, FieldValue.NewU64(rdLength));

            // Parse type-specific RDATA
            ParseRData(in rrField, rrType, rdata, data, fullMemory, offset);

            offset += rdLength;
        }
    }

    /// <summary>
    /// Eagerly walks the question and resource-record sections to decide which RR-type dependent
    /// index groups apply, mirroring <see cref="ParseResourceRecords"/>'s offset advancement and
    /// per-type emission guards without building any field. The same compression-aware name reader
    /// is used so the offset progression is identical to the populator's, guaranteeing the recorded
    /// groups match the fields that will be emitted. This duplicates the record walk; that is the
    /// accepted cost of keeping the field tree lazy while the presence index stays content-consistent.
    /// </summary>
    private static void DetectRrGroups(
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

        // Skip the question section exactly as ParseQuestions advances the offset.
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
                case RrTypeOpt: // OPT (EDNS0) — ParseOptRecord always emits the dns.opt fields.
                    hasOpt = true;
                    if (OptHasOption(span.Slice(offset, rdLength)))
                    {
                        hasOptOption = true;
                    }
                    break;
                case RrTypeDs when rdLength >= DsMinRdLength:
                    hasDs = true;
                    break;
                case RrTypeRrsig when rdLength >= RrsigMinRdLength:
                    hasRrsig = true;
                    break;
                case RrTypeNsec when rdLength >= NsecMinRdLength:
                    hasNsec = true;
                    break;
                case RrTypeDnskey when rdLength >= DnskeyMinRdLength:
                    hasDnskey = true;
                    break;
            }

            offset += rdLength;
        }
    }

    /// <summary>
    /// Returns true when an OPT record's RDATA carries at least one EDNS0 option that the populator
    /// would emit, mirroring the first TLV guard in <see cref="ParseOptRecord"/> (a 4-byte option
    /// header followed by its declared option-length within bounds).
    /// </summary>
    private static bool OptHasOption(ReadOnlySpan<byte> rdata)
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
    private void ParseOptRecord(
        in MutField container, ushort udpPayloadSize, uint ttlField,
        ReadOnlySpan<byte> rdata, ReadOnlyMemory<byte> fullMemory, int rdataOffset)
    {
        MutField optField = container.AppendWithCustomText(
            _RespNameFieldId, FieldValue.NewString("<Root>"),
            ZA.Lazy("<Root>: type OPT"));

        optField.AppendWithCustomText(_RespTypeFieldId,
            FieldValue.NewU64(41), DnsDisplayTables.GetTypeDisplayText(41));

        // CLASS = UDP payload size (not a DNS class)
        optField.Append(_OptUdpPayloadSizeFieldId, FieldValue.NewU64(udpPayloadSize));

        // TTL bytes: [0]=extended RCODE, [1]=version, [2-3]=flags (bit 15=DO)
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

        // Parse EDNS0 options as TLV entries
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
                ZA.Lazy("Option: ", DnsDisplayTables.GetEdnsOptionName(optionCode)));

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
    private void ParseRData(
        in MutField rrField, ushort rrType,
        ReadOnlySpan<byte> rdata, ReadOnlySpan<byte> fullPacket,
        ReadOnlyMemory<byte> fullMemory, int rdataOffset)
    {
        switch (rrType)
        {
            case 1 when rdata.Length >= 4: // A record
                IPv4Address ipv4 = DnsNameParser.ParseARecord(rdata);
                rrField.Append(_AFieldId, FieldValue.NewIPv4(ipv4));
                break;

            case 28 when rdata.Length >= 16: // AAAA record
                IPv6Address ipv6 = DnsNameParser.ParseAAAARecord(rdata);
                rrField.Append(_AAAAFieldId, FieldValue.NewIPv6(ipv6));
                break;

            case 5: // CNAME
                {
                    int pos = rdataOffset;
                    string cname = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_CnameFieldId, FieldValue.NewString(cname));
                    break;
                }

            case 2: // NS
                {
                    int pos = rdataOffset;
                    string ns = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_NsFieldId, FieldValue.NewString(ns));
                    break;
                }

            case 12: // PTR
                {
                    int pos = rdataOffset;
                    string ptr = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_PtrFieldId, FieldValue.NewString(ptr));
                    break;
                }

            case 15 when rdata.Length >= 2: // MX
                {
                    ushort preference = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    rrField.Append(_MxPreferenceFieldId, FieldValue.NewU64(preference));
                    int pos = rdataOffset + 2;
                    string exchange = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_MxExchangeFieldId, FieldValue.NewString(exchange));
                    break;
                }

            case 16 when rdata.Length >= 1: // TXT
                {
                    // TXT records contain one or more length-prefixed strings.
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

                    // Second pass: fill the string directly via string.Create (zero-copy, no StringBuilder).
                    ReadOnlySpan<byte> rdataCapture = rdata;
                    string txtResult = string.Create(totalLen, (rdataCapture.Length, rdataCapture.ToArray()),
                        static (chars, state) =>
                        {
                            ReadOnlySpan<byte> src = state.Item2;
                            int pos = 0;
                            int written = 0;
                            while (pos < state.Item1)
                            {
                                byte strLen = src[pos++];
                                if (pos + strLen > state.Item1)
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

            case 6 when rdata.Length >= 20: // SOA
                {
                    int pos = rdataOffset;
                    string mname = DnsNameParser.ReadName(fullPacket, ref pos);
                    string rname = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_SoaMnameFieldId, FieldValue.NewString(mname));
                    rrField.Append(_SoaRnameFieldId, FieldValue.NewString(rname));

                    // Skip to the 5 fixed-size fields (20 bytes: serial, refresh, retry, expire, minttl)
                    // pos now points after rname within the full packet
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

            case 33 when rdata.Length >= 6: // SRV
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

            case RrTypeDs when rdata.Length >= DsMinRdLength: // DS (Delegation Signer, RFC 4034)
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

            case RrTypeRrsig when rdata.Length >= RrsigMinRdLength: // RRSIG (RFC 4034)
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

            case RrTypeNsec when rdata.Length >= NsecMinRdLength: // NSEC (RFC 4034)
                {
                    // Next Domain Name (DNS-encoded) + Type Bit Maps
                    int pos = rdataOffset;
                    string nextDomain = DnsNameParser.ReadName(fullPacket, ref pos);
                    rrField.Append(_NsecNextDomainFieldId, FieldValue.NewString(nextDomain));

                    // Parse type bit maps to produce a human-readable list of covered types
                    int bitmapOffset = pos - rdataOffset;
                    if (bitmapOffset < rdata.Length)
                    {
                        string typeList = ParseNsecTypeBitMaps(rdata[bitmapOffset..]);
                        rrField.Append(_NsecTypeBitmapFieldId, FieldValue.NewString(typeList));
                    }
                    break;
                }

            case RrTypeDnskey when rdata.Length >= DnskeyMinRdLength: // DNSKEY (RFC 4034)
                {
                    // Flags(2) + Protocol(1) + Algorithm(1) + Public Key(variable)
                    ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rdata);
                    byte protocol = rdata[2];
                    byte algorithm = rdata[3];

                    // Flag bit 7 (0x0100) = Zone Key, bit 15 (0x0001) = Secure Entry Point
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
                        FieldValue.NewU64(flags), ZA.Lazy(flagsText));
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
    /// Parses NSEC/NSEC3 Type Bit Maps into a human-readable comma-separated list of RR type names.
    /// Format: {window(1)} {bitmap_length(1)} {bitmap(N)} repeated.
    /// </summary>
    private static string ParseNsecTypeBitMaps(ReadOnlySpan<byte> data)
    {
        List<string> types = [];
        int pos = 0;

        while (pos + 2 <= data.Length)
        {
            byte windowBlock = data[pos];
            byte bitmapLen = data[pos + 1];
            pos += 2;

            if (bitmapLen == 0 || pos + bitmapLen > data.Length)
            {
                break;
            }

            // Each bit in the bitmap represents a type number:
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
