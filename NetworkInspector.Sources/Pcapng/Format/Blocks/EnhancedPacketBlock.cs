// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Enhanced Packet Block (EPB) — 28 bytes.
/// Standard packet format with interface ID, timestamps, and options.
/// Fields are read as little-endian; byte-swapping applied externally.
/// The 28 bytes are the fixed part only: the packet data, its padding to 4 bytes, the options, and the trailing
/// block length follow.
/// </summary>
/// <remarks>
/// Layout reference only: the production readers do not parse this struct. <c>PcapngPacketBlocks.TryReadEpb</c> reads
/// the same fields directly with an endian-aware reader because EPB parsing is the per-packet hot path (every scanned
/// packet and every <c>FrameById</c>), so direct reads there skip building this struct.
/// The struct documents the on-disk layout and is covered by a parity test, so it must stay in step with that code.
/// <see cref="TryParse"/> decodes it by hand (one length check, fixed-offset little-endian reads with
/// <see cref="BinaryPrimitives"/>); the declaration order is the on-disk order for the reader only. Thread safety:
/// immutable value type.
/// </remarks>
internal readonly struct EnhancedPacketBlock
{
    /// <summary>Block type identifier. Always 0x00000006 for EPB.</summary>
    public uint BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public uint BlockTotalLength
    {
        get; init;
    }

    /// <summary>Zero-based interface ID referencing an IDB in the current section.</summary>
    public uint InterfaceId
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
    public static bool TryParse(ReadOnlySpan<byte> source, out EnhancedPacketBlock value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new EnhancedPacketBlock
        {
            BlockType = BinaryPrimitives.ReadUInt32LittleEndian(h),          // 0..4
            BlockTotalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), // 4..8
            InterfaceId = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),   // 8..12
            TimestampHigh = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]), // 12..16
            TimestampLow = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),  // 16..20
            CapturedLength = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]), // 20..24
            OriginalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]), // 24..28
        };
        return true;
    }
}
