// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tls;

/// <summary>
/// TLS Record Layer header (5 bytes, RFC 8446 Section 5.1).
/// <code>
/// +---+---+---+---+---+
/// | CT| Version | Len |
/// +---+---+---+---+---+
///   1     2       2
/// </code>
/// </summary>
internal readonly struct TlsRecordHeader
{
    /// <summary>Size of the TLS record header in bytes.</summary>
    internal const int Size = 5;

    /// <summary>Maximum TLS record payload length (16 KiB + 2 KiB overhead).</summary>
    internal const int MaxRecordLength = 16384 + 2048;

    /// <summary>Content type byte.</summary>
    internal byte ContentType
    {
        get;
    }

    /// <summary>Protocol version (e.g., 0x0303 = TLS 1.2).</summary>
    internal ushort Version
    {
        get;
    }

    /// <summary>Length of the following record payload.</summary>
    internal ushort Length
    {
        get;
    }

    private TlsRecordHeader(byte contentType, ushort version, ushort length)
    {
        ContentType = contentType;
        Version = version;
        Length = length;
    }

    /// <summary>
    /// Attempts to parse a TLS record header from the given data.
    /// </summary>
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
}
