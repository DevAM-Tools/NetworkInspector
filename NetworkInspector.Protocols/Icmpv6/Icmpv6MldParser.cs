// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Index groups an MLD message emits; the result of <see cref="Icmpv6MldParser.DetectGroups"/>.
/// </summary>
[Flags]
internal enum MldGroups
{
    /// <summary>No MLD field is emitted.</summary>
    None = 0,

    /// <summary>The multicast address (and, for MLDv1, the maximum response delay) is emitted.</summary>
    Mld = 1,

    /// <summary>The MLDv2 query fields (response code, flags, QQI, source count, sources) are emitted.</summary>
    QueryV2 = 2,

    /// <summary>The MLDv2 report fields (record count and records) are emitted.</summary>
    Report = 4,
}

/// <summary>
/// Parses ICMPv6 Multicast Listener Discovery messages: MLDv1 query, report and done (RFC 2710, types 130 to 132),
/// the MLDv2 query (RFC 3810, type 130 with 28 or more bytes) and the MLDv2 report (type 143).
/// <para>
/// <b>Why this is a parser of its own:</b> the three layouts share a type number (130) and are told apart only by the
/// message length, so the decision "which fields exist" has to be made twice, once eagerly for the index groups and
/// once lazily for the fields. Keeping both decisions next to each other in one class (<see cref="DetectGroups"/>
/// and <see cref="Parse"/>) is what keeps the presence index free of false positives; a copy in the protocol class
/// would drift. Older code decoded every query as MLDv1 and showed the raw response code of an MLDv2 query as a delay
/// in milliseconds, which is wrong for every code above 32767, and it did not decode reports at all.
/// </para>
/// <para>
/// Thread safety: static and stateless; all input is a read-only span or memory, so concurrent calls are safe.
/// Malformed input is never an error here: a record that does not fit in the message ends the walk, and the fields
/// shown so far remain useful (the declared counts stay visible, so a cut message is recognisable).
/// </para>
/// </summary>
internal static class Icmpv6MldParser
{
    #region Constants

    /// <summary>MLD message type: Query (v1 and v2, RFC 2710 / RFC 3810).</summary>
    internal const byte TypeQuery = 130;

    /// <summary>MLD message type: Version 1 Report.</summary>
    internal const byte TypeReportV1 = 131;

    /// <summary>MLD message type: Version 1 Done.</summary>
    internal const byte TypeDone = 132;

    /// <summary>MLD message type: Version 2 Report.</summary>
    internal const byte TypeReportV2 = 143;

    /// <summary>Size of an MLDv1 message in bytes: header (4), maximum response delay (2), reserved (2), address (16).</summary>
    private const int _V1Length = 24;

    /// <summary>Smallest MLDv2 query: the MLDv1 message plus flags (1), QQIC (1) and the source count (2).</summary>
    private const int _V2QueryMinLength = 28;

    /// <summary>Smallest MLDv2 report: header (4), reserved (2) and the record count (2).</summary>
    private const int _ReportMinLength = 8;

    /// <summary>Size of the fixed part of a multicast address record: type, aux length, source count, address.</summary>
    private const int _RecordFixedLength = 20;

    /// <summary>Size of one IPv6 address in bytes.</summary>
    private const int _AddressLength = 16;

    #endregion

    #region Public API

    /// <summary>Tells whether a message type is one of the MLD types handled here (130 to 132 and 143).</summary>
    /// <param name="type">The ICMPv6 type byte.</param>
    internal static bool IsMldType(byte type) =>
        type is >= TypeQuery and <= TypeDone or TypeReportV2;

    /// <summary>
    /// Decides which index groups <see cref="Parse"/> will emit a field in. The single place for the length rules:
    /// the eager parse records the groups from it, and <see cref="Parse"/> uses the same constants, so they cannot
    /// disagree.
    /// </summary>
    /// <param name="type">The ICMPv6 type byte.</param>
    /// <param name="length">Length of the whole ICMPv6 message in bytes.</param>
    /// <returns>The groups that contain at least one emitted field; <see cref="MldGroups.None"/> for other types.</returns>
    internal static MldGroups DetectGroups(byte type, int length)
    {
        if (type is >= TypeQuery and <= TypeDone && length >= _V1Length)
        {
            // An MLDv2 query is longer than the 24-byte MLDv1 message and always carries the multicast address too,
            // so it belongs to both groups. Only a query can be v2: reports and done messages keep the v1 length.
            return type == TypeQuery && length >= _V2QueryMinLength
                ? MldGroups.Mld | MldGroups.QueryV2
                : MldGroups.Mld;
        }

        if (type == TypeReportV2 && length >= _ReportMinLength)
        {
            return MldGroups.Report;
        }

        return MldGroups.None;
    }

    /// <summary>
    /// Decodes the Maximum Response Code of an MLDv2 query into milliseconds (RFC 3810 section 5.1.3). Codes below
    /// 32768 are the delay itself; larger codes carry a 3-bit exponent and a 12-bit mantissa.
    /// </summary>
    /// <param name="code">The 16-bit code as transmitted.</param>
    /// <returns>The maximum response delay in milliseconds; at most 8 387 584 for the largest code.</returns>
    internal static uint DecodeMaxResponseCode(ushort code) => code < 0x8000
        ? code
        : (uint)(((code & 0x0FFF) | 0x1000) << (((code & 0x7000) >> 12) + 3));

    /// <summary>
    /// Decodes the Querier's Query Interval Code of an MLDv2 query into seconds (RFC 3810 section 5.1.9). Codes
    /// below 128 are the interval itself; larger codes carry a 3-bit exponent and a 4-bit mantissa.
    /// </summary>
    /// <param name="code">The 8-bit code as transmitted.</param>
    /// <returns>The query interval in seconds.</returns>
    internal static uint DecodeQqic(byte code) => code < 0x80
        ? code
        : (uint)(((code & 0x0F) | 0x10) << (((code & 0x70) >> 4) + 3));

    /// <summary>
    /// Appends the MLD fields of one message to <paramref name="container"/>. The same length rules as
    /// <see cref="DetectGroups"/> decide what is emitted.
    /// </summary>
    /// <param name="container">The ICMPv6 container field that receives the MLD fields.</param>
    /// <param name="message">The whole ICMPv6 message, starting at the type byte.</param>
    /// <param name="type">The ICMPv6 type byte.</param>
    /// <param name="f">The registered MLD field ids.</param>
    internal static void Parse(in MutField container, ReadOnlyMemory<byte> message, byte type, in Icmpv6MldFieldIds f)
    {
        MldGroups groups = DetectGroups(type, message.Length);
        ReadOnlySpan<byte> span = message.Span;

        if ((groups & MldGroups.QueryV2) != 0)
        {
            _ParseQueryV2(in container, message, in f);
            return;
        }

        if ((groups & MldGroups.Mld) != 0)
        {
            // MLDv1 layout: maximum response delay (2 bytes, milliseconds) at 4, reserved at 6, address at 8..23.
            // Only the query uses the delay; a report or done message sends it as zero, but the field exists in
            // every message of this layout, so it is always emitted.
            ushort delay = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
            container.AppendWithCustomText(f.MaxResponseDelay, FieldValue.NewU64(delay), delay, " ms");
            _AppendAddress(in container, f.MulticastAddress, span[8..24]);
            return;
        }

        if ((groups & MldGroups.Report) != 0)
        {
            _ParseReport(in container, message, in f);
        }
    }

    #endregion

    #region Message layouts

    /// <summary>
    /// MLDv2 query (RFC 3810 section 5.1): maximum response code (2), reserved (2), multicast address (16), flags
    /// (1: reserved nibble, S, QRV), QQIC (1), source count (2) and the source addresses.
    /// </summary>
    private static void _ParseQueryV2(in MutField container, ReadOnlyMemory<byte> message, in Icmpv6MldFieldIds f)
    {
        ReadOnlySpan<byte> span = message.Span;

        // The response code is shown raw (so a filter can match the wire value) with the decoded delay as text.
        ushort responseCode = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
        container.AppendWithCustomText(f.MaxResponseCode, FieldValue.NewU64(responseCode), DecodeMaxResponseCode(responseCode), " ms");
        _AppendAddress(in container, f.MulticastAddress, span[8..24]);

        // Flags byte: bits 7-4 reserved, bit 3 S (suppress router-side processing), bits 2-0 QRV. The container has
        // no value of its own; its children carry the data, like the other flag containers of this protocol.
        byte flags = span[24];
        MutField flagsField = container.AppendWithCustomText(f.Flags, FieldValue.None, new LazyString(_FlagsText(flags)));
        flagsField.Append(f.FlagReserved, FieldValue.NewU64((ulong)(flags >> 4)));
        flagsField.Append(f.FlagS, FieldValue.NewBool((flags & 0x08) != 0));
        flagsField.Append(f.FlagQrv, FieldValue.NewU64((ulong)(flags & 0x07)));

        byte qqic = span[25];
        container.AppendWithCustomText(f.Qqi, FieldValue.NewU64(qqic), DecodeQqic(qqic), " s");

        // The declared count stays the wire value even when the message is cut, so a truncated query is visible; only
        // the sources that fit are listed. The loop is bounded by the message length (16 bytes per iteration), not by
        // the attacker-controlled count, so a count of 65535 in a 28-byte message does no extra work.
        ushort sourceCount = BinaryPrimitives.ReadUInt16BigEndian(span[26..28]);
        container.Append(f.NbSources, FieldValue.NewU64(sourceCount));
        int offset = _V2QueryMinLength;
        for (int i = 0; i < sourceCount && offset + _AddressLength <= span.Length; i++)
        {
            _AppendAddress(in container, f.SourceAddress, span.Slice(offset, _AddressLength));
            offset += _AddressLength;
        }
    }

    /// <summary>
    /// MLDv2 report (RFC 3810 section 5.2): reserved (2), record count (2), then that many multicast address
    /// records of 20 + 16 x sources + 4 x auxiliary words bytes each.
    /// </summary>
    private static void _ParseReport(in MutField container, ReadOnlyMemory<byte> message, in Icmpv6MldFieldIds f)
    {
        ReadOnlySpan<byte> span = message.Span;

        ushort recordCount = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
        container.Append(f.ReportRecordCount, FieldValue.NewU64(recordCount));

        // Each record starts right after the previous one. A record that does not fit completely ends the walk:
        // its length fields come from the wire, so decoding half a record would show invented addresses. Every
        // iteration consumes at least 20 bytes, so the loop is bounded by the message length, not by the count.
        int offset = 8;
        for (int i = 0; i < recordCount && offset + _RecordFixedLength <= span.Length; i++)
        {
            byte recordType = span[offset];
            byte auxWords = span[offset + 1];
            ushort sourceCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));

            // Both multipliers come from single bytes / 16-bit values, so the sum is at most 20 + 16 x 65535 + 4 x 255,
            // far inside int range; no overflow is possible.
            int recordLength = _RecordFixedLength + (_AddressLength * sourceCount) + (4 * auxWords);
            if (offset + recordLength > span.Length)
            {
                break;
            }

            MutField record = container.AppendWithCustomText(f.Record, FieldValue.None, new LazyString(_RecordTitle(recordType)));
            record.AppendWithCustomText(f.RecordType, FieldValue.NewU64(recordType), new LazyString(_RecordTypeText(recordType)));
            record.Append(f.RecordAuxDataLength, FieldValue.NewU64(auxWords));
            record.Append(f.RecordNbSources, FieldValue.NewU64(sourceCount));
            _AppendAddress(in record, f.RecordMulticastAddress, span.Slice(offset + 4, _AddressLength));

            int sourceOffset = offset + _RecordFixedLength;
            for (int s = 0; s < sourceCount; s++)
            {
                _AppendAddress(in record, f.RecordSourceAddress, span.Slice(sourceOffset, _AddressLength));
                sourceOffset += _AddressLength;
            }

            if (auxWords > 0)
            {
                record.Append(f.RecordAuxData, FieldValue.NewBytes(message.Slice(sourceOffset, 4 * auxWords)));
            }

            offset += recordLength;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Appends an IPv6 address read from 16 bytes; the display text is the address itself (no label), which is
    /// what the field name already says.
    /// </summary>
    private static void _AppendAddress(in MutField container, FieldId fieldId, ReadOnlySpan<byte> address)
    {
        // Two 64-bit big-endian halves are the value type's native form; no System.Net.IPAddress is created, which
        // would allocate two objects per address for a text that nobody may read.
        IPv6Address value = new(BinaryPrimitives.ReadUInt64BigEndian(address[..8]), BinaryPrimitives.ReadUInt64BigEndian(address[8..16]));
        container.Append(fieldId, FieldValue.NewIPv6(value));
    }

    /// <summary>Text of the MLDv2 query flags byte; precomputed strings, so no allocation per packet.</summary>
    private static string _FlagsText(byte flags) => (flags & 0x08) != 0
        ? _FlagsWithSuppress[flags & 0x07]
        : _FlagsWithoutSuppress[flags & 0x07];

    private static readonly string[] _FlagsWithSuppress =
    [
        "[S, QRV=0]", "[S, QRV=1]", "[S, QRV=2]", "[S, QRV=3]", "[S, QRV=4]", "[S, QRV=5]", "[S, QRV=6]", "[S, QRV=7]",
    ];

    private static readonly string[] _FlagsWithoutSuppress =
    [
        "[QRV=0]", "[QRV=1]", "[QRV=2]", "[QRV=3]", "[QRV=4]", "[QRV=5]", "[QRV=6]", "[QRV=7]",
    ];

    /// <summary>Record type name (RFC 3810 section 5.2.12); the numeric value follows in parentheses.</summary>
    private static string _RecordTypeText(byte recordType) => recordType switch
    {
        1 => "Mode Is Include (1)",
        2 => "Mode Is Exclude (2)",
        3 => "Change To Include Mode (3)",
        4 => "Change To Exclude Mode (4)",
        5 => "Allow New Sources (5)",
        6 => "Block Old Sources (6)",
        _ => DisplayTables.GetDecimalText(recordType),
    };

    /// <summary>Title of one record container; constant strings, selected by the record type.</summary>
    private static string _RecordTitle(byte recordType) => recordType switch
    {
        1 => "Multicast Address Record: Mode Is Include",
        2 => "Multicast Address Record: Mode Is Exclude",
        3 => "Multicast Address Record: Change To Include Mode",
        4 => "Multicast Address Record: Change To Exclude Mode",
        5 => "Multicast Address Record: Allow New Sources",
        6 => "Multicast Address Record: Block Old Sources",
        _ => "Multicast Address Record",
    };

    #endregion
}
