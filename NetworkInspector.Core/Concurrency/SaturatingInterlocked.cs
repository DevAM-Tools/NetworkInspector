// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Concurrency;

/// <summary>
/// Interlocked helpers that refuse to wrap past <see cref="int.MaxValue"/>.
/// Used for Error/Skipped counters that may exceed frame/packet capacity when multiple
/// errors map to a single item.
/// <para>
/// <b>Volatile fields:</b> C# forbids passing a <c>volatile int</c> field to a user-defined
/// <c>ref int</c> parameter (CS0420). Call sites with <c>volatile</c> counters must use a
/// private saturating CAS loop (or <see cref="System.Threading.Interlocked"/> for non-saturating bumps).
/// This helper is for non-volatile counters (e.g. single-threaded exporters).
/// </para>
/// </summary>
public static class SaturatingInterlocked
{
    #region Public API

    /// <summary>
    /// Atomically increments <paramref name="location"/> unless it is already <see cref="int.MaxValue"/>.
    /// </summary>
    /// <param name="location">
    /// Non-volatile counter location (e.g. single-threaded exporter fields).
    /// Do not pass <c>volatile</c> fields — C# forbids <c>ref volatile</c> to user methods (CS0420);
    /// use a private CAS loop or <see cref="SaturatingVolatileCounter"/> instead.
    /// </param>
    /// <returns>The value after the attempt (either <c>current + 1</c> or <see cref="int.MaxValue"/>).</returns>
    public static int Increment(ref int location)
    {
        while (true)
        {
            int current = location;
            if (current == int.MaxValue)
            {
                return int.MaxValue;
            }

            int updated = Interlocked.CompareExchange(ref location, current + 1, current);
            if (updated == current)
            {
                return current + 1;
            }
        }
    }

    #endregion
}
