// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Dtls;

/// <summary>
/// DTLS Record Layer header (13 bytes, RFC 6347 Section 4.1).
/// <code>
/// +---+---+---+---+---+---+---+---+---+---+---+---+---+
/// | CT| Version |  Epoch  |     Sequence (6 bytes)      | Len |
/// +---+---+---+---+---+---+---+---+---+---+---+---+---+
///   1     2        2              6                       2
/// </code>
/// </summary>
internal readonly struct DtlsRecordHeader
{
    /// <summary>Size of the DTLS record header in bytes.</summary>
    internal const int Size = 13;

    /// <summary>Maximum DTLS record payload length.</summary>
    internal const int MaxRecordLength = 16384 + 2048;

    /// <summary>Content type byte.</summary>
    internal byte ContentType
    {
        get;
    }

    /// <summary>Protocol version (e.g., 0xFEFD = DTLS 1.2).</summary>
    internal ushort Version
    {
        get;
    }

    /// <summary>DTLS epoch for key material tracking.</summary>
    internal ushort Epoch
    {
        get;
    }

    /// <summary>48-bit sequence number within the epoch.</summary>
    internal ulong SequenceNumber
    {
        get;
    }

    /// <summary>Length of the following record payload.</summary>
    internal ushort Length
    {
        get;
    }

    private DtlsRecordHeader(byte contentType, ushort version, ushort epoch, ulong sequenceNumber, ushort length)
    {
        ContentType = contentType;
        Version = version;
        Epoch = epoch;
        SequenceNumber = sequenceNumber;
        Length = length;
    }

    /// <summary>
    /// Attempts to parse a DTLS record header from the given data.
    /// </summary>
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

        // 48-bit sequence number in big-endian (bytes 5-10)
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

    /// <summary>
    /// Checks if the content type is a known DTLS content type (20-23, 25).
    /// Same types as TLS.
    /// </summary>
    internal bool IsValidContentType() =>
        ContentType is 20 or 21 or 22 or 23 or 25;

    /// <summary>
    /// Checks if the version is a known DTLS version.
    /// DTLS 1.0 = 0xFEFF, DTLS 1.2 = 0xFEFD, DTLS 1.3 uses 0xFEFD on wire.
    /// </summary>
    internal bool IsValidVersion() =>
        Version is 0xFEFF or 0xFEFD;
}
