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
    #region Configuration and state

    // Limit: 1 024 slots (1 024 × 64 B = 64 KiB) per thread. It caps the memory a thread keeps pooled after parsing
    // large frames, so retained memory is bounded by thread count × 64 KiB; chunks beyond the cap are dropped and left
    // to the GC (see TryReturn). A fixed constant of this internal cache, not a setting.
    /// <summary>Largest number of pooled slots per thread: 1 024 × 64 B = 64 KiB.</summary>
    internal const int MaxPooledSlots = 1024;

    /// <summary>Stack storage: entries <c>0 … _Count − 1</c> are pooled chunks, the rest are default. Fixed size, never grows.</summary>
    private readonly FieldBodyChunk[] _Items;

    /// <summary>Number of pooled chunks (the stack top).</summary>
    private int _Count;

    /// <summary>Creates an empty pool for chunks of <paramref name="chunkSize"/> slots.</summary>
    /// <param name="chunkSize">Slots per chunk, 1 … <see cref="MaxPooledSlots"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkSize"/> is outside that range.</exception>
    internal FieldChunkPool(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxPooledSlots);
        _Items = new FieldBodyChunk[MaxPooledSlots / chunkSize];
    }

    /// <summary>Pooled chunks.</summary>
    internal int Count => _Count;

    /// <summary>Largest number of pooled chunks.</summary>
    internal int Capacity => _Items.Length;

    #endregion

    #region Rent and return

    /// <summary>Takes the most recently returned chunk; false and a default chunk when the pool is empty.</summary>
    /// <param name="chunk">The pooled chunk (all slots zero), or <c>default</c> when the pool is empty.</param>
    /// <returns><see langword="true"/> when a chunk was taken; <see langword="false"/> when the pool is empty.</returns>
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

    /// <summary>
    /// Adds a zeroed chunk; false when the pool is full (the caller drops the chunk). The chunk is not cleared here:
    /// the caller guarantees every slot is zero, which is what lets a rented chunk be used without clearing it again.
    /// </summary>
    /// <param name="chunk">The chunk to pool; its buffer must not be null.</param>
    /// <returns><see langword="true"/> when the chunk was pooled; <see langword="false"/> when the pool is full.</returns>
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
