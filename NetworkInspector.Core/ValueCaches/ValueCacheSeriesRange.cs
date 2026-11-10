// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Half-open index range <c>[Start, End)</c> over a <see cref="ValueCacheSeriesHandle"/> snapshot.
/// Immutable value type (thread-safe). The constructor only checks that <c>0 &lt;= Start &lt;= End</c>; it cannot know the
/// snapshot length, so the handle that consumes the range throws for an <see cref="End"/> beyond its frozen count.
/// The default value is the empty range <c>[0, 0)</c>.
/// </summary>
public readonly struct ValueCacheSeriesRange
{
    /// <summary>Creates a range. <paramref name="end"/> must be greater than or equal to <paramref name="start"/>.</summary>
    /// <param name="start">Inclusive first row index; not negative.</param>
    /// <param name="end">Exclusive end row index; not less than <paramref name="start"/>. Equal to <paramref name="start"/> makes an empty range.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative, or <paramref name="end"/> is less than <paramref name="start"/>.
    /// </exception>
    public ValueCacheSeriesRange(int start, int end)
    {
        // Invariant 0 <= Start <= End makes Count (End - Start) non-negative and overflow-free for every constructed range.
        if (start < 0 || end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "Range end must be >= start, and start must be >= 0.");
        }

        Start = start;
        End = end;
    }

    /// <summary>Inclusive start index.</summary>
    public int Start { get; }

    /// <summary>Exclusive end index.</summary>
    public int End { get; }

    /// <summary>Number of rows in the range.</summary>
    public int Count => End - Start;

    /// <summary>True when <see cref="Count"/> is 0.</summary>
    public bool IsEmpty => Start == End;
}
