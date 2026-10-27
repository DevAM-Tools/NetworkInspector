// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Container Header — 16 bytes.
/// Follows the log object header when the object type is LOG_CONTAINER (10).
/// Describes the compression method and uncompressed payload size.
///
/// Layout:
///   [0..2)   CompressionMethod  — 0 = none, 1 = raw LZ4 block, 2 = zlib
///   [2..4)   Reserved1A         — reserved
///   [4..8)   Reserved1B         — reserved
///   [8..12)  UncompressedSize   — decompressed payload size in bytes
///   [12..16) Reserved2          — reserved
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct BlfContainerHeader
{
    /// <summary>
    /// Compression method: 0 = uncompressed, 1 = raw LZ4 block, 2 = zlib.
    /// See <see cref="BlfConstants.CompressionNone"/>, <see cref="BlfConstants.CompressionLz4"/>,
    /// and <see cref="BlfConstants.CompressionZlib"/>.
    /// </summary>
    public ushort CompressionMethod
    {
        get; init;
    }

    /// <summary>Reserved field (part of 6-byte reserved block).</summary>
    public ushort Reserved1A
    {
        get; init;
    }

    /// <summary>Reserved field (part of 6-byte reserved block).</summary>
    public uint Reserved1B
    {
        get; init;
    }

    /// <summary>Size in bytes of the decompressed payload.</summary>
    public uint UncompressedSize
    {
        get; init;
    }

    /// <summary>Reserved field.</summary>
    public uint Reserved2
    {
        get; init;
    }

    /// <summary>Header size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Decodes the 16-byte container header; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out BlfContainerHeader value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new BlfContainerHeader
        {
            CompressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(h),   // 0..2
            Reserved1A = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]),     // 2..4
            Reserved1B = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),     // 4..8
            UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]), // 8..12
            Reserved2 = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),     // 12..16
        };
        return true;
    }
}
