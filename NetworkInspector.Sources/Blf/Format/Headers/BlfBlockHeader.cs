// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF block/object header — 16 bytes.
/// Every BLF object (including log containers) starts with this header.
/// The signature field must equal <see cref="BlfConstants.ObjectMagic"/> ("LOBJ"). The parser itself only reads the
/// fields; callers (<see cref="TryGetSkipDistance"/> and the object parser) check the signature and the sizes.
///
/// Layout:
///   [0..4)  Signature  — "LOBJ" as LE u32
///   [4..6)  HeaderSize — total header size (block + log object header)
///   [6..8)  HeaderType — selects V1/V2/V3 log object header format
///   [8..12) ObjectLength — total LOBJ size from the first magic byte, including this header
///   [12..16) ObjectType — identifies the object kind (CAN, Ethernet, etc.)
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly partial struct BlfBlockHeader
{
    /// <summary>Object signature. Must be "LOBJ" (<see cref="BlfConstants.ObjectMagic"/>).</summary>
    public uint Signature
    {
        get; init;
    }

    /// <summary>
    /// Total header size in bytes (block header + log object header). For a LOG_CONTAINER the BLF writer of this product
    /// stores 16 (the block header alone); the 16-byte container header then starts at this offset. Untrusted: readers
    /// clamp it to at least 16 when they compute the skip distance.
    /// </summary>
    public ushort HeaderSize
    {
        get; init;
    }

    /// <summary>
    /// Header type selector:
    /// 1 = <see cref="BlfLogObjectHeaderV1"/> (16B),
    /// 2 = <see cref="BlfLogObjectHeaderV2"/> (24B),
    /// 3 = <see cref="BlfLogObjectHeaderV3"/> (16B).
    /// </summary>
    public ushort HeaderType
    {
        get; init;
    }

    /// <summary>
    /// Total size in bytes of this LOBJ from the first magic byte, including this 16-byte header.
    /// Inner writers store the unpadded total; 0–3 alignment zeros are written after the object
    /// so the next LOBJ is 4-aligned. Skip distance is <c>max(max(16, ObjectLength), HeaderSize)</c>;
    /// the following 1-byte LOBJ scan consumes those pad bytes. Padding is not part of the
    /// compressed container payload.
    /// </summary>
    public uint ObjectLength
    {
        get; init;
    }

    /// <summary>Object type identifier (see <see cref="BlfConstants"/>).</summary>
    public uint ObjectType
    {
        get; init;
    }

    /// <summary>Header size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Decodes the 16-byte block header; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out BlfBlockHeader value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new BlfBlockHeader
        {
            Signature = BinaryPrimitives.ReadUInt32LittleEndian(h),         // 0..4
            HeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),   // 4..6
            HeaderType = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),   // 6..8
            ObjectLength = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]), // 8..12
            ObjectType = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),  // 12..16
        };
        return true;
    }
}
