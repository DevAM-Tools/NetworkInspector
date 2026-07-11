// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Transport Layer Security (TLS) protocol parser (RFC 8446).
/// Parses TLS record layer and handshake messages including Client/Server Hello.
/// No decryption — only handshake metadata and record structure are parsed.
/// <para>Field tree structure:</para>
/// <code>
/// tls: Transport Layer Security
/// ├── tls.record: TLS Record Layer
/// │   ├── tls.record.content_type: 22 (Handshake)
/// │   ├── tls.record.version: 0x0303 (TLS 1.2)
/// │   └── tls.record.length: 512
/// ├── tls.handshake: Handshake Protocol
/// │   ├── tls.handshake.type: 1 (Client Hello)
/// │   ├── tls.handshake.length: 508
/// │   ├── tls.handshake.version: 0x0303 (TLS 1.2)
/// │   ├── tls.handshake.random: (32 bytes)
/// │   ├── tls.handshake.session_id_length: 32
/// │   ├── tls.handshake.session_id: (32 bytes)
/// │   ├── tls.handshake.cipher_suites_length: 28
/// │   ├── tls.handshake.ciphersuite: TLS_AES_128_GCM_SHA256 (0x1301)
/// │   │   [... repeated]
/// │   ├── tls.handshake.comp_methods_length: 1
/// │   ├── tls.handshake.comp_method: null (0)
/// │   └── tls.handshake.extensions_length: 443
/// │       └── tls.handshake.extension
/// │           ├── tls.handshake.extension.type: server_name (0)
/// │           ├── tls.handshake.extension.len: 18
/// │           └── tls.handshake.extensions.server_name: example.com
/// └── [additional records if multiple in segment]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("tls", "Transport Layer Security", Description = "TLS (RFC 8446)")]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPortKey)]
public sealed partial class TlsProtocol : IProtocol
{
    #region Constants

    /// <summary>TCP port for HTTPS (TLS).</summary>
    public const ulong TcpPortKey = 443;

    /// <summary>Index group for always-present TLS fields.</summary>
    private const string _TlsIndexGroup = "tls";

    #endregion

    #region Protocol container

    [BytesField("tls", "Transport Layer Security", IndexGroup = _TlsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Record Layer fields (always present)

    [NoneField("tls.record", "TLS Record Layer", IndexGroup = _TlsIndexGroup)]
    private FieldId _RecordFieldId;

    [U64Field("tls.record.content_type", "Content Type", IndexGroup = _TlsIndexGroup)]
    private FieldId _ContentTypeFieldId;

    [U64Field("tls.record.version", "Version", IndexGroup = _TlsIndexGroup)]
    private FieldId _RecordVersionFieldId;

    [U64Field("tls.record.length", "Length", IndexGroup = _TlsIndexGroup)]
    private FieldId _RecordLengthFieldId;

    #endregion

    #region Handshake fields (conditional — only for content type 22)

    [NoneField("tls.handshake", "Handshake Protocol", IndexGroup = "tls.handshake")]
    private FieldId _HandshakeFieldId;

    [U64Field("tls.handshake.type", "Handshake Type", IndexGroup = "tls.handshake")]
    private FieldId _HandshakeTypeFieldId;

    [U64Field("tls.handshake.length", "Length", IndexGroup = "tls.handshake")]
    private FieldId _HandshakeLengthFieldId;

    [U64Field("tls.handshake.version", "Version", IndexGroup = "tls.handshake")]
    private FieldId _HandshakeVersionFieldId;

    [BytesField("tls.handshake.random", "Random", IndexGroup = "tls.handshake")]
    private FieldId _HandshakeRandomFieldId;

    [U64Field("tls.handshake.session_id_length", "Session ID Length", IndexGroup = "tls.handshake")]
    private FieldId _SessionIdLengthFieldId;

    [BytesField("tls.handshake.session_id", "Session ID", IndexGroup = "tls.handshake")]
    private FieldId _SessionIdFieldId;

    [U64Field("tls.handshake.cipher_suites_length", "Cipher Suites Length", IndexGroup = "tls.handshake")]
    private FieldId _CipherSuitesLengthFieldId;

    [U64Field("tls.handshake.ciphersuite", "Cipher Suite", IndexGroup = "tls.handshake.ciphersuite")]
    private FieldId _CipherSuiteFieldId;

    [U64Field("tls.handshake.comp_methods_length", "Compression Methods Length", IndexGroup = "tls.handshake")]
    private FieldId _CompMethodsLengthFieldId;

    [U64Field("tls.handshake.comp_method", "Compression Method", IndexGroup = "tls.handshake")]
    private FieldId _CompMethodFieldId;

    [U64Field("tls.handshake.extensions_length", "Extensions Length", IndexGroup = "tls.handshake.ext")]
    private FieldId _ExtensionsLengthFieldId;

    #endregion

    #region Extension fields (conditional)

    [NoneField("tls.handshake.extension", "Extension", IndexGroup = "tls.handshake.ext")]
    private FieldId _ExtensionFieldId;

    [U64Field("tls.handshake.extension.type", "Type", IndexGroup = "tls.handshake.ext")]
    private FieldId _ExtensionTypeFieldId;

    [U64Field("tls.handshake.extension.len", "Length", IndexGroup = "tls.handshake.ext")]
    private FieldId _ExtensionLenFieldId;

    [BytesField("tls.handshake.extension.data", "Data", IndexGroup = "tls.handshake.ext")]
    private FieldId _ExtensionDataFieldId;

    #endregion

    #region SNI

    [StringField("tls.handshake.extensions.server_name", "Server Name", IndexGroup = "tls.sni")]
    private FieldId _SniFieldId;

    #endregion

    #region ALPN

    [StringField("tls.handshake.extensions.alpn_str", "ALPN Protocol", IndexGroup = "tls.alpn")]
    private FieldId _AlpnFieldId;

    #endregion

    #region Supported Groups (extension type 10)

    [U64Field("tls.handshake.extensions.supported_group", "Supported Group", IndexGroup = "tls.supported_groups")]
    private FieldId _SupportedGroupFieldId;

    #endregion

    #region Signature Algorithms (extension type 13)

    [U64Field("tls.handshake.extensions.sig_hash_alg", "Signature Algorithm", IndexGroup = "tls.sig_algs")]
    private FieldId _SigAlgFieldId;

    #endregion

    #region Supported Versions (extension type 43)

    [U64Field("tls.handshake.extensions.supported_version", "Supported Version", IndexGroup = "tls.supported_versions")]
    private FieldId _SupportedVersionFieldId;

    #endregion

    #region Key Share (extension type 51)

    [NoneField("tls.handshake.extensions.key_share.entry", "Key Share Entry", IndexGroup = "tls.key_share")]
    private FieldId _KeyShareEntryFieldId;

    [U64Field("tls.handshake.extensions.key_share.group", "Group", IndexGroup = "tls.key_share")]
    private FieldId _KeyShareGroupFieldId;

    [U64Field("tls.handshake.extensions.key_share.key_exchange_length", "Key Exchange Length", IndexGroup = "tls.key_share")]
    private FieldId _KeyShareKeyExchangeLenFieldId;

    [BytesField("tls.handshake.extensions.key_share.key_exchange", "Key Exchange", IndexGroup = "tls.key_share")]
    private FieldId _KeyShareKeyExchangeFieldId;

    #endregion

    #region Certificate chain fields (type 11)

    [U64Field("tls.handshake.certificates_length", "Certificates Length", IndexGroup = "tls.cert")]
    private FieldId _CertificatesLengthFieldId;

    [NoneField("tls.handshake.certificate", "Certificate", IndexGroup = "tls.cert")]
    private FieldId _CertificateFieldId;

    [U64Field("tls.handshake.certificate.length", "Certificate Length", IndexGroup = "tls.cert")]
    private FieldId _CertificateLengthFieldId;

    [BytesField("tls.handshake.certificate.data", "Certificate Data", IndexGroup = "tls.cert")]
    private FieldId _CertificateDataFieldId;

    #endregion

    #region Alert fields

    [U64Field("tls.alert.level", "Level", IndexGroup = "tls.alert")]
    private FieldId _AlertLevelFieldId;

    [U64Field("tls.alert.description", "Description", IndexGroup = "tls.alert")]
    private FieldId _AlertDescFieldId;

    #endregion

    #region Populators

    // Pre-allocated delegate; wired once in _OnStartCustom (build phase, not per packet).
    private LazyPopulator _Populator = null!;

    partial void _OnStartCustom(Stack stack) => _Populator = _PopulateTls;

    /// <summary>
    /// Parses TLS records from the given data. Supports multiple records per segment.
    /// Record-level summary is eager; handshake/alert content is lazy via the protocol container.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < TlsRecordHeader.Size)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TlsRecordHeader.Size, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_TlsGroupId);

        // Read first record header for summary
        if (!TlsRecordHeader.TryParse(data.Span, out TlsRecordHeader firstRecord))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TlsRecordHeader.Size, (ulong)data.Length);
        }

        // Eagerly walk every TLS record (and, for handshake records, every handshake message and
        // extension) to record exactly the content-dependent index groups whose fields the lazy
        // populator will emit. The decision depends on record content types, handshake message
        // types and extension types/lengths spread across the whole segment — not just the first
        // record — so a first-record check would both miss groups in later records and falsely
        // record handshake groups for truncated messages. This repeats the record walk to keep the
        // presence index content-consistent with materialization and free of false positives.
        TlsGroupFlags flags = default;
        _DetectTlsGroups(data.Span, ref flags);

        if (flags.Handshake)
        {
            context.RecordGroupPresence(_TlsHandshakeGroupId);
        }
        if (flags.CipherSuite)
        {
            context.RecordGroupPresence(_TlsHandshakeCiphersuiteGroupId);
        }
        if (flags.Ext)
        {
            context.RecordGroupPresence(_TlsHandshakeExtGroupId);
        }
        if (flags.Sni)
        {
            context.RecordGroupPresence(_TlsSniGroupId);
        }
        if (flags.Alpn)
        {
            context.RecordGroupPresence(_TlsAlpnGroupId);
        }
        if (flags.SupportedGroups)
        {
            context.RecordGroupPresence(_TlsSupported_groupsGroupId);
        }
        if (flags.SigAlgs)
        {
            context.RecordGroupPresence(_TlsSig_algsGroupId);
        }
        if (flags.SupportedVersions)
        {
            context.RecordGroupPresence(_TlsSupported_versionsGroupId);
        }
        if (flags.KeyShare)
        {
            context.RecordGroupPresence(_TlsKey_shareGroupId);
        }
        if (flags.Cert)
        {
            context.RecordGroupPresence(_TlsCertGroupId);
        }
        if (flags.Alert)
        {
            context.RecordGroupPresence(_TlsAlertGroupId);
        }

        // Build summary text from first record
        string contentTypeName = TlsDisplayTables.GetContentTypeName(firstRecord.ContentType);
        LazyString summary = ZA.Lazy("Transport Layer Security, ", contentTypeName);

        // Set packet info
        parentField.SetPacketInfo(ZA.Lazy("TLS ", contentTypeName));

        // Store entire TLS data in container for lazy parsing
        FieldValue containerValue = FieldValue.NewBytes(data);
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Lazy populator: per-record containers with record-header fields, plus handshake
    /// messages, extensions, and alerts for all TLS records in the segment. Fires on
    /// first access of the TLS container's children. The <c>tls.record</c> containers
    /// and the handshake/alert sub-fields are siblings under the <c>tls</c> container.
    /// </summary>
    private ParseResult _PopulateTls(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> tlsData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        ReadOnlySpan<byte> span = tlsData.Span;
        int offset = 0;

        while (offset + TlsRecordHeader.Size <= span.Length)
        {
            if (!TlsRecordHeader.TryParse(span[offset..], out TlsRecordHeader record))
            {
                break;
            }

            int recordPayloadEnd = offset + TlsRecordHeader.Size + record.Length;
            if (recordPayloadEnd > span.Length)
            {
                break; // Incomplete record — stop parsing
            }

            string ctText = TlsDisplayTables.GetContentTypeDisplayText(record.ContentType);
            string versionText = TlsDisplayTables.GetVersionDisplayText(record.Version);
            MutField recordField = container.AppendWithCustomText(
                _RecordFieldId, FieldValue.None,
                ZA.Lazy("TLS Record Layer: ", ctText));

            recordField.AppendWithCustomText(_ContentTypeFieldId,
                FieldValue.NewU64(record.ContentType), ctText);
            recordField.AppendWithCustomText(_RecordVersionFieldId,
                FieldValue.NewU64(record.Version), versionText);
            recordField.Append(_RecordLengthFieldId, FieldValue.NewU64(record.Length));

            int payloadOffset = offset + TlsRecordHeader.Size;
            ReadOnlySpan<byte> payload = span[payloadOffset..recordPayloadEnd];

            switch (record.ContentType)
            {
                case 22: // Handshake
                    _ParseHandshakeRecords(in container, payload, tlsData, payloadOffset);
                    break;
                case 21: // Alert
                    _ParseAlert(in container, payload);
                    break;
            }

            offset = recordPayloadEnd;
        }

        return 0;
    }

    /// <summary>
    /// Parses one or more handshake messages within a single TLS record.
    /// </summary>
    private void _ParseHandshakeRecords(
        in MutField recordField, ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> fullData, int payloadBaseOffset)
    {
        int offset = 0;

        while (offset + 4 <= payload.Length)
        {
            byte hsType = payload[offset];

            // Handshake length is 3 bytes big-endian
            int hsLength = (payload[offset + 1] << 16) | (payload[offset + 2] << 8) | payload[offset + 3];
            offset += 4;

            if (offset + hsLength > payload.Length)
            {
                break; // Incomplete handshake message
            }

            string hsTypeName = TlsDisplayTables.GetHandshakeTypeName(hsType);
            MutField hsField = recordField.AppendWithCustomText(
                _HandshakeFieldId, FieldValue.None,
                ZA.Lazy("Handshake Protocol: ", hsTypeName));

            hsField.AppendWithCustomText(_HandshakeTypeFieldId,
                FieldValue.NewU64(hsType),
                TlsDisplayTables.GetHandshakeTypeDisplayText(hsType));
            hsField.Append(_HandshakeLengthFieldId, FieldValue.NewU64((ulong)hsLength));

            ReadOnlySpan<byte> hsBody = payload[offset..(offset + hsLength)];

            // Parse type-specific handshake content
            switch (hsType)
            {
                case 1: // Client Hello
                    _ParseClientHello(in hsField, hsBody, fullData, payloadBaseOffset + offset);
                    break;
                case 2: // Server Hello
                    _ParseServerHello(in hsField, hsBody, fullData, payloadBaseOffset + offset);
                    break;
                case 11: // Certificate
                    _ParseCertificate(in hsField, hsBody, fullData, payloadBaseOffset + offset);
                    break;
            }

            offset += hsLength;
        }
    }

    /// <summary>
    /// Parses a TLS Client Hello handshake message.
    /// </summary>
    private void _ParseClientHello(
        in MutField hsField, ReadOnlySpan<byte> body,
        ReadOnlyMemory<byte> fullData, int bodyBaseOffset)
    {
        int pos = 0;

        // Version (2 bytes)
        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
        hsField.AppendWithCustomText(_HandshakeVersionFieldId,
            FieldValue.NewU64(version), TlsDisplayTables.GetVersionDisplayText(version));
        pos += 2;

        // Random (32 bytes)
        if (pos + 32 > body.Length)
        {
            return;
        }
        hsField.Append(_HandshakeRandomFieldId, FieldValue.NewBytes(fullData.Slice(bodyBaseOffset + pos, 32)));
        pos += 32;

        // Session ID
        if (pos + 1 > body.Length)
        {
            return;
        }
        byte sessionIdLen = body[pos++];
        hsField.Append(_SessionIdLengthFieldId, FieldValue.NewU64(sessionIdLen));

        if (pos + sessionIdLen > body.Length)
        {
            return;
        }
        if (sessionIdLen > 0)
        {
            hsField.Append(_SessionIdFieldId, FieldValue.NewBytes(fullData.Slice(bodyBaseOffset + pos, sessionIdLen)));
        }
        pos += sessionIdLen;

        // Cipher Suites
        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
        hsField.Append(_CipherSuitesLengthFieldId, FieldValue.NewU64(cipherSuitesLen));
        pos += 2;

        if (pos + cipherSuitesLen > body.Length)
        {
            return;
        }
        int cipherSuitesEnd = pos + cipherSuitesLen;
        while (pos + 2 <= cipherSuitesEnd)
        {
            ushort suite = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
            hsField.AppendWithCustomText(_CipherSuiteFieldId,
                FieldValue.NewU64(suite),
                TlsDisplayTables.GetCipherSuiteDisplayText(suite));
            pos += 2;
        }
        pos = cipherSuitesEnd;

        // Compression Methods
        if (pos + 1 > body.Length)
        {
            return;
        }
        byte compMethodsLen = body[pos++];
        hsField.Append(_CompMethodsLengthFieldId, FieldValue.NewU64(compMethodsLen));

        int compMethodsEnd = pos + compMethodsLen;
        if (compMethodsEnd > body.Length)
        {
            return;
        }
        while (pos < compMethodsEnd)
        {
            byte method = body[pos++];
            string methodText = TlsDisplayTables.GetCompressionMethodDisplayText(method);
            hsField.AppendWithCustomText(_CompMethodFieldId,
                FieldValue.NewU64(method), methodText);
        }

        // Extensions
        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
        hsField.Append(_ExtensionsLengthFieldId, FieldValue.NewU64(extensionsLen));
        pos += 2;

        _ParseExtensions(in hsField, body, ref pos, extensionsLen, fullData, bodyBaseOffset);
    }

    /// <summary>
    /// Parses a TLS Server Hello handshake message.
    /// </summary>
    private void _ParseServerHello(
        in MutField hsField, ReadOnlySpan<byte> body,
        ReadOnlyMemory<byte> fullData, int bodyBaseOffset)
    {
        int pos = 0;

        // Version (2 bytes)
        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
        hsField.AppendWithCustomText(_HandshakeVersionFieldId,
            FieldValue.NewU64(version), TlsDisplayTables.GetVersionDisplayText(version));
        pos += 2;

        // Random (32 bytes)
        if (pos + 32 > body.Length)
        {
            return;
        }
        hsField.Append(_HandshakeRandomFieldId, FieldValue.NewBytes(fullData.Slice(bodyBaseOffset + pos, 32)));
        pos += 32;

        // Session ID
        if (pos + 1 > body.Length)
        {
            return;
        }
        byte sessionIdLen = body[pos++];
        hsField.Append(_SessionIdLengthFieldId, FieldValue.NewU64(sessionIdLen));

        if (pos + sessionIdLen > body.Length)
        {
            return;
        }
        if (sessionIdLen > 0)
        {
            hsField.Append(_SessionIdFieldId, FieldValue.NewBytes(fullData.Slice(bodyBaseOffset + pos, sessionIdLen)));
        }
        pos += sessionIdLen;

        // Selected Cipher Suite (2 bytes — single suite, not a list)
        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort suite = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
        hsField.AppendWithCustomText(_CipherSuiteFieldId,
            FieldValue.NewU64(suite),
            TlsDisplayTables.GetCipherSuiteDisplayText(suite));
        pos += 2;

        // Compression Method (1 byte — single method)
        if (pos + 1 > body.Length)
        {
            return;
        }
        byte compMethod = body[pos++];
        string methodText = TlsDisplayTables.GetCompressionMethodDisplayText(compMethod);
        hsField.AppendWithCustomText(_CompMethodFieldId,
            FieldValue.NewU64(compMethod), methodText);

        // Extensions (if remaining data)
        if (pos + 2 <= body.Length)
        {
            ushort extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
            hsField.Append(_ExtensionsLengthFieldId, FieldValue.NewU64(extensionsLen));
            pos += 2;

            _ParseExtensions(in hsField, body, ref pos, extensionsLen, fullData, bodyBaseOffset);
        }
    }

    /// <summary>
    /// Parses a TLS Certificate handshake message (type 11).
    /// Format: CertificatesLength(3) → [ CertLength(3) CertData(N) ]*
    /// </summary>
    private void _ParseCertificate(
        in MutField hsField, ReadOnlySpan<byte> body,
        ReadOnlyMemory<byte> fullData, int bodyBaseOffset)
    {
        if (body.Length < 3)
        {
            return;
        }

        // Total certificates length (3 bytes big-endian)
        int certsLength = (body[0] << 16) | (body[1] << 8) | body[2];
        hsField.Append(_CertificatesLengthFieldId, FieldValue.NewU64((ulong)certsLength));

        int pos = 3;
        int end = Math.Min(pos + certsLength, body.Length);
        int certIndex = 0;

        while (pos + 3 <= end)
        {
            // Individual certificate length (3 bytes big-endian)
            int certLen = (body[pos] << 16) | (body[pos + 1] << 8) | body[pos + 2];
            pos += 3;

            if (pos + certLen > end)
            {
                break;
            }

            MutField certField = hsField.AppendWithCustomText(
                _CertificateFieldId, FieldValue.None,
                ZA.Lazy("Certificate [", certIndex, "] (", certLen, " bytes)"));

            certField.Append(_CertificateLengthFieldId, FieldValue.NewU64((ulong)certLen));

            if (certLen > 0)
            {
                certField.Append(_CertificateDataFieldId,
                    FieldValue.NewBytes(fullData.Slice(bodyBaseOffset + pos, certLen)));
            }

            pos += certLen;
            certIndex++;
        }
    }

    /// <summary>
    /// Parses TLS extensions from a Client Hello or Server Hello.
    /// </summary>
    private void _ParseExtensions(
        in MutField parent, ReadOnlySpan<byte> body, ref int pos, ushort extensionsLen,
        ReadOnlyMemory<byte> fullData, int bodyBaseOffset)
    {
        int extensionsEnd = pos + extensionsLen;
        if (extensionsEnd > body.Length)
        {
            return;
        }

        while (pos + 4 <= extensionsEnd)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(body[(pos + 2)..]);
            pos += 4;

            if (pos + extLen > extensionsEnd)
            {
                break;
            }

            string extName = TlsDisplayTables.GetExtensionTypeName(extType);
            MutField extField = parent.AppendWithCustomText(
                _ExtensionFieldId, FieldValue.None,
                ZA.Lazy("Extension: ", extName));

            extField.AppendWithCustomText(_ExtensionTypeFieldId,
                FieldValue.NewU64(extType),
                TlsDisplayTables.GetExtensionTypeDisplayText(extType));
            extField.Append(_ExtensionLenFieldId, FieldValue.NewU64(extLen));

            if (extLen > 0)
            {
                // Parse known extension types
                ReadOnlySpan<byte> extData = body[pos..(pos + extLen)];
                _ParseExtensionData(in extField, extType, extData, fullData, bodyBaseOffset + pos);
            }

            pos += extLen;
        }
    }

    /// <summary>
    /// Parses data for known TLS extension types (SNI, ALPN).
    /// </summary>
    private void _ParseExtensionData(
        in MutField extField, ushort extType, ReadOnlySpan<byte> extData,
        ReadOnlyMemory<byte> fullData, int dataBaseOffset)
    {
        switch (extType)
        {
            case 0: // server_name (SNI)
                _ParseSni(in extField, extData);
                break;
            case 10: // supported_groups
                _ParseSupportedGroups(in extField, extData);
                break;
            case 13: // signature_algorithms
                _ParseSignatureAlgorithms(in extField, extData);
                break;
            case 16: // ALPN
                _ParseAlpn(in extField, extData);
                break;
            case 43: // supported_versions
                _ParseSupportedVersions(in extField, extData);
                break;
            case 51: // key_share
                _ParseKeyShare(in extField, extData, fullData, dataBaseOffset);
                break;
            default:
                // Store raw extension data for unknown types
                if (extData.Length > 0)
                {
                    extField.Append(_ExtensionDataFieldId,
                        FieldValue.NewBytes(fullData.Slice(dataBaseOffset, extData.Length)));
                }
                break;
        }
    }

    /// <summary>
    /// Parses the Server Name Indication (SNI) extension.
    /// Format: ServerNameList(2) → [ NameType(1) HostNameLength(2) HostName(N) ]*
    /// </summary>
    private void _ParseSni(in MutField extField, ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return;
        }

        // ServerNameList length (2 bytes)
        int pos = 2;
        while (pos + 3 <= data.Length)
        {
            byte nameType = data[pos++];
            ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;

            if (pos + nameLen > data.Length)
            {
                break;
            }

            // nameType 0 = host_name
            if (nameType == 0)
            {
                string serverName = Encoding.ASCII.GetString(data[pos..(pos + nameLen)]);
                extField.Append(_SniFieldId, FieldValue.NewString(serverName));
            }

            pos += nameLen;
        }
    }

    /// <summary>
    /// Parses the Application-Layer Protocol Negotiation (ALPN) extension.
    /// Format: ALPNProtocolList(2) → [ StringLength(1) ProtocolName(N) ]*
    /// </summary>
    private void _ParseAlpn(in MutField extField, ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        // ALPN Protocol List length (2 bytes)
        int pos = 2;
        while (pos + 1 <= data.Length)
        {
            byte protoLen = data[pos++];
            if (pos + protoLen > data.Length)
            {
                break;
            }

            string protocol = Encoding.ASCII.GetString(data[pos..(pos + protoLen)]);
            extField.Append(_AlpnFieldId, FieldValue.NewString(protocol));
            pos += protoLen;
        }
    }

    /// <summary>Parses a TLS Alert record (2 bytes: level + description).</summary>
    private void _ParseAlert(in MutField recordField, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return;
        }

        byte level = payload[0];
        byte description = payload[1];

        recordField.AppendWithCustomText(_AlertLevelFieldId,
            FieldValue.NewU64(level),
            TlsDisplayTables.GetAlertLevelDisplayText(level));
        recordField.AppendWithCustomText(_AlertDescFieldId,
            FieldValue.NewU64(description),
            TlsDisplayTables.GetAlertDescriptionDisplayText(description));
    }

    /// <summary>
    /// Parses the supported_groups extension (type 10, RFC 8422).
    /// Format: NamedGroupList(2) → [ NamedGroup(2) ]*
    /// </summary>
    private void _ParseSupportedGroups(in MutField extField, ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int listLen = BinaryPrimitives.ReadUInt16BigEndian(data);
        int pos = 2;
        int end = Math.Min(pos + listLen, data.Length);

        while (pos + 2 <= end)
        {
            ushort group = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            extField.AppendWithCustomText(_SupportedGroupFieldId,
                FieldValue.NewU64(group),
                TlsDisplayTables.GetSupportedGroupDisplayText(group));
            pos += 2;
        }
    }

    /// <summary>
    /// Parses the signature_algorithms extension (type 13, RFC 8446).
    /// Format: SignatureSchemeList(2) → [ SignatureScheme(2) ]*
    /// </summary>
    private void _ParseSignatureAlgorithms(in MutField extField, ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int listLen = BinaryPrimitives.ReadUInt16BigEndian(data);
        int pos = 2;
        int end = Math.Min(pos + listLen, data.Length);

        while (pos + 2 <= end)
        {
            ushort algo = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            extField.AppendWithCustomText(_SigAlgFieldId,
                FieldValue.NewU64(algo),
                TlsDisplayTables.GetSignatureAlgorithmDisplayText(algo));
            pos += 2;
        }
    }

    /// <summary>
    /// Parses the supported_versions extension (type 43, RFC 8446).
    /// Client Hello format: ListLength(1) → [ ProtocolVersion(2) ]*
    /// Server Hello format: ProtocolVersion(2)
    /// </summary>
    private void _ParseSupportedVersions(in MutField extField, ReadOnlySpan<byte> data)
    {
        if (data.Length == 2)
        {
            // Server Hello: single version selected
            ushort version = BinaryPrimitives.ReadUInt16BigEndian(data);
            extField.AppendWithCustomText(_SupportedVersionFieldId,
                FieldValue.NewU64(version),
                TlsDisplayTables.GetVersionDisplayText(version));
            return;
        }

        if (data.Length < 1)
        {
            return;
        }

        // Client Hello: list of versions
        byte listLen = data[0];
        int pos = 1;
        int end = Math.Min(pos + listLen, data.Length);

        while (pos + 2 <= end)
        {
            ushort version = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            extField.AppendWithCustomText(_SupportedVersionFieldId,
                FieldValue.NewU64(version),
                TlsDisplayTables.GetVersionDisplayText(version));
            pos += 2;
        }
    }

    /// <summary>
    /// Parses the key_share extension (type 51, RFC 8446).
    /// Client Hello format: ClientShares(2) → [ NamedGroup(2) KeyExchangeLength(2) KeyExchange(N) ]*
    /// Server Hello format: NamedGroup(2) KeyExchangeLength(2) KeyExchange(N)
    /// </summary>
    private void _ParseKeyShare(
        in MutField extField, ReadOnlySpan<byte> data,
        ReadOnlyMemory<byte> fullData, int dataBaseOffset)
    {
        int pos = 0;

        // Detect Client Hello vs Server Hello by checking if data starts with a u16 list length
        // consistent with remaining data. If listLen == data.Length - 2, it's Client Hello.
        if (data.Length >= 2)
        {
            ushort potentialListLen = BinaryPrimitives.ReadUInt16BigEndian(data);
            if (potentialListLen == data.Length - 2)
            {
                // Client Hello: skip list length prefix
                pos = 2;
            }
        }

        while (pos + 4 <= data.Length)
        {
            ushort group = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            ushort keyExLen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            pos += 4;

            if (pos + keyExLen > data.Length)
            {
                break;
            }

            string groupName = TlsDisplayTables.GetSupportedGroupDisplayText(group);
            MutField entry = extField.AppendWithCustomText(
                _KeyShareEntryFieldId, FieldValue.None,
                ZA.Lazy("Key Share Entry: Group: ", groupName, ", Key Exchange length: ", keyExLen));

            entry.AppendWithCustomText(_KeyShareGroupFieldId,
                FieldValue.NewU64(group), groupName);
            entry.Append(_KeyShareKeyExchangeLenFieldId, FieldValue.NewU64(keyExLen));

            if (keyExLen > 0)
            {
                entry.Append(_KeyShareKeyExchangeFieldId,
                    FieldValue.NewBytes(fullData.Slice(dataBaseOffset + pos, keyExLen)));
            }

            pos += keyExLen;
        }
    }
    #endregion

    #region Eager index-group detection

    /// <summary>
    /// Flags for the content-dependent TLS index groups discovered during the eager detection walk.
    /// Each flag mirrors the emission guard of the corresponding field in the lazy populator so the
    /// presence index records a group if and only if materialization would emit a field for it.
    /// </summary>
    private struct TlsGroupFlags
    {
        public bool Handshake;
        public bool CipherSuite;
        public bool Ext;
        public bool Sni;
        public bool Alpn;
        public bool SupportedGroups;
        public bool SigAlgs;
        public bool SupportedVersions;
        public bool KeyShare;
        public bool Cert;
        public bool Alert;
    }

    /// <summary>
    /// Eagerly walks all TLS records, mirroring <see cref="_PopulateTls"/>'s record loop, and sets the
    /// content-dependent group flags. Duplicates the record/handshake/extension walk so the index is
    /// complete and content-consistent without forcing field materialization.
    /// </summary>
    private static void _DetectTlsGroups(ReadOnlySpan<byte> span, ref TlsGroupFlags flags)
    {
        int offset = 0;

        while (offset + TlsRecordHeader.Size <= span.Length)
        {
            if (!TlsRecordHeader.TryParse(span[offset..], out TlsRecordHeader record))
            {
                break;
            }

            int recordPayloadEnd = offset + TlsRecordHeader.Size + record.Length;
            if (recordPayloadEnd > span.Length)
            {
                break;
            }

            int payloadOffset = offset + TlsRecordHeader.Size;
            ReadOnlySpan<byte> payload = span[payloadOffset..recordPayloadEnd];

            switch (record.ContentType)
            {
                case 22: // Handshake
                    _DetectHandshakeRecords(payload, ref flags);
                    break;
                case 21: // Alert — _ParseAlert emits when payload has level + description.
                    if (payload.Length >= 2)
                    {
                        flags.Alert = true;
                    }
                    break;
            }

            offset = recordPayloadEnd;
        }
    }

    /// <summary>Mirrors <see cref="_ParseHandshakeRecords"/> to flag handshake-dependent groups.</summary>
    private static void _DetectHandshakeRecords(ReadOnlySpan<byte> payload, ref TlsGroupFlags flags)
    {
        int offset = 0;

        while (offset + 4 <= payload.Length)
        {
            byte hsType = payload[offset];
            int hsLength = (payload[offset + 1] << 16) | (payload[offset + 2] << 8) | payload[offset + 3];
            offset += 4;

            if (offset + hsLength > payload.Length)
            {
                break;
            }

            // A handshake message field is emitted for every complete message.
            flags.Handshake = true;

            ReadOnlySpan<byte> hsBody = payload[offset..(offset + hsLength)];

            switch (hsType)
            {
                case 1: // Client Hello
                    _DetectClientHello(hsBody, ref flags);
                    break;
                case 2: // Server Hello
                    _DetectServerHello(hsBody, ref flags);
                    break;
                case 11: // Certificate — _ParseCertificate emits the certificates-length field when body >= 3.
                    if (hsBody.Length >= 3)
                    {
                        flags.Cert = true;
                    }
                    break;
            }

            offset += hsLength;
        }
    }

    /// <summary>Mirrors <see cref="_ParseClientHello"/> through the cipher-suite list and extensions.</summary>
    private static void _DetectClientHello(ReadOnlySpan<byte> body, ref TlsGroupFlags flags)
    {
        int pos = 0;

        if (pos + 2 > body.Length)
        {
            return;
        }
        pos += 2; // version

        if (pos + 32 > body.Length)
        {
            return;
        }
        pos += 32; // random

        if (pos + 1 > body.Length)
        {
            return;
        }
        byte sessionIdLen = body[pos++];
        if (pos + sessionIdLen > body.Length)
        {
            return;
        }
        pos += sessionIdLen;

        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
        pos += 2;
        if (pos + cipherSuitesLen > body.Length)
        {
            return;
        }
        int cipherSuitesEnd = pos + cipherSuitesLen;

        // At least one cipher suite is emitted when the list holds a full 2-byte entry.
        if (pos + 2 <= cipherSuitesEnd)
        {
            flags.CipherSuite = true;
        }
        pos = cipherSuitesEnd;

        if (pos + 1 > body.Length)
        {
            return;
        }
        byte compMethodsLen = body[pos++];
        int compMethodsEnd = pos + compMethodsLen;
        if (compMethodsEnd > body.Length)
        {
            return;
        }
        pos = compMethodsEnd;

        if (pos + 2 > body.Length)
        {
            return;
        }
        ushort extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);

        // The extensions-length field (tls.handshake.ext) is emitted once extensions are reached.
        flags.Ext = true;
        pos += 2;

        _DetectExtensions(body, ref pos, extensionsLen, ref flags);
    }

    /// <summary>Mirrors <see cref="_ParseServerHello"/> through the selected cipher suite and extensions.</summary>
    private static void _DetectServerHello(ReadOnlySpan<byte> body, ref TlsGroupFlags flags)
    {
        int pos = 0;

        if (pos + 2 > body.Length)
        {
            return;
        }
        pos += 2; // version

        if (pos + 32 > body.Length)
        {
            return;
        }
        pos += 32; // random

        if (pos + 1 > body.Length)
        {
            return;
        }
        byte sessionIdLen = body[pos++];
        if (pos + sessionIdLen > body.Length)
        {
            return;
        }
        pos += sessionIdLen;

        if (pos + 2 > body.Length)
        {
            return;
        }
        // A single selected cipher suite is emitted.
        flags.CipherSuite = true;
        pos += 2;

        if (pos + 1 > body.Length)
        {
            return;
        }
        pos += 1; // compression method

        if (pos + 2 <= body.Length)
        {
            ushort extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
            flags.Ext = true;
            pos += 2;

            _DetectExtensions(body, ref pos, extensionsLen, ref flags);
        }
    }

    /// <summary>Mirrors <see cref="_ParseExtensions"/>, flagging the extension group and per-extension groups.</summary>
    private static void _DetectExtensions(
        ReadOnlySpan<byte> body, ref int pos, ushort extensionsLen, ref TlsGroupFlags flags)
    {
        int extensionsEnd = pos + extensionsLen;
        if (extensionsEnd > body.Length)
        {
            return;
        }

        while (pos + 4 <= extensionsEnd)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(body[pos..]);
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(body[(pos + 2)..]);
            pos += 4;

            if (pos + extLen > extensionsEnd)
            {
                break;
            }

            // Every complete extension emits an extension field (tls.handshake.ext).
            flags.Ext = true;

            if (extLen > 0)
            {
                _DetectExtensionData(extType, body[pos..(pos + extLen)], ref flags);
            }

            pos += extLen;
        }
    }

    /// <summary>Mirrors <see cref="_ParseExtensionData"/>, flagging the group of each known extension.</summary>
    private static void _DetectExtensionData(ushort extType, ReadOnlySpan<byte> extData, ref TlsGroupFlags flags)
    {
        switch (extType)
        {
            case 0 when _DetectSni(extData): // server_name (SNI)
                flags.Sni = true;
                break;
            case 10 when _DetectListHasEntry(extData): // supported_groups
                flags.SupportedGroups = true;
                break;
            case 13 when _DetectListHasEntry(extData): // signature_algorithms
                flags.SigAlgs = true;
                break;
            case 16 when _DetectAlpn(extData): // ALPN
                flags.Alpn = true;
                break;
            case 43 when _DetectSupportedVersions(extData): // supported_versions
                flags.SupportedVersions = true;
                break;
            case 51 when _DetectKeyShare(extData): // key_share
                flags.KeyShare = true;
                break;
        }
    }

    /// <summary>Mirrors <see cref="_ParseSni"/>: true when at least one host_name entry would be emitted.</summary>
    private static bool _DetectSni(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return false;
        }

        int pos = 2;
        while (pos + 3 <= data.Length)
        {
            byte nameType = data[pos++];
            ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;

            if (pos + nameLen > data.Length)
            {
                break;
            }

            if (nameType == 0)
            {
                return true;
            }

            pos += nameLen;
        }

        return false;
    }

    /// <summary>Mirrors <see cref="_ParseAlpn"/>: true when at least one protocol name would be emitted.</summary>
    private static bool _DetectAlpn(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        int pos = 2;
        while (pos + 1 <= data.Length)
        {
            byte protoLen = data[pos++];
            if (pos + protoLen > data.Length)
            {
                break;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Mirrors the 2-byte-list parsers (<see cref="_ParseSupportedGroups"/>, <see cref="_ParseSignatureAlgorithms"/>):
    /// true when the length-prefixed list holds at least one full 2-byte entry.
    /// </summary>
    private static bool _DetectListHasEntry(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        int listLen = BinaryPrimitives.ReadUInt16BigEndian(data);
        int end = Math.Min(2 + listLen, data.Length);
        return 2 + 2 <= end;
    }

    /// <summary>Mirrors <see cref="_ParseSupportedVersions"/>: true when at least one version would be emitted.</summary>
    private static bool _DetectSupportedVersions(ReadOnlySpan<byte> data)
    {
        if (data.Length == 2)
        {
            return true;
        }

        if (data.Length < 1)
        {
            return false;
        }

        byte listLen = data[0];
        int end = Math.Min(1 + listLen, data.Length);
        return 1 + 2 <= end;
    }

    /// <summary>Mirrors <see cref="_ParseKeyShare"/>: true when at least one key-share entry would be emitted.</summary>
    private static bool _DetectKeyShare(ReadOnlySpan<byte> data)
    {
        int pos = 0;

        if (data.Length >= 2)
        {
            ushort potentialListLen = BinaryPrimitives.ReadUInt16BigEndian(data);
            if (potentialListLen == data.Length - 2)
            {
                pos = 2;
            }
        }

        while (pos + 4 <= data.Length)
        {
            ushort keyExLen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            pos += 4;

            if (pos + keyExLen > data.Length)
            {
                break;
            }

            return true;
        }

        return false;
    }

    #endregion
}
