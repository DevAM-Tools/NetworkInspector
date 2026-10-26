// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Arena-backed doubly-linked map with free-list slot reuse.
/// Provides O(1) insert, remove, move-to-front, and pop-back.
/// Uses <see cref="CollectionsMarshal.GetValueRefOrNullRef{TKey, TValue}"/>
/// for single-lookup dictionary access (a plain TryGetValue followed by an update would hash the key twice).
/// <para>
/// <b>Design:</b> The list links are slot indices into one <see cref="List{T}"/> of structs (the "arena") instead of
/// <see cref="LinkedListNode{T}"/> objects. This avoids one heap object per entry (no allocation per insert once the
/// arena has grown, no extra GC tracing work) and keeps the entries contiguous. Removed slots are chained into a free
/// list and reused, so a cache that churns at a stable size stops growing the arena. The arena keeps its high-water
/// mark of slots until <see cref="Clear"/> empties it (its capacity is retained). The dictionary maps a key to its slot
/// index, so finding an existing entry costs one hash lookup.
/// </para>
/// <para>
/// <b>Span invariant:</b> <see cref="Slot"/> is a mutable struct, so slots are modified through a
/// <see cref="Span{T}"/> from <c>CollectionsMarshal.AsSpan</c> (a copy would silently lose the
/// write). A span is only valid until the next <see cref="List{T}.Add"/> (which may reallocate the backing array);
/// therefore a span must always be taken after <c>_AllocateSlot</c>, never before.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Caller synchronization required.
/// </para>
/// </summary>
internal sealed class LinkedMap<TKey, TValue> where TKey : notnull
{
    /// <summary>One arena entry: payload plus its position in the recency list and in the free list.</summary>
    private struct Slot
    {
        internal TKey Key;
        internal TValue Value;
        internal int Prev; // -1 = head sentinel
        internal int Next; // -1 = tail sentinel
        internal int NextFree; // free list link (-1 = end)
    }

    #region Fields

    /// <summary>The arena. Index into this list is the slot id used by <see cref="_Index"/>, <c>Prev</c> and <c>Next</c>.</summary>
    private readonly List<Slot> _Slots = [];

    /// <summary>Key to slot index. Holds exactly the occupied slots.</summary>
    private readonly Dictionary<TKey, int> _Index;

    /// <summary>Slot index of the front (most recently used) entry, or -1 when empty.</summary>
    private int _Head = -1;

    /// <summary>Slot index of the back (least recently used) entry, or -1 when empty.</summary>
    private int _Tail = -1;

    /// <summary>First slot of the free list (reusable slots chained through <c>NextFree</c>), or -1 when none.</summary>
    private int _FreeHead = -1;

    /// <summary>Number of occupied slots; equals the dictionary count.</summary>
    private int _Count;

    #endregion

    #region Constructors

    /// <summary>Creates an empty linked map with the specified initial capacity.</summary>
    /// <param name="capacity">Initial capacity of the key dictionary only; the slot arena always starts empty and grows on demand.</param>
    /// <param name="comparer">Optional key comparer; null uses the default comparer.</param>
    /// <remarks>Parameters are not validated here: the dictionary constructor rejects a negative capacity.</remarks>
    internal LinkedMap(int capacity = 16, IEqualityComparer<TKey>? comparer = null)
    {
        _Index = new Dictionary<TKey, int>(capacity, comparer);
    }

    #endregion

    #region Properties

    /// <summary>Number of entries in the map.</summary>
    internal int Count => _Count;

    /// <summary>Returns true if the map contains no entries.</summary>
    internal bool IsEmpty => _Count == 0;

    #endregion

    #region Internal API

    /// <summary>
    /// Inserts at the front of the list. If key already exists, updates value
    /// in place without changing position. Returns true if new, false if updated.
    /// </summary>
    /// <param name="key">The key to insert or update.</param>
    /// <param name="value">The value to store.</param>
    /// <returns><see langword="true"/> when a new entry was added, <see langword="false"/> when an existing one was updated.</returns>
    internal bool InsertFront(TKey key, TValue value)
    {
        // Single dictionary lookup via ref. The ref is only read before the dictionary is modified below.
        ref int existingSlot = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (!Unsafe.IsNullRef(ref existingSlot))
        {
            // Key exists — replace value in place, preserve ordering
            CollectionsMarshal.AsSpan(_Slots)[existingSlot].Value = value;
            return false;
        }

        // New key: take the span only after _AllocateSlot, because allocating may grow (reallocate) the arena
        // and would leave an earlier span pointing at the old array.
        int slotIndex = _AllocateSlot();
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        ref Slot slot = ref slots[slotIndex];
        slot.Key = key;
        slot.Value = value;
        slot.Prev = -1;
        slot.Next = _Head;

        // Link in front of the old head; the first entry is both head and tail.
        if (_Head >= 0)
        {
            slots[_Head].Prev = slotIndex;
        }
        _Head = slotIndex;
        if (_Tail < 0)
        {
            _Tail = slotIndex;
        }

        // Second hash only on a real insert; the update path above needed just the one lookup.
        _Index[key] = slotIndex;
        _Count++;
        return true;
    }

    /// <summary>
    /// Updates the value for an existing key in place without changing its position.
    /// Single dictionary lookup. Returns true if found and updated.
    /// </summary>
    /// <param name="key">The key to update.</param>
    /// <param name="newValue">The replacement value.</param>
    /// <param name="oldValue">The previous value (the caller needs it to adjust its weight accounting), or default when not found.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryUpdateInPlace(TKey key, TValue newValue, out TValue oldValue)
    {
        ref int slotIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (Unsafe.IsNullRef(ref slotIndex))
        {
            oldValue = default!;
            return false;
        }

        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        oldValue = slots[slotIndex].Value;
        slots[slotIndex].Value = newValue;
        return true;
    }

    /// <summary>
    /// Updates the value for an existing key and moves it to the front (LRU promotion).
    /// Single dictionary lookup. Returns true if found and updated.
    /// </summary>
    /// <param name="key">The key to update.</param>
    /// <param name="newValue">The replacement value.</param>
    /// <param name="oldValue">The previous value (the caller needs it to adjust its weight accounting), or default when not found.</param>
    internal bool TryUpdateAndMoveToFront(TKey key, TValue newValue, out TValue oldValue)
    {
        ref int slotIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (Unsafe.IsNullRef(ref slotIndex))
        {
            oldValue = default!;
            return false;
        }

        // Copy the index out of the dictionary ref first: the relink below must not depend on a ref into the dictionary.
        int idx = slotIndex;
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        oldValue = slots[idx].Value;
        slots[idx].Value = newValue;
        _MoveToFrontInternal(idx);
        return true;
    }

    /// <summary>
    /// Gets the value for a key and moves it to the front (LRU promotion).
    /// Single dictionary lookup. Returns true if found.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The stored value, or default when not found.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetAndMoveToFront(TKey key, out TValue value)
    {
        ref int slotIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (Unsafe.IsNullRef(ref slotIndex))
        {
            value = default!;
            return false;
        }

        int idx = slotIndex;
        value = _Slots[idx].Value;
        _MoveToFrontInternal(idx);
        return true;
    }

    /// <summary>Removes a key. Returns true if found.</summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="value">The removed value (the caller needs it to adjust its weight accounting), or default when not found.</param>
    internal bool Remove(TKey key, out TValue value)
    {
        if (!_Index.Remove(key, out int slotIndex))
        {
            value = default!;
            return false;
        }

        // Read the value before _FreeSlot wipes the slot. Unlink first while Prev/Next are still valid.
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        value = slots[slotIndex].Value;
        _UnlinkSlot(slotIndex);
        _FreeSlot(slotIndex);
        _Count--;
        return true;
    }

    /// <summary>Removes and returns the back (least recently used) entry.</summary>
    /// <param name="key">The key of the removed entry, or default when the map is empty.</param>
    /// <param name="value">The value of the removed entry, or default when the map is empty.</param>
    /// <returns><see langword="false"/> when the map is empty.</returns>
    internal bool PopBack(out TKey key, out TValue value)
    {
        if (_Tail < 0)
        {
            key = default!;
            value = default!;
            return false;
        }

        // Copy key and value out before _FreeSlot clears the slot (the out parameters are the only copies afterwards).
        int tailIndex = _Tail;
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        key = slots[tailIndex].Key;
        value = slots[tailIndex].Value;

        _Index.Remove(key);
        _UnlinkSlot(tailIndex);
        _FreeSlot(tailIndex);
        _Count--;
        return true;
    }

    /// <summary>Moves a key to the front (most recently used).</summary>
    /// <param name="key">The key to promote.</param>
    /// <returns><see langword="true"/> when the key exists.</returns>
    internal bool MoveToFront(TKey key)
    {
        ref int slotIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (Unsafe.IsNullRef(ref slotIndex))
        {
            return false;
        }

        _MoveToFrontInternal(slotIndex);
        return true;
    }

    /// <summary>Checks if a key exists.</summary>
    /// <param name="key">The key to look up.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ContainsKey(TKey key) => _Index.ContainsKey(key);

    /// <summary>
    /// Tries to get the value for a key without changing position. Used for FIFO queues, where reading must not
    /// reorder the list.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The stored value, or default when not found.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(TKey key, out TValue value)
    {
        ref int slotIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (Unsafe.IsNullRef(ref slotIndex))
        {
            value = default!;
            return false;
        }

        value = _Slots[slotIndex].Value;
        return true;
    }

    /// <summary>
    /// Clears all entries and releases the arena contents (the arena list keeps its capacity, so refilling does not
    /// reallocate).
    /// </summary>
    internal void Clear()
    {
        _Slots.Clear();
        _Index.Clear();
        _Head = -1;
        _Tail = -1;
        _FreeHead = -1;
        _Count = 0;
    }

    #endregion

    #region Private Helpers

    // ----- Internal helpers -----

    /// <summary>
    /// Returns the index of a free slot: a recycled one from the free list if available, otherwise a new one appended
    /// to the arena. Appending may reallocate the arena, which invalidates every <see cref="Span{T}"/> taken before
    /// this call; callers must therefore take their span afterwards. The caller initializes the slot's fields.
    /// </summary>
    private int _AllocateSlot()
    {
        // Prefer recycling so the arena (and its memory) stops growing once the cache is at steady state.
        if (_FreeHead >= 0)
        {
            int idx = _FreeHead;
            _FreeHead = _Slots[idx].NextFree;
            return idx;
        }
        int newIdx = _Slots.Count;
        _Slots.Add(default);
        return newIdx;
    }

    /// <summary>
    /// Pushes a slot onto the free list. Key and value are cleared on purpose: the slot stays in the arena, so
    /// leaving them would keep an evicted (possibly large) value reachable for the GC until the slot is reused.
    /// </summary>
    private void _FreeSlot(int index)
    {
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        slots[index].Key = default!;
        slots[index].Value = default!;
        slots[index].NextFree = _FreeHead;
        _FreeHead = index;
    }

    /// <summary>
    /// Detaches a slot from the recency list and repairs its neighbours (or head/tail). Does not touch the dictionary,
    /// the free list, or the slot's own links, which the caller overwrites or discards.
    /// </summary>
    private void _UnlinkSlot(int index)
    {
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        int prev = slots[index].Prev;
        int next = slots[index].Next;

        if (prev >= 0)
        {
            slots[prev].Next = next;
        }
        else
        {
            _Head = next;
        }
        if (next >= 0)
        {
            slots[next].Prev = prev;
        }
        else
        {
            _Tail = prev;
        }
    }

    /// <summary>Relinks an existing slot at the front of the recency list without touching the dictionary.</summary>
    private void _MoveToFrontInternal(int slotIndex)
    {
        // Already at front: skip the relink. This is the common case for repeated hits on the same entry.
        if (slotIndex == _Head)
        {
            return;
        }

        // Unlink from the current position (updates head/tail if the slot was an end)
        _UnlinkSlot(slotIndex);

        // Re-insert at front
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        slots[slotIndex].Prev = -1;
        slots[slotIndex].Next = _Head;
        if (_Head >= 0)
        {
            slots[_Head].Prev = slotIndex;
        }
        _Head = slotIndex;
        if (_Tail < 0)
        {
            _Tail = slotIndex;
        }
    }

    #endregion
}
