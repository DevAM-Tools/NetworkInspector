// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Legacy PCAP global header — 24 bytes.
/// Appears once at the start of a legacy .pcap file.
/// Fields are read as little-endian; byte-swapping applied
/// if magic matches a swapped variant.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct PcapGlobalHeader
{
    /// <summary>
    /// Magic number identifying format, byte order, and timestamp resolution (microseconds or nanoseconds). Read as
    /// little-endian first; a swapped variant (see <see cref="PcapConstants"/>) means every other field must be byte-swapped.
    /// </summary>
    public uint Magic
    {
        get; init;
    }

    /// <summary>Major PCAP version (typically 2).</summary>
    public ushort VersionMajor
    {
        get; init;
    }

    /// <summary>Minor PCAP version (typically 4).</summary>
    public ushort VersionMinor
    {
        get; init;
    }

    /// <summary>GMT to local correction (usually 0).</summary>
    public uint ThisZone
    {
        get; init;
    }

    /// <summary>Accuracy of timestamps (usually 0).</summary>
    public uint SigFigs
    {
        get; init;
    }

    /// <summary>Snapshot length — maximum number of octets captured.</summary>
    public uint SnapLen
    {
        get; init;
    }

    /// <summary>Link-layer type code (see IANA LINKTYPE registry).</summary>
    public uint Network
    {
        get; init;
    }

    /// <summary>Header size in bytes.</summary>
    internal const int Size = 24;

    /// <summary>Decodes the 24-byte global header; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out PcapGlobalHeader value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new PcapGlobalHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(h),          // 0..4
            VersionMajor = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]), // 4..6
            VersionMinor = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), // 6..8
            ThisZone = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),  // 8..12
            SigFigs = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),  // 12..16
            SnapLen = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),  // 16..20
            Network = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]),  // 20..24
        };
        return true;
    }
}
