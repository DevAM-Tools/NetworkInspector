// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Dtls;

/// <summary>
/// DTLS Record Layer header (13 bytes, RFC 6347 Section 4.1): content type (1), version (2), epoch (2),
/// 48-bit sequence number (6) and payload length (2), all big-endian. This is the DTLS 1.0 / 1.2 plaintext layout;
/// DTLS 1.3 records that use the unified header (RFC 9147) have a different first byte and are not described by
/// this type.
/// </summary>
/// <param name="ContentType">Record content type byte.</param>
/// <param name="Version">Record version as sent. DTLS uses inverted numbers (1.0 = 0xFEFF, 1.2 = 0xFEFD).</param>
/// <param name="Epoch">Epoch counter; increases with each key change.</param>
/// <param name="SequenceNumber">The 48-bit record sequence number, widened to 64 bits (upper 16 bits always zero).</param>
/// <param name="Length">Length of the record payload in bytes as declared by the sender; not checked against the data.</param>
internal readonly record struct DtlsRecordHeader(
    byte ContentType,
    ushort Version,
    ushort Epoch,
    ulong SequenceNumber,
    ushort Length)
{
    #region Constants

    /// <summary>
    /// Size of the DTLS record header in bytes. Does not apply to a connection ID record (content type 25), whose header
    /// has a connection ID of negotiated length before the length field.
    /// </summary>
    internal const int Size = 13;

    #endregion

    #region Parsing

    /// <summary>
    /// Attempts to parse a DTLS record header from the given data.
    /// Only the header is decoded and the declared length is not validated against the data; callers must clamp it.
    /// </summary>
    /// <param name="data">Bytes starting at the first header byte; at least <see cref="Size"/> are required.</param>
    /// <param name="header">The decoded header, or <see langword="default"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="data"/> holds a whole header.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> data, out DtlsRecordHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        byte ct = data[0];
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
        ushort epoch = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);

        // 48-bit sequence number in big-endian (bytes 5-10). BinaryPrimitives has no 6-byte reader, so it is assembled
        // by hand; the widening casts keep each shift in 64 bits (a 32-bit shift by 40 would be wrong).
        ulong seqNum = ((ulong)data[5] << 40)
                     | ((ulong)data[6] << 32)
                     | ((ulong)data[7] << 24)
                     | ((ulong)data[8] << 16)
                     | ((ulong)data[9] << 8)
                     | data[10];

        ushort length = BinaryPrimitives.ReadUInt16BigEndian(data[11..]);

        header = new DtlsRecordHeader(ct, version, epoch, seqNum, length);
        return true;
    }

    #endregion

    #region Validation

    /// <summary>
    /// Checks if the content type is a known DTLS content type: 20 ChangeCipherSpec, 21 Alert, 22 Handshake,
    /// 23 Application Data and 25 (tls12_cid, RFC 9146). Same numbers as TLS; heartbeat (24) is not accepted.
    /// The parser uses this as its only plausibility check: a payload whose first byte is not one of these is
    /// rejected as not DTLS.
    /// </summary>
    internal bool IsValidContentType() =>
        ContentType is 20 or 21 or 22 or 23 or 25;

    #endregion
}
