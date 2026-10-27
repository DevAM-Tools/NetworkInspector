// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// Interface Description Block (IDB) — 16 bytes.
/// Describes a capture interface. Fields are read as little-endian;
/// byte-swapping is applied externally based on the enclosing section's byte order.
/// The 16 bytes are the fixed part only; the options (name, timestamp resolution, and so on) and the trailing block
/// length follow.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct InterfaceDescriptionBlock
{
    /// <summary>Block type identifier. Always 0x00000001 for IDB.</summary>
    public uint BlockType
    {
        get; init;
    }

    /// <summary>Total block length in bytes (including trailing copy).</summary>
    public uint BlockTotalLength
    {
        get; init;
    }

    /// <summary>Link-layer type code (see IANA LINKTYPE registry).</summary>
    public ushort LinkType
    {
        get; init;
    }

    /// <summary>Reserved field; the format says zero. The reader does not validate it (a non-zero value is tolerated).</summary>
    public ushort Reserved
    {
        get; init;
    }

    /// <summary>Snapshot length — maximum number of octets captured from each packet.</summary>
    public uint SnapLength
    {
        get; init;
    }

    /// <summary>Fixed part size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Decodes the 16-byte fixed part; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out InterfaceDescriptionBlock value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new InterfaceDescriptionBlock
        {
            BlockType = BinaryPrimitives.ReadUInt32LittleEndian(h),             // 0..4
            BlockTotalLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), // 4..8
            LinkType = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]),         // 8..10
            Reserved = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),        // 10..12
            SnapLength = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),      // 12..16
        };
        return true;
    }
}
