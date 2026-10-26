// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Datagram Transport Layer Security (DTLS) protocol parser (RFC 6347/9147).
/// Similar to TLS but over UDP with epoch and sequence number in the record header.
/// Reuses TLS display tables for content types and handshake types.
/// Decodes the plaintext record layer of DTLS 1.0 / 1.2 (13-byte record header, 12-byte handshake header); DTLS 1.3
/// records with the unified header (RFC 9147) are rejected because their first byte is not a content type.
/// Handshake headers are decoded only for records of epoch 0; later epochs are normally encrypted, so their record
/// header is shown without handshake fields. A connection ID record (content type 25, RFC 9146) has a header of variable
/// length: its first four header fields are shown, its length field is not, and the record walk ends there.
/// Payloads are never decrypted or reassembled.
/// <para>Field tree structure:</para>
/// <code>
/// dtls: DTLS
/// ├── dtls.record: DTLS Record Layer
/// │   ├── dtls.record.content_type: 22 (Handshake)
/// │   ├── dtls.record.version: 0xFEFD (DTLS 1.2)
/// │   ├── dtls.record.epoch: 0
/// │   ├── dtls.record.sequence_number: 0
/// │   └── dtls.record.length: 200
/// └── dtls.handshake: Handshake Protocol
///     ├── dtls.handshake.type: 1 (Client Hello)
///     ├── dtls.handshake.length: 196
///     ├── dtls.handshake.message_seq: 0
///     ├── dtls.handshake.fragment_offset: 0
///     └── dtls.handshake.fragment_length: 196
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("dtls", "Datagram Transport Layer Security", Description = "DTLS (RFC 6347/9147)")]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortKey)]
public sealed partial class DtlsProtocol : IProtocol
{
    #region Constants

    /// <summary>UDP port for DTLS (same as HTTPS, but over UDP).</summary>
    public const ulong UdpPortKey = 443;

    /// <summary>Index group for always-present DTLS fields.</summary>
    private const string _DtlsIndexGroup = "dtls";

    /// <summary>Record content type of a handshake record (22).</summary>
    private const byte _ContentTypeHandshake = 22;

    /// <summary>
    /// Record content type of a DTLS 1.2 record with a connection ID (25, tls12_cid, RFC 9146). Its header carries a
    /// connection ID of negotiated length before the length field, so it does not have the 13-byte layout.
    /// </summary>
    private const byte _ContentTypeConnectionId = 25;

    #endregion

    #region Protocol container

    [BytesField("dtls", "DTLS", IndexGroup = _DtlsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Record Layer fields

    [NoneField("dtls.record", "DTLS Record Layer", IndexGroup = _DtlsIndexGroup)]
    private FieldId _RecordFieldId;

    [U64Field("dtls.record.content_type", "Content Type", IndexGroup = _DtlsIndexGroup)]
    private FieldId _ContentTypeFieldId;

    [U64Field("dtls.record.version", "Version", IndexGroup = _DtlsIndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("dtls.record.epoch", "Epoch", IndexGroup = _DtlsIndexGroup)]
    private FieldId _EpochFieldId;

    [U64Field("dtls.record.sequence_number", "Sequence Number", IndexGroup = _DtlsIndexGroup)]
    private FieldId _SeqNumFieldId;

    [U64Field("dtls.record.length", "Length", IndexGroup = _DtlsIndexGroup)]
    private FieldId _LengthFieldId;

    #endregion

    #region Handshake fields (conditional)

    [NoneField("dtls.handshake", "Handshake Protocol", IndexGroup = "dtls.handshake")]
    private FieldId _HandshakeFieldId;

    [U64Field("dtls.handshake.type", "Handshake Type", IndexGroup = "dtls.handshake")]
    private FieldId _HandshakeTypeFieldId;

    [U64Field("dtls.handshake.length", "Length", IndexGroup = "dtls.handshake")]
    private FieldId _HandshakeLengthFieldId;

    [U64Field("dtls.handshake.message_seq", "Message Sequence", IndexGroup = "dtls.handshake")]
    private FieldId _HandshakeMsgSeqFieldId;

    [U64Field("dtls.handshake.fragment_offset", "Fragment Offset", IndexGroup = "dtls.handshake")]
    private FieldId _HandshakeFragOffsetFieldId;

    [U64Field("dtls.handshake.fragment_length", "Fragment Length", IndexGroup = "dtls.handshake")]
    private FieldId _HandshakeFragLengthFieldId;

    [BoolField("dtls.handshake.is_fragment", "Is Fragment", IndexGroup = "dtls.handshake.fragment")]
    private FieldId _HandshakeIsFragmentFieldId;

    [StringField("dtls.handshake.reassembly_status", "Reassembly Status", IndexGroup = "dtls.handshake.fragment")]
    private FieldId _HandshakeReassemblyStatusFieldId;

    #endregion

    #region Populators

    // Pre-allocated delegate; wired once in _OnStartCustom (build phase, not per packet). Converting the instance method
    // group to a delegate allocates on every conversion, so doing it per packet would add garbage on the parse path.
    private LazyPopulator _Populator = null!; // null-forgiving: always assigned in _OnStartCustom, which runs before the first Parse

    /// <summary>Creates the shared lazy populator delegate once, after the stack is built.</summary>
    partial void _OnStartCustom(Stack stack) => _Populator = _PopulateDtls;

    /// <summary>
    /// Parses DTLS records from the UDP payload. Record-level summary is eager;
    /// handshake content is lazy via the protocol container.
    /// </summary>
    /// <param name="parentField">Parent field that receives the DTLS container.</param>
    /// <param name="data">The UDP payload: one or more DTLS records.</param>
    /// <param name="context">Parse context; used to record protocol and index-group presence.</param>
    /// <returns>
    /// The whole <paramref name="data"/> length, or a <see cref="ParseError"/> when the payload is shorter than a
    /// record header or its first byte is not a DTLS content type (this is how non-DTLS traffic on the same port is
    /// rejected). Only the first record is validated here; later records are validated when the tree is populated.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < DtlsRecordHeader.Size)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DtlsRecordHeader.Size, (ulong)data.Length);
        }

        // Cannot fail after the length check above; kept because TryParse is the single place that decodes the header.
        if (!DtlsRecordHeader.TryParse(data.Span, out DtlsRecordHeader firstRecord))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DtlsRecordHeader.Size, (ulong)data.Length);
        }

        // Verify it looks like a valid DTLS record. The version and length are deliberately not checked here.
        if (!firstRecord.IsValidContentType())
        {
            return ParseError.InvalidData(ProtocolName, "Invalid DTLS content type");
        }

        // Presence is recorded only after the payload was accepted as DTLS.
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DtlsGroupId);

        // Build summary from first record (the packet info and container title name only the first record of the datagram)
        string ctText = _GetContentTypeText(firstRecord.ContentType);
        string verText = _GetDtlsVersionText(firstRecord.Version);

        parentField.SetPacketInfo(ZA.Lazy("DTLS ", ctText));

        // Eagerly scan every DTLS record in the datagram to decide the handshake-related
        // presence groups. The lazy populator emits dtls.handshake fields for any record with
        // content type 22 (and ≥ 12 bytes of handshake header), and dtls.handshake.fragment
        // fields only when a handshake message is fragmented. Replicating that walk here keeps
        // the presence index complete and content-consistent (no false positives or negatives).
        _DetectHandshakeGroups(data.Span, out bool hasHandshake, out bool hasHandshakeFragment);
        if (hasHandshake)
        {
            context.RecordGroupPresence(_DtlsHandshakeGroupId);
        }
        if (hasHandshakeFragment)
        {
            context.RecordGroupPresence(_DtlsHandshakeFragmentGroupId);
        }

        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), "DTLS Record Layer: ", ctText, " (", verText, ")", _Populator);

        return data.Length;
    }

    /// <summary>
    /// Scans every DTLS record in the datagram to determine the handshake presence groups,
    /// mirroring the record/handshake walk performed by the lazy populator. <paramref name="hasHandshake"/>
    /// is set when any record carries a parseable handshake header (content type 22 with at least the
    /// 12-byte DTLS handshake header); <paramref name="hasHandshakeFragment"/> is set when any such
    /// handshake message is fragmented (fragment offset &gt; 0 or fragment length &lt; total length).
    /// This duplicate walk is the cost of recording content-dependent groups eagerly while keeping
    /// the actual field emission lazy. The walk must happen in <see cref="Parse"/> because a lazy populator is not
    /// allowed to touch the packet index; it must apply exactly the same record boundaries and header conditions as
    /// <see cref="_PopulateDtls"/> and <see cref="_ParseHandshake"/>, otherwise the index and the tree disagree.
    /// Work is bounded by the datagram size: every iteration advances by at least the 13-byte record header.
    /// </summary>
    private static void _DetectHandshakeGroups(ReadOnlySpan<byte> span, out bool hasHandshake, out bool hasHandshakeFragment)
    {
        hasHandshake = false;
        hasHandshakeFragment = false;

        int offset = 0;
        while (offset + DtlsRecordHeader.Size <= span.Length)
        {
            if (!DtlsRecordHeader.TryParse(span[offset..], out DtlsRecordHeader record))
            {
                break;
            }

            // A connection ID record has a different header, so its length field and the following record boundaries are
            // unknown; the walk ends here exactly as in _PopulateDtls.
            if (!record.IsValidContentType() || record.ContentType == _ContentTypeConnectionId)
            {
                break;
            }

            // The declared record length is untrusted: clamp it to the datagram so a lying length can neither read past
            // the data nor skip the rest of the datagram (a truncated last record is still inspected). The int sum
            // cannot overflow (span length plus a 16-bit length). The same clamp is applied in _PopulateDtls.
            int recordStart = offset + DtlsRecordHeader.Size;
            int recordEnd = Math.Min(recordStart + record.Length, span.Length);

            // Only a record of epoch 0 is plaintext; later epochs are encrypted (same rule as in _PopulateDtls).
            if (record.ContentType == _ContentTypeHandshake && record.Epoch == 0 && recordEnd > recordStart)
            {
                ReadOnlySpan<byte> hsData = span[recordStart..recordEnd];

                // _ParseHandshake emits handshake fields only when the full 12-byte handshake header fits.
                if (hsData.Length >= 12)
                {
                    hasHandshake = true;

                    // 24-bit big-endian fields assembled by hand (no 3-byte reader exists); same offsets as
                    // _ParseHandshake: length at 1, fragment offset at 6, fragment length at 9 (RFC 6347 §4.2.2).
                    uint hsLength = (uint)((hsData[1] << 16) | (hsData[2] << 8) | hsData[3]);
                    uint fragOffset = (uint)((hsData[6] << 16) | (hsData[7] << 8) | hsData[8]);
                    uint fragLength = (uint)((hsData[9] << 16) | (hsData[10] << 8) | hsData[11]);

                    if (fragOffset > 0 || fragLength < hsLength)
                    {
                        hasHandshakeFragment = true;
                    }
                }
            }

            offset = recordEnd;
        }
    }

    /// <summary>
    /// Lazy populator: per-record containers with record-header fields, plus handshake
    /// messages for all DTLS records in the datagram. Fires on first access of the DTLS
    /// container's children. The <c>dtls.record</c> containers and the handshake
    /// sub-fields are siblings under the <c>dtls</c> container.
    /// May run after <see cref="Parse"/> has returned, so it uses only the container's bytes and the read-only field
    /// IDs. It stops at the first record that has no valid content type, like the eager detector, and returns 0
    /// (success) in that case: trailing garbage after valid records is not reported as an error.
    /// </summary>
    private ParseResult _PopulateDtls(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> dtlsData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        ReadOnlySpan<byte> span = dtlsData.Span;
        int offset = 0;

        while (offset + DtlsRecordHeader.Size <= span.Length)
        {
            if (!DtlsRecordHeader.TryParse(span[offset..], out DtlsRecordHeader record))
            {
                break;
            }

            if (!record.IsValidContentType())
            {
                break;
            }

            string ctText = _GetContentTypeText(record.ContentType);
            string verText = _GetDtlsVersionText(record.Version);

            MutField recordContainer = container.AppendWithCustomText(
                _RecordFieldId, FieldValue.None,
                "DTLS Record Layer: ", ctText, " (", verText, ")");

            recordContainer.AppendWithCustomText(_ContentTypeFieldId,
                FieldValue.NewU64(record.ContentType), ctText);
            recordContainer.AppendWithCustomText(_VersionFieldId,
                FieldValue.NewU64(record.Version), verText);
            recordContainer.Append(_EpochFieldId, FieldValue.NewU64(record.Epoch));
            recordContainer.Append(_SeqNumFieldId, FieldValue.NewU64(record.SequenceNumber));

            // A connection ID record (RFC 9146) has a connection ID of negotiated length between the sequence number and
            // the length field, so the 13-byte layout does not apply: the two bytes read as "length" would be part of the
            // connection ID. The fields before it are valid, the length is not shown, and the walk ends because neither
            // the end of this record nor the start of the next one can be found without the negotiated length.
            if (record.ContentType == _ContentTypeConnectionId)
            {
                break;
            }

            recordContainer.Append(_LengthFieldId, FieldValue.NewU64(record.Length));

            int recordStart = offset + DtlsRecordHeader.Size;
            int recordEnd = Math.Min(recordStart + record.Length, span.Length);

            // Only a record of epoch 0 is plaintext. From the first key change on (epoch 1 and later) the record
            // payload is encrypted, so its first bytes are ciphertext and decoding them as a handshake header would show
            // meaningless fields. The record header is still shown.
            if (record.ContentType == _ContentTypeHandshake && record.Epoch == 0 && recordEnd > recordStart)
            {
                _ParseHandshake(in container, span[recordStart..recordEnd]);
            }

            offset = recordEnd;
        }

        return 0;
    }

    /// <summary>
    /// Parses a DTLS handshake message header (type + length + fragmentation fields, RFC 6347 §4.2.2).
    /// Only the header is decoded; the message body is not shown. A record shorter than the 12-byte header yields
    /// no handshake fields, which is the condition <see cref="_DetectHandshakeGroups"/> mirrors.
    /// </summary>
    private void _ParseHandshake(in MutField container, ReadOnlySpan<byte> hsData)
    {
        // DTLS handshake header: type(1) + length(3) + message_seq(2) + fragment_offset(3) + fragment_length(3) = 12 bytes
        if (hsData.Length < 12)
        {
            return;
        }

        byte hsType = hsData[0];
        uint hsLength = (uint)((hsData[1] << 16) | (hsData[2] << 8) | hsData[3]);

        string hsTypeText = TlsDisplayTables.GetHandshakeTypeDisplayText(hsType);

        MutField hsContainer = container.AppendWithCustomText(
            _HandshakeFieldId, FieldValue.None,
            "Handshake Protocol: ", hsTypeText);

        hsContainer.AppendWithCustomText(_HandshakeTypeFieldId,
            FieldValue.NewU64(hsType), hsTypeText);

        hsContainer.Append(_HandshakeLengthFieldId, FieldValue.NewU64(hsLength));

        // DTLS-specific fields: message sequence and fragment info
        ushort msgSeq = BinaryPrimitives.ReadUInt16BigEndian(hsData[4..6]);
        uint fragOffset = (uint)((hsData[6] << 16) | (hsData[7] << 8) | hsData[8]);
        uint fragLength = (uint)((hsData[9] << 16) | (hsData[10] << 8) | hsData[11]);

        hsContainer.Append(_HandshakeMsgSeqFieldId, FieldValue.NewU64(msgSeq));
        hsContainer.Append(_HandshakeFragOffsetFieldId, FieldValue.NewU64(fragOffset));
        hsContainer.Append(_HandshakeFragLengthFieldId, FieldValue.NewU64(fragLength));

        // Detect fragmentation: a message is fragmented if offset > 0 or fragment_length < total length.
        // This is the same condition as in _DetectHandshakeGroups (it decides the dtls.handshake.fragment group).
        // fragOffset + fragLength below is at most 2 * (2^24 - 1), so the uint sum cannot overflow.
        bool isFragment = fragOffset > 0 || fragLength < hsLength;
        if (isFragment)
        {
            hsContainer.Append(_HandshakeIsFragmentFieldId, FieldValue.NewBool(true));

            // Provide fragment position information
            if (fragOffset == 0)
            {
                hsContainer.Append(_HandshakeReassemblyStatusFieldId,
                    FieldValue.NewString("First fragment"));
            }
            else if (fragOffset + fragLength >= hsLength)
            {
                hsContainer.Append(_HandshakeReassemblyStatusFieldId,
                    FieldValue.NewString("Last fragment"));
            }
            else
            {
                hsContainer.Append(_HandshakeReassemblyStatusFieldId,
                    FieldValue.NewString("Middle fragment"));
            }
        }
    }

    /// <summary>
    /// Display text of a DTLS content type. The TLS table is reused, except for 25: in DTLS 1.2 it is the connection ID
    /// record (RFC 9146), which the TLS table does not know.
    /// </summary>
    private static string _GetContentTypeText(byte contentType) =>
        contentType == _ContentTypeConnectionId
            ? "Connection ID (25)"
            : TlsDisplayTables.GetContentTypeDisplayText(contentType);

    /// <summary>
    /// Returns display text for DTLS version codes.
    /// DTLS uses inverted version numbers: 0xFEFF = DTLS 1.0, 0xFEFD = DTLS 1.2.
    /// </summary>
    private static string _GetDtlsVersionText(ushort version) =>
        version switch
        {
            0xFEFF => "DTLS 1.0",
            0xFEFD => "DTLS 1.2",
            _ => (string)ZA.String("Unknown (", Helpers.DisplayTables.FormatHexU16(version), ")")
        };
    #endregion
}
