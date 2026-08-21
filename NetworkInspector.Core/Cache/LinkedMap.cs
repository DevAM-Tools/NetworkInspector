// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Cache;

/// <summary>
/// Arena-backed doubly-linked map with free-list slot reuse.
/// Provides O(1) insert, remove, move-to-front, and pop-back.
/// Uses <see cref="CollectionsMarshal.GetValueRefOrNullRef{TKey, TValue}"/>
/// for single-lookup dictionary access in hot paths.
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Caller synchronization required.
/// </para>
/// </summary>
internal sealed class LinkedMap<TKey, TValue> where TKey : notnull
{
    private struct Slot
    {
        internal bool IsOccupied;
        internal TKey Key;
        internal TValue Value;
        internal int Prev; // -1 = head sentinel
        internal int Next; // -1 = tail sentinel
        internal int NextFree; // free list link (-1 = end)
    }

    #region Fields

    private readonly List<Slot> _Slots = [];
    private readonly Dictionary<TKey, int> _Index;
    private int _Head = -1;
    private int _Tail = -1;
    private int _FreeHead = -1;
    private int _Count;

    #endregion

    #region Constructors

    /// <summary>Creates an empty linked map with the specified initial capacity.</summary>
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
    internal bool InsertFront(TKey key, TValue value)
    {
        // Single dictionary lookup via ref
        ref int existingSlot = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (!Unsafe.IsNullRef(ref existingSlot))
        {
            // Key exists — replace value in place, preserve ordering
            CollectionsMarshal.AsSpan(_Slots)[existingSlot].Value = value;
            return false;
        }

        int slotIndex = _AllocateSlot();
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        ref Slot slot = ref slots[slotIndex];
        slot.IsOccupied = true;
        slot.Key = key;
        slot.Value = value;
        slot.Prev = -1;
        slot.Next = _Head;

        if (_Head >= 0)
        {
            slots[_Head].Prev = slotIndex;
        }
        _Head = slotIndex;
        if (_Tail < 0)
        {
            _Tail = slotIndex;
        }

        _Index[key] = slotIndex;
        _Count++;
        return true;
    }

    /// <summary>
    /// Updates the value for an existing key in place without changing its position.
    /// Single dictionary lookup. Returns true if found and updated.
    /// </summary>
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
    internal bool TryUpdateAndMoveToFront(TKey key, TValue newValue, out TValue oldValue)
    {
        ref int slotIndex = ref CollectionsMarshal.GetValueRefOrNullRef(_Index, key);
        if (Unsafe.IsNullRef(ref slotIndex))
        {
            oldValue = default!;
            return false;
        }

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
    internal bool Remove(TKey key, out TValue value)
    {
        if (!_Index.Remove(key, out int slotIndex))
        {
            value = default!;
            return false;
        }

        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        value = slots[slotIndex].Value;
        _UnlinkSlot(slotIndex);
        _FreeSlot(slotIndex);
        _Count--;
        return true;
    }

    /// <summary>Removes and returns the back (least recently used) entry.</summary>
    internal bool PopBack(out TKey key, out TValue value)
    {
        if (_Tail < 0)
        {
            key = default!;
            value = default!;
            return false;
        }

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ContainsKey(TKey key) => _Index.ContainsKey(key);

    /// <summary>Tries to get the value for a key without changing position.</summary>
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

    /// <summary>Clears all entries.</summary>
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

    private int _AllocateSlot()
    {
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

    private void _FreeSlot(int index)
    {
        Span<Slot> slots = CollectionsMarshal.AsSpan(_Slots);
        slots[index].IsOccupied = false;
        slots[index].Key = default!;
        slots[index].Value = default!;
        slots[index].NextFree = _FreeHead;
        _FreeHead = index;
    }

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

    private void _MoveToFrontInternal(int slotIndex)
    {
        if (slotIndex == _Head)
        {
            return;
        } // already at front

        // Unlink
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
