// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Delta encoder for monotonic or near-monotonic sequences such as packet IDs
/// and timestamps. Produces a base value plus an array of deltas for efficient
/// varint encoding.
/// </summary>
internal static class DeltaEncoder
{
    /// <summary>
    /// Encodes a sequence of values as a base value plus deltas from each predecessor.
    /// The first delta is always 0 (delta from base to first value).
    /// </summary>
    /// <param name="values">The values to encode.</param>
    /// <returns>A tuple of the base value and the delta array.</returns>
    internal static (long Base, long[] Deltas) Encode(ReadOnlySpan<long> values)
    {
        if (values.IsEmpty)
        {
            return (0, []);
        }

        long baseValue = values[0];
        long[] deltas = new long[values.Length];
        deltas[0] = 0;
        for (int i = 1; i < values.Length; i++)
        {
            deltas[i] = values[i] - values[i - 1];
        }
        return (baseValue, deltas);
    }
}
