// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Storage strategy for discrete signal value names (enums).
/// Dense layouts use contiguous arrays for cache-friendly O(1) lookup.
/// </summary>
internal enum SignalEnumKind : byte
{
    /// <summary>No value names.</summary>
    None = 0,

    /// <summary>Names for raw values <c>0..n-1</c> stored as <c>names[raw]</c>.</summary>
    DenseLow = 1,

    /// <summary>
    /// Names for raw values <c>(maxRaw-n+1)..maxRaw</c> stored as
    /// <c>names[maxRaw - raw]</c> where <c>maxRaw = (1 &lt;&lt; bitLength) - 1</c>.
    /// </summary>
    DenseHigh = 2,

    /// <summary>Arbitrary keys stored in a <see cref="FrozenDictionary{TKey,TValue}"/>.</summary>
    Sparse = 3,
}
