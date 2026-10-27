// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 3 (header_type = 3) — 16 bytes.
/// Layout (little-endian):
/// <code>
///   [0..4)  flags (u32; the whole word is 1 for 10 µs or 2 for 1 ns)
///   [4..6)  static size (u16)
///   [6..8)  object version (u16)
///   [8..16) object timestamp (u64)
/// </code>
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct BlfLogObjectHeaderV3
{
    /// <summary>
    /// Flags word. The whole value must be 1 (10 µs units) or 2 (nanoseconds).
    /// Any other word, including a value whose low nibble is 1 or 2, yields timestamp 0.
    /// </summary>
    public uint Flags
    {
        get; init;
    }

    /// <summary>Static size of the object (V3 specific).</summary>
    public ushort StaticSize
    {
        get; init;
    }

    /// <summary>Version of the object structure.</summary>
    public ushort ObjectVersion
    {
        get; init;
    }

    /// <summary>Raw timestamp. Units come from the whole <see cref="Flags"/> word (1 = 10 µs, 2 = 1 ns).</summary>
    public ulong Timestamp
    {
        get; init;
    }

    /// <summary>Header size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Decodes the 16-byte V3 log object header; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out BlfLogObjectHeaderV3 value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new BlfLogObjectHeaderV3
        {
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(h),            // 0..4
            StaticSize = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),  // 4..6
            ObjectVersion = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), // 6..8
            Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(h[8..]),   // 8..16
        };
        return true;
    }
}
