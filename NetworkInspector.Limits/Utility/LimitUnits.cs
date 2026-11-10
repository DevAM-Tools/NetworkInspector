// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Utility;

/// <summary>
/// The ready units most limits need: counts, sizes, and durations. Each is a <see cref="TableUnit"/> built once; declare a unit
/// of your own domain with <see cref="TableUnit"/> and a <see cref="UnitWordSet"/> when none of these fits. Thread-safe.
/// </summary>
/// <remarks>
/// Months and years are no duration words: they have no fixed length, so a limit that needs one says it in days.
/// Percentages are not shipped: what a percentage refers to is part of the limit, not of the unit.
/// </remarks>
/// <seealso cref="TableUnit"/>
/// <seealso cref="UnitWordSet"/>
public static class LimitUnits
{
    /// <summary>A plain whole number (items, streams, levels of nesting). The base unit is one; the value takes no unit word.</summary>
    public static TableUnit Count { get; } = new("counts", "10000", UnitWordSet.None);

    /// <summary>
    /// A size. The base unit is the byte; the accepted words are <c>B</c>, <c>KiB</c>, <c>MiB</c>, <c>GiB</c>, <c>TiB</c>
    /// (binary, 1 KiB = 1024 B). Decimal spellings such as <c>MB</c> are refused with the binary replacement.
    /// </summary>
    public static TableUnit Bytes { get; } = new(
        "bytes",
        "256 MiB",
        UnitWordSet.Binary("B"),
        forbiddenKeySuffixes: ["Bytes", "_bytes", "KiB", "MiB", "GiB", "TiB", "Kib", "Mib", "Gib", "Tib"]);

    /// <summary>
    /// A time span. The base unit is the millisecond; the accepted words are <c>ms</c>, <c>s</c>, <c>min</c>, <c>h</c>,
    /// <c>d</c> (24 hours), and <c>w</c> (7 days). A word is required and a value is at least 1 ms.
    /// </summary>
    public static TableUnit Duration { get; } = new(
        "durations",
        "90 s",
        UnitWordSet.Of("ms").With("s", 1_000).With("min", 60_000).With("h", 3_600_000).With("d", 86_400_000).With("w", 604_800_000),
        wordRequired: true,
        smallestMinimum: 1,
        forbiddenKeySuffixes: ["Seconds", "_seconds", "Secs", "Sec", "Ms", "_ms", "Millis", "Minutes", "Mins", "Hours", "Hrs", "Days", "Weeks"]);
}
