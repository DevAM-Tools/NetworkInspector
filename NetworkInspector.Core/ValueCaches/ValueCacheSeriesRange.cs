// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Half-open index range <c>[Start, End)</c> over a <see cref="ValueCacheSeriesHandle"/> snapshot.
/// </summary>
public readonly struct ValueCacheSeriesRange
{
    #region Constructors

    /// <summary>Creates a range. <paramref name="end"/> must be greater than or equal to <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative, or <paramref name="end"/> is less than <paramref name="start"/>.</exception>
    public ValueCacheSeriesRange(int start, int end)
    {
        if (start < 0 || end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "Range end must be >= start, and start must be >= 0.");
        }

        Start = start;
        End = end;
    }

    #endregion

    #region Properties

    /// <summary>Inclusive start index.</summary>
    public int Start { get; }

    /// <summary>Exclusive end index.</summary>
    public int End { get; }

    /// <summary>Number of rows in the range.</summary>
    public int Count => End - Start;

    /// <summary>True when <see cref="Count"/> is 0.</summary>
    public bool IsEmpty => Start == End;

    #endregion
}
