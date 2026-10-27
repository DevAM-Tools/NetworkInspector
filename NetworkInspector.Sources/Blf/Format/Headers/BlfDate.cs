// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Date structure — 16 bytes (Windows SYSTEMTIME layout).
/// Stores absolute date/time values in the BLF file header.
/// Layout: 8 consecutive LE u16 fields.
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset little-endian reads with <see cref="BinaryPrimitives"/>) instead of a
/// generated parser; the declaration order is the on-disk order for the reader only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct BlfDate
{
    /// <summary>Full year (e.g. 2024).</summary>
    public ushort Year
    {
        get; init;
    }

    /// <summary>Month (1 = January, 12 = December).</summary>
    public ushort Month
    {
        get; init;
    }

    /// <summary>Day of week (0 = Sunday, 6 = Saturday).</summary>
    public ushort DayOfWeek
    {
        get; init;
    }

    /// <summary>Day of the month (1–31).</summary>
    public ushort Day
    {
        get; init;
    }

    /// <summary>Hour (0–23).</summary>
    public ushort Hour
    {
        get; init;
    }

    /// <summary>Minute (0–59).</summary>
    public ushort Minute
    {
        get; init;
    }

    /// <summary>Second (0–59).</summary>
    public ushort Second
    {
        get; init;
    }

    /// <summary>Millisecond (0–999).</summary>
    public ushort Millisecond
    {
        get; init;
    }

    /// <summary>Structure size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Decodes the 16-byte date; <see langword="false"/> with a default value when fewer bytes are present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out BlfDate value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < Size)
        {
            value = default;
            return false;
        }

        // Exact-size slice: the fixed-offset reads below need no further bounds checks after JIT.
        ReadOnlySpan<byte> h = source[..Size];
        value = new BlfDate
        {
            Year = BinaryPrimitives.ReadUInt16LittleEndian(h),          // 0..2
            Month = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]),    // 2..4
            DayOfWeek = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]), // 4..6
            Day = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),      // 6..8
            Hour = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]),     // 8..10
            Minute = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),  // 10..12
            Second = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]),  // 12..14
            Millisecond = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]), // 14..16
        };
        return true;
    }
}
