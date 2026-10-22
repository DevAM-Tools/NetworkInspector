// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Per-thread stack of free follow-up chunks of <see cref="FieldStorage"/>, at most <see cref="MaxPooledSlots"/> slots.
/// A recycle that keeps fewer chunks than its last parse used returns the surplus here, and the next chunk append on
/// the same thread rents from here before it takes a new slab slice.
/// <para>Every slot of a pooled chunk is zero: <see cref="FieldStorage.ResetForReuse"/> returns cleared chunks only, so
/// a chunk carries no field data from one packet to another.</para>
/// <para><b>Thread-safety:</b> not thread-safe; <see cref="FieldStorage"/> keeps one instance per thread
/// (<c>[ThreadStatic]</c>) and only that thread touches it.</para>
/// </summary>
internal sealed class FieldChunkPool
{
    #region Constants

    /// <summary>Largest number of pooled slots per thread: 1 024 × 64 B = 64 KiB.</summary>
    internal const int MaxPooledSlots = 1024;

    #endregion

    #region Fields

    private readonly FieldBodyChunk[] _Items;
    private int _Count;

    #endregion

    #region Constructors

    /// <summary>Creates an empty pool for chunks of <paramref name="chunkSize"/> slots.</summary>
    /// <param name="chunkSize">Slots per chunk, 1 … <see cref="MaxPooledSlots"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkSize"/> is outside that range.</exception>
    internal FieldChunkPool(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxPooledSlots);
        _Items = new FieldBodyChunk[MaxPooledSlots / chunkSize];
    }

    #endregion

    #region Properties

    /// <summary>Pooled chunks.</summary>
    internal int Count => _Count;

    /// <summary>Largest number of pooled chunks.</summary>
    internal int Capacity => _Items.Length;

    #endregion

    #region Operations

    /// <summary>Takes the most recently returned chunk; false and a default chunk when the pool is empty.</summary>
    internal bool TryRent(out FieldBodyChunk chunk)
    {
        if (_Count == 0)
        {
            chunk = default;
            return false;
        }

        int last = _Count - 1;
        chunk = _Items[last];

        // The pool must not pin a buffer it no longer owns.
        _Items[last] = default;
        _Count = last;
        return true;
    }

    /// <summary>Adds a zeroed chunk; false when the pool is full (the caller drops the chunk).</summary>
    /// <exception cref="ArgumentNullException">The chunk has no buffer.</exception>
    internal bool TryReturn(in FieldBodyChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk.Buffer);
        if (_Count == _Items.Length)
        {
            return false;
        }

        _Items[_Count] = chunk;
        _Count++;
        return true;
    }

    #endregion
}
