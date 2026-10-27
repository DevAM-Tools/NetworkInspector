// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Obsolete Packet Block (PB) — 28 bytes.
/// Deprecated in favour of the Enhanced Packet Block.
/// Notable difference: interface_id is 16 bits (vs 32 bits in EPB).
/// Fields are read as little-endian; byte-swapping applied externally.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct ObsoletePacketBlock
{
    /// <summary>Block type identifier. Always 0x00000002 for PB.</summary>
    public uint BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public uint BlockTotalLength
    {
        get; init;
    }

    /// <summary>Zero-based interface ID (16 bits, unlike EPB's 32 bits).</summary>
    public ushort InterfaceId
    {
        get; init;
    }

    /// <summary>Number of packets dropped between this and the previous packet. Parsed but not used by the reader.</summary>
    public ushort DropsCount
    {
        get; init;
    }

    /// <summary>Upper 32 bits of the 64-bit timestamp.</summary>
    public uint TimestampHigh
    {
        get; init;
    }

    /// <summary>Lower 32 bits of the 64-bit timestamp.</summary>
    public uint TimestampLow
    {
        get; init;
    }

    /// <summary>Number of octets actually captured and stored in this block.</summary>
    public uint CapturedLength
    {
        get; init;
    }

    /// <summary>Original packet length on the wire.</summary>
    public uint OriginalLength
    {
        get; init;
    }

    /// <summary>Fixed part size in bytes.</summary>
    internal const int Size = 28;

    /// <summary>Decodes the 28-byte fixed part; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out ObsoletePacketBlock value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new ObsoletePacketBlock
        {
            BlockType = BinaryPrimitives.ReadUInt32LittleEndian(h),             // 0..4
            BlockTotalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), // 4..8
            InterfaceId = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]),      // 8..10
            DropsCount = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),      // 10..12
            TimestampHigh = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),   // 12..16
            TimestampLow = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),    // 16..20
            CapturedLength = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]),  // 20..24
            OriginalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]),  // 24..28
        };
        return true;
    }
}
