// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Legacy PCAP packet header — 16 bytes.
/// Precedes each captured packet in a legacy .pcap file.
/// Fields are read as little-endian; byte-swapping applied
/// based on the global header's magic number.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct PcapPacketHeader
{
    /// <summary>Timestamp seconds since epoch.</summary>
    public uint TsSec
    {
        get; init;
    }

    /// <summary>
    /// Timestamp fractional part — microseconds or nanoseconds
    /// depending on the global header magic number.
    /// </summary>
    public uint TsFrac
    {
        get; init;
    }

    /// <summary>
    /// Number of octets captured and saved in the file. Untrusted: it sizes the record data read, so it must pass
    /// <see cref="PcapngBlockLayout.TryValidateLegacyInclLen"/> (snaplen and size cap) before it is used.
    /// </summary>
    public uint InclLen
    {
        get; init;
    }

    /// <summary>Original packet length on the wire.</summary>
    public uint OrigLen
    {
        get; init;
    }

    /// <summary>Header size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Decodes the 16-byte record header; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out PcapPacketHeader value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new PcapPacketHeader
        {
            TsSec = BinaryPrimitives.ReadUInt32LittleEndian(h),         // 0..4
            TsFrac = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),   // 4..8
            InclLen = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),  // 8..12
            OrigLen = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]), // 12..16
        };
        return true;
    }
}
