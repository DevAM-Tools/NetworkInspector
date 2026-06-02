// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Datagram Transport Layer Security (DTLS) protocol parser (RFC 6347/9147).
/// Similar to TLS but over UDP with epoch and sequence number in the record header.
/// Reuses TLS display tables for content types, versions, and handshake types.
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
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
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
    private const string DtlsIndexGroup = "dtls";

    #endregion

    #region Protocol container

    [BytesField("dtls", "DTLS", IndexGroup = DtlsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Record Layer fields

    [NoneField("dtls.record", "DTLS Record Layer", IndexGroup = DtlsIndexGroup)]
    private FieldId _RecordFieldId;

    [U64Field("dtls.record.content_type", "Content Type", IndexGroup = DtlsIndexGroup)]
    private FieldId _ContentTypeFieldId;

    [U64Field("dtls.record.version", "Version", IndexGroup = DtlsIndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("dtls.record.epoch", "Epoch", IndexGroup = DtlsIndexGroup)]
    private FieldId _EpochFieldId;

    [U64Field("dtls.record.sequence_number", "Sequence Number", IndexGroup = DtlsIndexGroup)]
    private FieldId _SeqNumFieldId;

    [U64Field("dtls.record.length", "Length", IndexGroup = DtlsIndexGroup)]
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

    // Pre-allocated delegate; wired once in OnStartCustom (build phase, not per packet).
    private LazyPopulator _Populator = null!;

    partial void OnStartCustom(Stack stack) => _Populator = PopulateDtls;

    /// <summary>
    /// Parses DTLS records from the UDP payload. Record-level summary is eager;
    /// handshake content is lazy via the protocol container.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < DtlsRecordHeader.Size)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DtlsRecordHeader.Size, (ulong)data.Length);
        }

        if (!DtlsRecordHeader.TryParse(data.Span, out DtlsRecordHeader firstRecord))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, DtlsRecordHeader.Size, (ulong)data.Length);
        }

        // Verify it looks like a valid DTLS record
        if (!firstRecord.IsValidContentType())
        {
            return ParseError.InvalidData(ProtocolName, "Invalid DTLS content type");
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DtlsGroupId);

        // Build summary from first record
        string ctText = TlsDisplayTables.GetContentTypeDisplayText(firstRecord.ContentType);
        string verText = GetDtlsVersionText(firstRecord.Version);

        LazyString summary = ZA.Lazy("DTLS Record Layer: ", ctText, " (", verText, ")");

        parentField.SetPacketInfo(ZA.Lazy("DTLS ", ctText));

        // Eagerly scan every DTLS record in the datagram to decide the handshake-related
        // presence groups. The lazy populator emits dtls.handshake fields for any record with
        // content type 22 (and ≥ 12 bytes of handshake header), and dtls.handshake.fragment
        // fields only when a handshake message is fragmented. Replicating that walk here keeps
        // the presence index complete and content-consistent (no false positives or negatives).
        DetectHandshakeGroups(data.Span, out bool hasHandshake, out bool hasHandshakeFragment);
        if (hasHandshake)
        {
            context.RecordGroupPresence(_DtlsHandshakeGroupId);
        }
        if (hasHandshakeFragment)
        {
            context.RecordGroupPresence(_DtlsHandshakeFragmentGroupId);
        }

        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Scans every DTLS record in the datagram to determine the handshake presence groups,
    /// mirroring the record/handshake walk performed by the lazy populator. <paramref name="hasHandshake"/>
    /// is set when any record carries a parseable handshake header (content type 22 with at least the
    /// 12-byte DTLS handshake header); <paramref name="hasHandshakeFragment"/> is set when any such
    /// handshake message is fragmented (fragment offset &gt; 0 or fragment length &lt; total length).
    /// This duplicate walk is the cost of recording content-dependent groups eagerly while keeping
    /// the actual field emission lazy.
    /// </summary>
    private static void DetectHandshakeGroups(ReadOnlySpan<byte> span, out bool hasHandshake, out bool hasHandshakeFragment)
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

            if (!record.IsValidContentType())
            {
                break;
            }

            int recordStart = offset + DtlsRecordHeader.Size;
            int recordEnd = Math.Min(recordStart + record.Length, span.Length);

            if (record.ContentType == 22 && recordEnd > recordStart)
            {
                ReadOnlySpan<byte> hsData = span[recordStart..recordEnd];

                // ParseHandshake emits handshake fields only when the full 12-byte handshake header fits.
                if (hsData.Length >= 12)
                {
                    hasHandshake = true;

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
    /// </summary>
    private ParseResult PopulateDtls(in MutField container)
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

            string ctText = TlsDisplayTables.GetContentTypeDisplayText(record.ContentType);
            string verText = GetDtlsVersionText(record.Version);

            MutField recordContainer = container.AppendWithCustomText(
                _RecordFieldId, FieldValue.None,
                ZA.Lazy("DTLS Record Layer: ", ctText, " (", verText, ")"));

            recordContainer.AppendWithCustomText(_ContentTypeFieldId,
                FieldValue.NewU64(record.ContentType), ctText);
            recordContainer.AppendWithCustomText(_VersionFieldId,
                FieldValue.NewU64(record.Version), verText);
            recordContainer.Append(_EpochFieldId, FieldValue.NewU64(record.Epoch));
            recordContainer.Append(_SeqNumFieldId, FieldValue.NewU64(record.SequenceNumber));
            recordContainer.Append(_LengthFieldId, FieldValue.NewU64(record.Length));

            int recordStart = offset + DtlsRecordHeader.Size;
            int recordEnd = Math.Min(recordStart + record.Length, span.Length);

            if (record.ContentType == 22 && recordEnd > recordStart)
            {
                ParseHandshake(in container, span[recordStart..recordEnd]);
            }

            offset = recordEnd;
        }

        return 0;
    }

    /// <summary>
    /// Parses a DTLS handshake message header (type + length).
    /// </summary>
    private void ParseHandshake(in MutField container, ReadOnlySpan<byte> hsData)
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
            ZA.Lazy("Handshake Protocol: ", hsTypeText));

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

        // Detect fragmentation: a message is fragmented if offset > 0 or fragment_length < total length
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
    /// Returns display text for DTLS version codes.
    /// DTLS uses inverted version numbers: 0xFEFF = DTLS 1.0, 0xFEFD = DTLS 1.2.
    /// </summary>
    private static string GetDtlsVersionText(ushort version) =>
        version switch
        {
            0xFEFF => "DTLS 1.0",
            0xFEFD => "DTLS 1.2",
            _ => (string)ZA.String("Unknown (", Helpers.DisplayTables.FormatHexU16(version), ")")
        };
    #endregion
}
