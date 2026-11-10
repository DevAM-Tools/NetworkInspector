// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Concurrency;

/// <summary>
/// Cross-thread counter that saturates at <see cref="int.MaxValue"/> instead of wrapping.
/// Owns a <c>volatile int</c> so callers never need to pass <c>ref volatile</c> (CS0420).
/// <para><b>Thread-safety:</b> thread-safe; any number of threads may call <see cref="Increment"/> and read <see cref="Value"/>.</para>
/// </summary>
public sealed class SaturatingVolatileCounter
{
    /// <summary>
    /// The counter value. Volatile: a read on any thread sees the latest published value (acquire), and the compare-exchange
    /// of <see cref="Increment"/> is the only writer path, so no lock is needed.
    /// </summary>
    private volatile int _Value;

    /// <summary>Creates a counter starting at zero.</summary>
    public SaturatingVolatileCounter()
    {
    }

    /// <summary>Creates a counter that starts at a given value.</summary>
    /// <param name="initial">The start value; 0 or more (up to <see cref="int.MaxValue"/>, where the counter is already saturated).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initial"/> is negative.</exception>
    public SaturatingVolatileCounter(int initial)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initial);
        _Value = initial;
    }

    /// <summary>Current counter value (volatile read).</summary>
    public int Value => _Value;

    /// <summary>
    /// Atomically increments unless already <see cref="int.MaxValue"/>; at the maximum the call changes nothing and does not throw.
    /// </summary>
    /// <remarks>Lock-free compare-exchange loop; it repeats only when another thread changed the value between the read and the exchange.</remarks>
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
}
