// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Concurrency;

/// <summary>
/// Cross-thread counter that saturates at <see cref="int.MaxValue"/> instead of wrapping.
/// Owns a <c>volatile int</c> so callers never need to pass <c>ref volatile</c> (CS0420).
/// </summary>
public sealed class SaturatingVolatileCounter
{
    #region Fields

    private volatile int _Value;

    #endregion

    #region Constructors

    /// <summary>Creates a counter starting at zero.</summary>
    public SaturatingVolatileCounter()
    {
    }

    /// <summary>Creates a counter starting at <paramref name="initial"/> (non-negative).</summary>
    public SaturatingVolatileCounter(int initial)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initial);
        _Value = initial;
    }

    #endregion

    #region Properties

    /// <summary>Current counter value (volatile read).</summary>
    public int Value => _Value;

    #endregion

    #region Public API

    /// <summary>
    /// Atomically increments unless already <see cref="int.MaxValue"/>.
    /// </summary>
    public void Increment()
    {
        while (true)
        {
            int current = _Value;
            if (current == int.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _Value, current + 1, current) == current)
            {
                return;
            }
        }
    }

    #endregion
}
