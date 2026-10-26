// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tls;

/// <summary>
/// TLS Record Layer header (5 bytes, RFC 8446 Section 5.1): content type (1 byte), legacy record version (2 bytes, big
/// endian), payload length (2 bytes, big endian). Immutable value type, so it is safe to share between threads.
/// </summary>
/// <param name="ContentType">Record content type byte (20 change_cipher_spec, 21 alert, 22 handshake, 23 application_data, 25 heartbeat).</param>
/// <param name="Version">Legacy record version as sent on the wire (0x0303 for TLS 1.2 and for most TLS 1.3 records).</param>
/// <param name="Length">Length of the record payload that follows the header, in bytes.</param>
internal readonly record struct TlsRecordHeader(byte ContentType, ushort Version, ushort Length)
{
    #region Constants

    /// <summary>Size of the TLS record header in bytes.</summary>
    internal const int Size = 5;

    /// <summary>
    /// Maximum TLS record payload length: 2^14 plaintext bytes plus 2048 bytes of compression and protection overhead
    /// (the TLS 1.2 ciphertext bound, RFC 5246 Section 6.2.3). TLS 1.3 allows only 2^14 + 256, so this is the looser
    /// of the two. Constant because it is a protocol fact, not a host-dependent limit.
    /// </summary>
    internal const int MaxRecordLength = 16384 + 2048;

    #endregion

    #region Parsing

    /// <summary>
    /// Attempts to parse a TLS record header from the given data. Only the first <see cref="Size"/> bytes are read.
    /// Does not validate the content type, version, or that <c>Length</c> fits in <paramref name="data"/>: callers
    /// check <see cref="IsValidContentType"/> and bound the payload against the buffer themselves.
    /// </summary>
    /// <param name="data">Bytes starting at the record header.</param>
    /// <param name="header">The parsed header, or <see langword="default"/> when <paramref name="data"/> is shorter than <see cref="Size"/>.</param>
    /// <returns><see langword="true"/> when a complete header was available.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> data, out TlsRecordHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        byte ct = data[0];
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);

        header = new TlsRecordHeader(ct, version, length);
        return true;
    }

    #endregion

    #region Validation

    /// <summary>
    /// Checks if the content type is a known TLS content type (20-23, 25).
    /// </summary>
    internal bool IsValidContentType() =>
        ContentType is 20 or 21 or 22 or 23 or 25;

    /// <summary>
    /// Checks if the version field is a known TLS version.
    /// </summary>
    internal bool IsValidVersion() =>
        Version is 0x0300 or 0x0301 or 0x0302 or 0x0303 or 0x0304;

    #endregion
}
