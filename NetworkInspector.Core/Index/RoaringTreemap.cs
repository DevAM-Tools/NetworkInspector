// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Roaring treemap for 64-bit values.
/// Partitions by high 32 bits, each containing a <see cref="RoaringBitmap"/> for the low 32 bits.
/// <para>
/// <b>Aliasing model for set operations:</b> <see cref="Or"/>, <see cref="AndNot"/>, and
/// <see cref="Xor"/> produce detached results — single-side <see cref="RoaringBitmap"/> entries
/// are cloned before insert. <see cref="And"/> delegates to <see cref="RoaringBitmap.And"/>,
/// which always allocates fresh bitmaps.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe for concurrent mutation. After all mutations
/// happen-before publication, concurrent reads of <see cref="Contains"/> and
/// <see cref="Cardinality"/> are safe.
/// </para>
/// </summary>
public sealed class RoaringTreemap
{
    #region Fields

    private readonly SortedDictionary<uint, RoaringBitmap> _Map = [];

    #endregion

    #region Properties

    /// <summary>Total number of values stored.</summary>
    public long Cardinality
    {
        get
        {
            long total = 0;
            foreach (RoaringBitmap rb in _Map.Values)
            {
                total += rb.Cardinality;
            }
            return total;
        }
    }

    /// <summary>Whether the treemap is empty.</summary>
    public bool IsEmpty => _Map.Count == 0;

    #endregion

    #region Public API

    /// <summary>Adds a 64-bit value.</summary>
    public void Add(ulong value)
    {
        uint high = (uint)(value >> 32);
        uint low = (uint)(value & 0xFFFFFFFF);

        if (!_Map.TryGetValue(high, out RoaringBitmap? rb))
        {
            rb = new();
            _Map[high] = rb;
        }
        rb.Add(low);
    }

    /// <summary>Checks if a 64-bit value is present.</summary>
    public bool Contains(ulong value)
    {
        uint high = (uint)(value >> 32);
        uint low = (uint)(value & 0xFFFFFFFF);
        return _Map.TryGetValue(high, out RoaringBitmap? rb) && rb.Contains(low);
    }

    /// <summary>
    /// Returns a deep copy of this treemap. Each contained <see cref="RoaringBitmap"/> is
    /// cloned so the result shares no mutable state with the original.
    /// </summary>
    public RoaringTreemap Clone()
    {
        RoaringTreemap result = new();
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in _Map)
        {
            result._Map[kvp.Key] = kvp.Value.Clone();
        }
        return result;
    }

    /// <summary>AND (intersection) with another treemap.</summary>
    public RoaringTreemap And(RoaringTreemap other)
    {
        // Fast-path: x AND x == clone(x). Skip the per-bitmap intersection cost.
        if (ReferenceEquals(this, other))
        {
            return Clone();
        }
        RoaringTreemap result = new();
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in _Map)
        {
            if (other._Map.TryGetValue(kvp.Key, out RoaringBitmap? otherRb))
            {
                RoaringBitmap intersected = kvp.Value.And(otherRb);
                if (intersected.Cardinality > 0)
                {
                    result._Map[kvp.Key] = intersected;
                }
            }
        }
        return result;
    }

    /// <summary>OR (union) with another treemap.</summary>
    public RoaringTreemap Or(RoaringTreemap other)
    {
        // Fast-path: x OR x == clone(x).
        if (ReferenceEquals(this, other))
        {
            return Clone();
        }
        RoaringTreemap result = new();
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in _Map)
        {
            if (other._Map.TryGetValue(kvp.Key, out RoaringBitmap? otherRb))
            {
                result._Map[kvp.Key] = kvp.Value.Or(otherRb);
            }
            else
            {
                result._Map[kvp.Key] = kvp.Value.Clone();
            }
        }
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in other._Map)
        {
            if (!_Map.ContainsKey(kvp.Key))
            {
                result._Map[kvp.Key] = kvp.Value.Clone();
            }
        }
        return result;
    }

    /// <summary>ANDNOT (difference) with another treemap.</summary>
    public RoaringTreemap AndNot(RoaringTreemap other)
    {
        // Fast-path: x ANDNOT x == empty.
        if (ReferenceEquals(this, other))
        {
            return new();
        }
        RoaringTreemap result = new();
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in _Map)
        {
            if (other._Map.TryGetValue(kvp.Key, out RoaringBitmap? otherRb))
            {
                RoaringBitmap diff = kvp.Value.AndNot(otherRb);
                if (diff.Cardinality > 0)
                {
                    result._Map[kvp.Key] = diff;
                }
            }
            else
            {
                result._Map[kvp.Key] = kvp.Value.Clone();
            }
        }
        return result;
    }

    /// <summary>XOR (symmetric difference) with another treemap.</summary>
    public RoaringTreemap Xor(RoaringTreemap other)
    {
        // Fast-path: x XOR x == empty.
        if (ReferenceEquals(this, other))
        {
            return new();
        }
        RoaringTreemap result = new();
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in _Map)
        {
            if (other._Map.TryGetValue(kvp.Key, out RoaringBitmap? otherRb))
            {
                RoaringBitmap xored = kvp.Value.Xor(otherRb);
                if (xored.Cardinality > 0)
                {
                    result._Map[kvp.Key] = xored;
                }
            }
            else
            {
                result._Map[kvp.Key] = kvp.Value.Clone();
            }
        }
        foreach (KeyValuePair<uint, RoaringBitmap> kvp in other._Map)
        {
            if (!_Map.ContainsKey(kvp.Key))
            {
                result._Map[kvp.Key] = kvp.Value.Clone();
            }
        }
        return result;
    }

    #endregion

    #region Private Helpers

    /// <summary>Internal: provides access to the map for iteration during upgrade.</summary>
    internal IEnumerable<KeyValuePair<uint, RoaringBitmap>> Entries => _Map;

    #endregion
}
