// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Date structure — 16 bytes (Windows SYSTEMTIME layout).
/// Stores absolute date/time values in the BLF file header.
/// Layout: 8 consecutive LE u16 fields.
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfDate
{
    /// <summary>Full year (e.g. 2024).</summary>
    public U16LE Year
    {
        get; init;
    }

    /// <summary>Month (1 = January, 12 = December).</summary>
    public U16LE Month
    {
        get; init;
    }

    /// <summary>Day of week (0 = Sunday, 6 = Saturday).</summary>
    public U16LE DayOfWeek
    {
        get; init;
    }

    /// <summary>Day of the month (1–31).</summary>
    public U16LE Day
    {
        get; init;
    }

    /// <summary>Hour (0–23).</summary>
    public U16LE Hour
    {
        get; init;
    }

    /// <summary>Minute (0–59).</summary>
    public U16LE Minute
    {
        get; init;
    }

    /// <summary>Second (0–59).</summary>
    public U16LE Second
    {
        get; init;
    }

    /// <summary>Millisecond (0–999).</summary>
    public U16LE Millisecond
    {
        get; init;
    }
}
