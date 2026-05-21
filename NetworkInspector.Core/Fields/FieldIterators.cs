// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

#region Children Iteration

/// <summary>
/// Iterates direct children of a field.
/// When <c>materialize</c> is true (default), lazy parents are materialized before iteration.
/// </summary>
public readonly struct FieldChildEnumerable(Packet packet, ushort parentIndex, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    public FieldChildEnumerator GetEnumerator() => new(packet, parentIndex, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, parentIndex, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    // Allocating class-based enumerator used for IEnumerable<Field> / LINQ scenarios.
    // The zero-alloc foreach path uses GetEnumerator() above (returning ref struct).
    private sealed class BoxedEnumerator(Packet p, ushort parentIdx, bool mat) : IEnumerator<Field>
    {
        private ushort _CurrentIndex = FieldBody.NullIndex;
        private bool _Started;

        /// <inheritdoc/>
        public Field Current => new(p, _CurrentIndex);

        /// <inheritdoc/>
        object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            if (!_Started)
            {
                _Started = true;
                // Materialize the lazy parent before reading its first child index.
                if (mat && p.HasUnpopulatedLazyFields && p.GetFieldRef(parentIdx).NeedsMaterialization)
                {
                    p.MaterializeLazyField(parentIdx);
                }
                _CurrentIndex = p.GetFieldRef(parentIdx).FirstChildIndex;
                return _CurrentIndex != FieldBody.NullIndex;
            }
            if (_CurrentIndex == FieldBody.NullIndex)
            {
                return false;
            }
            _CurrentIndex = p.GetFieldRef(_CurrentIndex).NextIndex;
            return _CurrentIndex != FieldBody.NullIndex;
        }

        /// <inheritdoc/>
        public void Reset()
        {
            _CurrentIndex = FieldBody.NullIndex;
            _Started = false;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Struct-based enumerator for direct children of a field.
/// Walks the sibling linked list — zero allocation, O(1) per step.
/// </summary>
public ref struct FieldChildEnumerator
{
    private readonly Packet _Packet;
    private ushort _CurrentIndex;
    private bool _Started;

    /// <summary>Creates a child enumerator.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="parentIndex">Storage index of the parent field.</param>
    /// <param name="materialize">Whether to materialize the parent's lazy children before iterating.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldChildEnumerator(Packet packet, ushort parentIndex, bool materialize)
    {
        _Packet = packet;

        // Optionally materialize lazy parent before reading its child list.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (materialize && packet.HasUnpopulatedLazyFields)
        {
            ref readonly FieldBody parentBody = ref packet.GetFieldRef(parentIndex);
            if (parentBody.NeedsMaterialization)
            {
                packet.MaterializeLazyField(parentIndex);
            }
        }

        _CurrentIndex = packet.GetFieldRef(parentIndex).FirstChildIndex;
        _Started = false;
    }

    /// <summary>The current child field.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _CurrentIndex);
    }

    /// <summary>Advances to the next child.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (!_Started)
        {
            _Started = true;
            return _CurrentIndex != FieldBody.NullIndex;
        }
        if (_CurrentIndex == FieldBody.NullIndex)
        {
            return false;
        }
        _CurrentIndex = _Packet.GetFieldRef(_CurrentIndex).NextIndex;
        return _CurrentIndex != FieldBody.NullIndex;
    }
}

#endregion

#region Descendant Iteration

/// <summary>
/// Iterates all descendants of a field in depth-first pre-order (excludes the root itself).
/// When <c>materialize</c> is true (default), lazy fields are materialized during traversal.
/// </summary>
public readonly struct FieldDescendantEnumerable(Packet packet, ushort rootIndex, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    public FieldDescendantEnumerator GetEnumerator() => new(packet, rootIndex, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, rootIndex, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    // Allocating class-based enumerator used for IEnumerable<Field> / LINQ scenarios.
    private sealed class BoxedEnumerator : IEnumerator<Field>
    {
        private readonly Packet _Packet;
        private readonly ushort _RootIndex;
        private readonly bool _Materialize;
        private readonly Stack<ushort> _Stack = new();
        private ushort _Current = FieldBody.NullIndex;

        internal BoxedEnumerator(Packet packet, ushort rootIndex, bool materialize)
        {
            _Packet = packet;
            _RootIndex = rootIndex;
            _Materialize = materialize;
            Initialize();
        }

        private void Initialize()
        {
            _Stack.Clear();
            _Current = FieldBody.NullIndex;
            // Materialize root before reading its child list.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_RootIndex).NeedsMaterialization)
            {
                _Packet.MaterializeLazyField(_RootIndex);
            }
            // Push first child of root to begin DFS traversal (root itself is not yielded).
            ushort firstChild = _Packet.GetFieldRef(_RootIndex).FirstChildIndex;
            if (firstChild != FieldBody.NullIndex)
            {
                _Stack.Push(firstChild);
            }
        }

        /// <inheritdoc/>
        public Field Current => new(_Packet, _Current);

        /// <inheritdoc/>
        object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            if (_Stack.Count == 0)
            {
                return false;
            }
            _Current = _Stack.Pop();
            // Push next sibling first so it is processed after all descendants of _Current.
            ushort next = _Packet.GetFieldRef(_Current).NextIndex;
            if (next != FieldBody.NullIndex)
            {
                _Stack.Push(next);
            }
            // Materialize before reading children.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_Current).NeedsMaterialization)
            {
                _Packet.MaterializeLazyField(_Current);
            }
            // Push first child so it is processed before the sibling.
            ushort child = _Packet.GetFieldRef(_Current).FirstChildIndex;
            if (child != FieldBody.NullIndex)
            {
                _Stack.Push(child);
            }
            return true;
        }

        /// <inheritdoc/>
        public void Reset() => Initialize();

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Struct-based DFS pre-order enumerator over descendants of a field.
/// Uses an inline stack (no heap allocation for trees with depth ≤ 16).
/// Falls back to a heap-allocated <c>ushort[]</c> for deeper trees.
/// </summary>
public ref struct FieldDescendantEnumerator
{
    private readonly Packet _Packet;
    private readonly bool _Materialize;
    private InlineStack16 _Stack;
    private ushort _Current;

    /// <summary>Creates a DFS pre-order descendant enumerator.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="rootIndex">Storage index of the root field (not yielded).</param>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    internal FieldDescendantEnumerator(Packet packet, ushort rootIndex, bool materialize)
    {
        _Packet = packet;
        _Materialize = materialize;
        _Stack = default;
        _Current = FieldBody.NullIndex;

        // Optionally materialize root before reading its child list.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (materialize && packet.HasUnpopulatedLazyFields)
        {
            ref readonly FieldBody rootBody = ref packet.GetFieldRef(rootIndex);
            if (rootBody.NeedsMaterialization)
            {
                packet.MaterializeLazyField(rootIndex);
            }
        }

        // Push the first child of the root to start traversal
        ushort firstChild = packet.GetFieldRef(rootIndex).FirstChildIndex;
        if (firstChild != FieldBody.NullIndex)
        {
            _Stack.Push(firstChild);
        }
    }

    /// <summary>The current descendant field.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _Current);
    }

    /// <summary>Advances to the next descendant in DFS pre-order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_Stack.Count == 0)
        {
            return false;
        }

        _Current = _Stack.Pop();

        // Push next sibling first so children are processed before it
        ushort next = _Packet.GetFieldRef(_Current).NextIndex;
        if (next != FieldBody.NullIndex)
        {
            _Stack.Push(next);
        }

        // Optionally materialize before descending into children.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (_Materialize && _Packet.HasUnpopulatedLazyFields && _Packet.GetFieldRef(_Current).NeedsMaterialization)
        {
            _Packet.MaterializeLazyField(_Current);
        }

        // Push first child to descend
        ushort child = _Packet.GetFieldRef(_Current).FirstChildIndex;
        if (child != FieldBody.NullIndex)
        {
            _Stack.Push(child);
        }

        return true;
    }
}

#endregion

#region DFS Iteration

/// <summary>
/// Depth-first pre-order enumerable over all fields in a packet (including root).
/// When <c>materialize</c> is true (default), lazy fields are materialized during traversal.
/// </summary>
public readonly struct FieldDfsEnumerable(Packet packet, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    public FieldDfsEnumerator GetEnumerator() => new(packet, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    // Allocating class-based enumerator used for IEnumerable<Field> / LINQ scenarios.
    private sealed class BoxedEnumerator : IEnumerator<Field>
    {
        private readonly Packet _Packet;
        private readonly bool _Materialize;
        private readonly Stack<ushort> _Stack = new();
        private ushort _Current = FieldBody.NullIndex;

        internal BoxedEnumerator(Packet packet, bool materialize)
        {
            _Packet = packet;
            _Materialize = materialize;
            Initialize();
        }

        private void Initialize()
        {
            _Stack.Clear();
            _Current = FieldBody.NullIndex;
            // Push root to begin full-packet DFS traversal (root is included in the output).
            if (_Packet.FieldCount() > 0)
            {
                _Stack.Push(0);
            }
        }

        /// <inheritdoc/>
        public Field Current => new(_Packet, _Current);

        /// <inheritdoc/>
        object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            if (_Stack.Count == 0)
            {
                return false;
            }
            _Current = _Stack.Pop();
            // Materialize before descending into children.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_Current).NeedsMaterialization)
            {
                _Packet.MaterializeLazyField(_Current);
            }
            // Push next sibling first (lower in stack), then first child (top).
            // LIFO order ensures children are visited before siblings — correct DFS pre-order.
            ushort next = _Packet.GetFieldRef(_Current).NextIndex;
            if (next != FieldBody.NullIndex)
            {
                _Stack.Push(next);
            }
            ushort child = _Packet.GetFieldRef(_Current).FirstChildIndex;
            if (child != FieldBody.NullIndex)
            {
                _Stack.Push(child);
            }
            return true;
        }

        /// <inheritdoc/>
        public void Reset() => Initialize();

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Struct-based DFS pre-order enumerator over all fields in a packet.
/// Uses an inline stack (no heap allocation for trees with depth ≤ 16).
/// Falls back to a heap-allocated <c>ushort[]</c> for deeper trees.
/// </summary>
public ref struct FieldDfsEnumerator
{
    private readonly Packet _Packet;
    private readonly bool _Materialize;
    private InlineStack16 _Stack;
    private ushort _Current;

    /// <summary>Creates a DFS enumerator starting from the packet's root field.</summary>
    /// <param name="packet">The packet to traverse.</param>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    internal FieldDfsEnumerator(Packet packet, bool materialize)
    {
        _Packet = packet;
        _Materialize = materialize;
        _Stack = default;
        _Current = FieldBody.NullIndex;

        if (packet.FieldCount() > 0)
        {
            _Stack.Push(0); // push root
        }
    }

    /// <summary>The current field.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _Current);
    }

    /// <summary>Advances to the next field in DFS pre-order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_Stack.Count == 0)
        {
            return false;
        }

        _Current = _Stack.Pop();

        // Optionally materialize before descending into children.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (_Materialize && _Packet.HasUnpopulatedLazyFields && _Packet.GetFieldRef(_Current).NeedsMaterialization)
        {
            _Packet.MaterializeLazyField(_Current);
        }

        // Push next sibling first, then first child (sibling-based DFS)
        // This keeps stack depth = tree depth (max 2 pushes per step)
        ushort next = _Packet.GetFieldRef(_Current).NextIndex;
        if (next != FieldBody.NullIndex)
        {
            _Stack.Push(next);
        }

        ushort child = _Packet.GetFieldRef(_Current).FirstChildIndex;
        if (child != FieldBody.NullIndex)
        {
            _Stack.Push(child);
        }

        return true;
    }
}

#endregion

#region Flat Iteration

/// <summary>
/// Iterates all fields in the packet's internal array linearly (storage order, not tree order).
/// When <c>materialize</c> is true (default), lazy fields are materialized as encountered
/// and their newly added children are visited when the index reaches them.
/// </summary>
public readonly struct FieldFlatEnumerable(Packet packet, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    public FieldFlatEnumerator GetEnumerator() => new(packet, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    // Allocating class-based enumerator used for IEnumerable<Field> / LINQ scenarios.
    private sealed class BoxedEnumerator : IEnumerator<Field>
    {
        private readonly Packet _Packet;
        private readonly bool _Materialize;
        private int _CurrentIndex = -1; // -1 = before first element

        internal BoxedEnumerator(Packet packet, bool materialize)
        {
            _Packet = packet;
            _Materialize = materialize;
        }

        /// <inheritdoc/>
        public Field Current => new(_Packet, (ushort)_CurrentIndex);

        /// <inheritdoc/>
        object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            _CurrentIndex++;
            // FieldCount() is checked dynamically so materialized children are also visited.
            if (_CurrentIndex >= _Packet.FieldCount())
            {
                return false;
            }
            // Materialize lazy field; newly appended children will be at higher indices
            // and will be visited when _CurrentIndex reaches them.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_CurrentIndex).NeedsMaterialization)
            {
                _Packet.MaterializeLazyField((ushort)_CurrentIndex);
            }
            return true;
        }

        /// <inheritdoc/>
        public void Reset() => _CurrentIndex = -1;

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Struct-based linear enumerator over the packet's internal field array.
/// Zero allocation. Iterates fields regardless of tree structure.
/// When <c>materialize</c> is true, materialized children appear at the end of the array
/// and are visited when the index reaches them (dynamic <see cref="Packet.FieldCount"/> check).
/// </summary>
public ref struct FieldFlatEnumerator
{
    private readonly Packet _Packet;
    private readonly bool _Materialize;
    private int _CurrentIndex;

    /// <summary>Creates a flat enumerator over all fields in the packet.</summary>
    /// <param name="packet">The packet to iterate.</param>
    /// <param name="materialize">Whether to materialize lazy fields during iteration.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldFlatEnumerator(Packet packet, bool materialize)
    {
        _Packet = packet;
        _Materialize = materialize;
        _CurrentIndex = -1; // pre-first sentinel
    }

    /// <summary>The current field.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, (ushort)_CurrentIndex);
    }

    /// <summary>Advances to the next field in storage order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _CurrentIndex++;

        // Dynamic check: FieldCount may grow during materialization
        if (_CurrentIndex >= _Packet.FieldCount())
        {
            return false;
        }

        // Optionally materialize — newly added children will be appended
        // to the array and visited when _CurrentIndex reaches them.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (_Materialize && _Packet.HasUnpopulatedLazyFields)
        {
            if (_Packet.GetFieldRef(_CurrentIndex).NeedsMaterialization)
            {
                _Packet.MaterializeLazyField((ushort)_CurrentIndex);
            }
        }

        return true;
    }
}

#endregion

#region Private Helpers

/// <summary>
/// Lightweight inline stack for <see cref="ushort"/> values.
/// Stores up to 16 entries (32 bytes) directly on the call stack.
/// Falls back to a heap-allocated array for deeper trees.
/// </summary>
internal ref struct InlineStack16
{
    private const int InlineCapacity = 16;

    private InlineBuffer16 _InlineBuffer;
    private ushort[]? _HeapBuffer;
    private int _Count;

    /// <summary>Number of elements currently on the stack.</summary>
    internal readonly int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Count;
    }

    /// <summary>Pushes a value onto the stack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Push(ushort value)
    {
        if (_Count < InlineCapacity)
        {
            _InlineBuffer[_Count] = value;
        }
        else
        {
            PushSlow(value);
        }
        _Count++;
    }

    /// <summary>Pops the top value from the stack.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the stack is empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort Pop()
    {
        if (_Count <= 0)
        {
            throw new InvalidOperationException("Cannot pop from an empty stack.");
        }
        _Count--;
        if (_HeapBuffer is null)
        {
            return _InlineBuffer[_Count];
        }
        return _HeapBuffer[_Count];
    }

    /// <summary>Slow path: spills to heap when inline capacity is exceeded.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushSlow(ushort value)
    {
        if (_HeapBuffer is null)
        {
            // First overflow: copy inline entries to heap
            _HeapBuffer = new ushort[InlineCapacity * 2];
            ((Span<ushort>)_InlineBuffer).CopyTo(_HeapBuffer);
        }
        else if (_Count >= _HeapBuffer.Length)
        {
            Array.Resize(ref _HeapBuffer, _HeapBuffer.Length * 2);
        }
        _HeapBuffer[_Count] = value;
    }

    /// <summary>Inline buffer for 16 ushort entries (32 bytes on stack).</summary>
    [InlineArray(InlineCapacity)]
    private struct InlineBuffer16
    {
        private ushort _Element0;
    }
}

#endregion
