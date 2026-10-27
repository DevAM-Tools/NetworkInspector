// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF file header prefix — 12 bytes.
/// Magic, header length, and API version. Measurement start time is a <see cref="BlfDate"/>
/// at file offset 40 (after application/compression bytes and the compressed/uncompressed
/// length fields). Those later bytes are not part of this prefix.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct BlfFileHeaderPrefix
{
    /// <summary>File signature. Must equal <see cref="BlfConstants.FileMagic"/> ("LOGG").</summary>
    public uint Signature
    {
        get; init;
    }

    /// <summary>Total file header size in bytes. Objects start at this offset.</summary>
    public uint HeaderSize
    {
        get; init;
    }

    /// <summary>BLF API version.</summary>
    public uint ApiVersion
    {
        get; init;
    }

    /// <summary>Prefix size in bytes.</summary>
    internal const int Size = 12;

    /// <summary>Decodes the 12-byte prefix; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out BlfFileHeaderPrefix value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new BlfFileHeaderPrefix
        {
            Signature = BinaryPrimitives.ReadUInt32LittleEndian(h),       // 0..4
            HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), // 4..8
            ApiVersion = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]), // 8..12
        };
        return true;
    }
}
