// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// PCAPNG option header — 4 bytes.
/// Each option within a block body starts with this TLV header.
/// The value that follows is padded to a 4-byte boundary.
/// Read little-endian like the other block structs; <c>PcapOptionIterator</c> swaps both fields for a swapped section
/// and validates the length against the data actually present before it exposes the value.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct OptionHeader
{
    /// <summary>Option code. 0 = end-of-options, 1 = comment, others are block-specific.</summary>
    public ushort Code
    {
        get; init;
    }

    /// <summary>Length of the option value in bytes (not including padding).</summary>
    public ushort Length
    {
        get; init;
    }

    /// <summary>Header size in bytes.</summary>
    internal const int Size = 4;

    /// <summary>Decodes the 4-byte option header; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out OptionHeader value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new OptionHeader
        {
            Code = BinaryPrimitives.ReadUInt16LittleEndian(h),        // 0..2
            Length = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]), // 2..4
        };
        return true;
    }
}
