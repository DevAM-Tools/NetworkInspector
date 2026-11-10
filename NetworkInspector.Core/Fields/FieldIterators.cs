// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

#region Children Iteration

/// <summary>
/// Iterates direct children of a field.
/// When <c>materialize</c> is <see langword="true"/>, lazy parents are materialized before iteration.
/// <para>
/// <b>Design (applies to all enumerables in this file):</b> each enumerable is a small struct with two enumerators. The
/// public <c>GetEnumerator()</c> returns a <see langword="ref struct"/> enumerator, which <see langword="foreach"/> binds
/// to, so a normal loop allocates nothing and needs no interface call. The explicit
/// <see cref="IEnumerable{T}"/> implementation returns a class-based enumerator (one allocation) so LINQ and other
/// interface consumers still work. Enumerators never lock: they follow the volatile sibling/child links and use
/// <c>WaitUntilFieldPublished</c> so a link that a concurrent materializer has just written is not followed before the
/// slot it points to is visible.
/// </para>
/// <para><b>Thread-safety:</b> the enumerable is an immutable value; an enumerator belongs to one thread.</para>
/// </summary>
/// <param name="packet">The packet that owns the fields.</param>
/// <param name="parentIndex">Storage index of the parent whose children are enumerated.</param>
/// <param name="materialize">Whether to populate lazy children before iterating.</param>
public readonly struct FieldChildEnumerable(Packet packet, ushort parentIndex, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    /// <returns>An enumerator over the direct children, first to last; it materializes the parent's lazy children first when requested.</returns>
    public FieldChildEnumerator GetEnumerator() => new(packet, parentIndex, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    /// <returns>An enumerator over the direct children; one allocation, and it materializes lazily on the first <c>MoveNext</c>.</returns>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, parentIndex, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    /// <summary>
    /// Allocating class-based enumerator used for <see cref="IEnumerable{T}"/> and LINQ scenarios. The zero-allocation
    /// <see langword="foreach"/> path uses <see cref="FieldChildEnumerable.GetEnumerator()"/> (a <see langword="ref struct"/>).
    /// </summary>
    /// <param name="p">The packet that owns the fields.</param>
    /// <param name="parentIdx">Storage index of the parent whose children are enumerated.</param>
    /// <param name="mat">Whether to populate the parent's lazy children on the first <c>MoveNext</c>.</param>
    private sealed class BoxedEnumerator(Packet p, ushort parentIdx, bool mat) : IEnumerator<Field>
    {
        /// <summary>Storage index of the current child; the null index before the first <c>MoveNext</c> and after the last child.</summary>
        private ushort _CurrentIndex = FieldBody.NullIndex;

        /// <summary>Whether <c>MoveNext</c> has already resolved the first child.</summary>
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
                if (!p.HasFieldTree)
                {
                    _CurrentIndex = FieldBody.NullIndex;
                    return false;
                }

                // Materialize the lazy parent before reading its first child index.
                if (mat && p.HasUnpopulatedLazyFields && p.GetFieldRef(parentIdx).IsLazyPending)
                {
                    p.MaterializeLazyField(parentIdx);
                }
                _CurrentIndex = p.WaitUntilFieldPublished(p.GetFieldRef(parentIdx).FirstChildIndex);
                return _CurrentIndex != FieldBody.NullIndex;
            }
            if (_CurrentIndex == FieldBody.NullIndex)
            {
                return false;
            }
            _CurrentIndex = p.WaitUntilFieldPublished(p.GetFieldRef(_CurrentIndex).NextIndex);
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
/// A <see langword="ref struct"/>, so it cannot be stored on the heap or used across <c>await</c>; it is meant for a
/// <see langword="foreach"/> loop. Not thread-safe (one enumerator per thread).
/// </summary>
public ref struct FieldChildEnumerator
{
    /// <summary>The packet that owns the fields.</summary>
    private readonly Packet _Packet;

    /// <summary>Storage index of the current child; before the first <c>MoveNext</c> it holds the first child (or the null index).</summary>
    private ushort _CurrentIndex;

    /// <summary>False until the first <c>MoveNext</c>, which returns the pre-loaded first child instead of advancing.</summary>
    private bool _Started;

    /// <summary>Creates a child enumerator.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="parentIndex">Storage index of the parent field.</param>
    /// <param name="materialize">Whether to materialize the parent's lazy children before iterating.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldChildEnumerator(Packet packet, ushort parentIndex, bool materialize)
    {
        _Packet = packet;

        if (!packet.HasFieldTree)
        {
            _CurrentIndex = FieldBody.NullIndex;
            _Started = false;
            return;
        }

        // Optionally materialize lazy parent before reading its child list.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (materialize && packet.HasUnpopulatedLazyFields)
        {
            ref readonly FieldBody parentBody = ref packet.GetFieldRef(parentIndex);
            if (parentBody.IsLazyPending)
            {
                packet.MaterializeLazyField(parentIndex);
            }
        }

        _CurrentIndex = packet.WaitUntilFieldPublished(packet.GetFieldRef(parentIndex).FirstChildIndex);
        _Started = false;
    }

    /// <summary>The current child field. Valid only after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _CurrentIndex);
    }

    /// <summary>Advances to the next child.</summary>
    /// <returns><see langword="false"/> when there is no further child.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        // First call: the constructor already resolved the first child, so return it without advancing.
        if (!_Started)
        {
            _Started = true;
            return _CurrentIndex != FieldBody.NullIndex;
        }
        // Exhausted already (or empty): stay exhausted.
        if (_CurrentIndex == FieldBody.NullIndex)
        {
            return false;
        }

        // Follow the volatile sibling link; WaitUntilFieldPublished guards against a link to a not yet published slot.
        _CurrentIndex = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_CurrentIndex).NextIndex);
        return _CurrentIndex != FieldBody.NullIndex;
    }
}

#endregion

#region Descendant Iteration

/// <summary>
/// Iterates all descendants of a field in depth-first pre-order (excludes the root itself).
/// When <c>materialize</c> is <see langword="true"/>, lazy fields are materialized during traversal.
/// See <see cref="FieldChildEnumerable"/> for the shared enumerator design.
/// </summary>
/// <param name="packet">The packet that owns the fields.</param>
/// <param name="rootIndex">Storage index of the field whose descendants are enumerated.</param>
/// <param name="materialize">Whether to populate lazy fields while traversing.</param>
public readonly struct FieldDescendantEnumerable(Packet packet, ushort rootIndex, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    /// <returns>An enumerator over every descendant in depth-first pre-order, the root excluded.</returns>
    public FieldDescendantEnumerator GetEnumerator() => new(packet, rootIndex, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    /// <returns>An enumerator over every descendant in depth-first pre-order, the root excluded; one allocation plus a stack.</returns>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, rootIndex, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    /// <summary>Allocating class-based enumerator used for <see cref="IEnumerable{T}"/> and LINQ scenarios.</summary>
    private sealed class BoxedEnumerator : IEnumerator<Field>
    {
        /// <summary>The packet that owns the fields.</summary>
        private readonly Packet _Packet;

        /// <summary>Storage index of the field whose descendants are enumerated.</summary>
        private readonly ushort _RootIndex;

        /// <summary>Whether lazy fields are populated while traversing.</summary>
        private readonly bool _Materialize;

        /// <summary>Pending fields: each pop yields the next field in pre-order; siblings sit below the children of their predecessor.</summary>
        private readonly Stack<ushort> _Stack = new();

        /// <summary>Storage index of the current field; the null index before the first <c>MoveNext</c>.</summary>
        private ushort _Current = FieldBody.NullIndex;

        /// <summary>Creates the enumerator and positions it before the first descendant.</summary>
        /// <param name="packet">The packet that owns the fields.</param>
        /// <param name="rootIndex">Storage index of the field whose descendants are enumerated.</param>
        /// <param name="materialize">Whether to populate lazy fields while traversing.</param>
        internal BoxedEnumerator(Packet packet, ushort rootIndex, bool materialize)
        {
            _Packet = packet;
            _RootIndex = rootIndex;
            _Materialize = materialize;
            Initialize();
        }

        /// <summary>
        /// Clears the stack and pushes the first child of the root (the root itself is not yielded), materializing the root first
        /// when requested.
        /// </summary>
        private void Initialize()
        {
            _Stack.Clear();
            _Current = FieldBody.NullIndex;
            if (!_Packet.HasFieldTree)
            {
                return;
            }

            // Materialize root before reading its child list.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_RootIndex).IsLazyPending)
            {
                _Packet.MaterializeLazyField(_RootIndex);
            }
            // Push first child of root to begin DFS traversal (root itself is not yielded).
            ushort firstChild = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_RootIndex).FirstChildIndex);
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
            ushort next = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).NextIndex);
            if (next != FieldBody.NullIndex)
            {
                _Stack.Push(next);
            }
            // Materialize before reading children.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_Current).IsLazyPending)
            {
                _Packet.MaterializeLazyField(_Current);
            }
            // Push first child so it is processed before the sibling.
            ushort child = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).FirstChildIndex);
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
    /// <summary>The packet that owns the fields.</summary>
    private readonly Packet _Packet;

    /// <summary>Whether lazy fields are populated while traversing.</summary>
    private readonly bool _Materialize;

    /// <summary>Pending fields; each pop yields the next field in pre-order.</summary>
    private InlineStack16 _Stack;

    /// <summary>Storage index of the current descendant; the null index before the first <c>MoveNext</c>.</summary>
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

        if (!packet.HasFieldTree)
        {
            return;
        }

        // Optionally materialize root before reading its child list.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (materialize && packet.HasUnpopulatedLazyFields)
        {
            ref readonly FieldBody rootBody = ref packet.GetFieldRef(rootIndex);
            if (rootBody.IsLazyPending)
            {
                packet.MaterializeLazyField(rootIndex);
            }
        }

        // Push the first child of the root to start traversal
        ushort firstChild = packet.WaitUntilFieldPublished(packet.GetFieldRef(rootIndex).FirstChildIndex);
        if (firstChild != FieldBody.NullIndex)
        {
            _Stack.Push(firstChild);
        }
    }

    /// <summary>The current descendant field. Valid only after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _Current);
    }

    /// <summary>Advances to the next descendant in DFS pre-order.</summary>
    /// <returns><see langword="false"/> when there is no further descendant.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_Stack.Count == 0)
        {
            return false;
        }

        _Current = _Stack.Pop();

        // Push next sibling first so children are processed before it
        ushort next = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).NextIndex);
        if (next != FieldBody.NullIndex)
        {
            _Stack.Push(next);
        }

        // Optionally materialize before descending into children.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (_Materialize && _Packet.HasUnpopulatedLazyFields && _Packet.GetFieldRef(_Current).IsLazyPending)
        {
            _Packet.MaterializeLazyField(_Current);
        }

        // Push first child to descend
        ushort child = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).FirstChildIndex);
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
/// When <c>materialize</c> is <see langword="true"/>, lazy fields are materialized during traversal.
/// See <see cref="FieldChildEnumerable"/> for the shared enumerator design.
/// </summary>
/// <param name="packet">The packet to traverse.</param>
/// <param name="materialize">Whether to populate lazy fields while traversing.</param>
public readonly struct FieldDfsEnumerable(Packet packet, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    /// <returns>An enumerator over every field in depth-first pre-order, the root included.</returns>
    public FieldDfsEnumerator GetEnumerator() => new(packet, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    /// <returns>An enumerator over every field in depth-first pre-order, the root included; one allocation plus a stack.</returns>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    /// <summary>Allocating class-based enumerator used for <see cref="IEnumerable{T}"/> and LINQ scenarios.</summary>
    private sealed class BoxedEnumerator : IEnumerator<Field>
    {
        /// <summary>The packet that owns the fields.</summary>
        private readonly Packet _Packet;

        /// <summary>Whether lazy fields are populated while traversing.</summary>
        private readonly bool _Materialize;

        /// <summary>Pending fields: each pop yields the next field in pre-order; siblings sit below the children of their predecessor.</summary>
        private readonly Stack<ushort> _Stack = new();

        /// <summary>Storage index of the current field; the null index before the first <c>MoveNext</c>.</summary>
        private ushort _Current = FieldBody.NullIndex;

        /// <summary>Creates the enumerator and positions it before the root.</summary>
        /// <param name="packet">The packet to traverse.</param>
        /// <param name="materialize">Whether to populate lazy fields while traversing.</param>
        internal BoxedEnumerator(Packet packet, bool materialize)
        {
            _Packet = packet;
            _Materialize = materialize;
            Initialize();
        }

        /// <summary>Clears the stack and pushes the root when the packet has a field tree with at least one field.</summary>
        private void Initialize()
        {
            _Stack.Clear();
            _Current = FieldBody.NullIndex;
            // Push root to begin full-packet DFS traversal (root is included in the output).
            // materialize: false — this enumerator owns materialization via _Materialize.
            if (_Packet.HasFieldTree && _Packet.FieldCount(materialize: false) > 0)
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
                && _Packet.GetFieldRef(_Current).IsLazyPending)
            {
                _Packet.MaterializeLazyField(_Current);
            }
            // Push next sibling first (lower in stack), then first child (top).
            // LIFO order ensures children are visited before siblings — correct DFS pre-order.
            ushort next = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).NextIndex);
            if (next != FieldBody.NullIndex)
            {
                _Stack.Push(next);
            }
            ushort child = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).FirstChildIndex);
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
    /// <summary>The packet that owns the fields.</summary>
    private readonly Packet _Packet;

    /// <summary>Whether lazy fields are populated while traversing.</summary>
    private readonly bool _Materialize;

    /// <summary>Pending fields; each pop yields the next field in pre-order.</summary>
    private InlineStack16 _Stack;

    /// <summary>Storage index of the current field; the null index before the first <c>MoveNext</c>.</summary>
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

        // materialize: false — this enumerator owns materialization via _Materialize.
        if (packet.HasFieldTree && packet.FieldCount(materialize: false) > 0)
        {
            _Stack.Push(0); // push root
        }
    }

    /// <summary>The current field. Valid only after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _Current);
    }

    /// <summary>Advances to the next field in DFS pre-order.</summary>
    /// <returns><see langword="false"/> when there is no further field.</returns>
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
        if (_Materialize && _Packet.HasUnpopulatedLazyFields && _Packet.GetFieldRef(_Current).IsLazyPending)
        {
            _Packet.MaterializeLazyField(_Current);
        }

        // Push next sibling first, then first child (sibling-based DFS)
        // This keeps stack depth = tree depth (max 2 pushes per step)
        ushort next = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).NextIndex);
        if (next != FieldBody.NullIndex)
        {
            _Stack.Push(next);
        }

        ushort child = _Packet.WaitUntilFieldPublished(_Packet.GetFieldRef(_Current).FirstChildIndex);
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
/// When <c>materialize</c> is <see langword="true"/>, lazy fields are materialized as encountered
/// and their newly added children are visited when the index reaches them.
/// See <see cref="FieldChildEnumerable"/> for the shared enumerator design.
/// </summary>
/// <param name="packet">The packet to iterate.</param>
/// <param name="materialize">Whether to populate lazy fields while iterating.</param>
public readonly struct FieldFlatEnumerable(Packet packet, bool materialize)
    : IEnumerable<Field>
{
    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    /// <returns>An enumerator over every field in storage order, the root included.</returns>
    public FieldFlatEnumerator GetEnumerator() => new(packet, materialize);

    /// <summary>
    /// Returns an allocating class-based enumerator for LINQ and <see cref="IEnumerable{T}"/> compatibility.
    /// </summary>
    /// <returns>An enumerator over every field in storage order, the root included; one allocation.</returns>
    IEnumerator<Field> IEnumerable<Field>.GetEnumerator() => new BoxedEnumerator(packet, materialize);

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Field>)this).GetEnumerator();

    /// <summary>Allocating class-based enumerator used for <see cref="IEnumerable{T}"/> and LINQ scenarios.</summary>
    private sealed class BoxedEnumerator : IEnumerator<Field>
    {
        /// <summary>The packet that owns the fields.</summary>
        private readonly Packet _Packet;

        /// <summary>Whether lazy fields are populated while iterating.</summary>
        private readonly bool _Materialize;

        /// <summary>Storage index of the current field; -1 before the first element.</summary>
        private int _CurrentIndex = -1;

        /// <summary>Creates the enumerator and positions it before the first field.</summary>
        /// <param name="packet">The packet to iterate.</param>
        /// <param name="materialize">Whether to populate lazy fields while iterating.</param>
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
            // FieldCount is checked dynamically so materialized children are also visited.
            // materialize: false — this enumerator owns materialization via _Materialize.
            if (!_Packet.HasFieldTree || _CurrentIndex >= _Packet.FieldCount(materialize: false))
            {
                return false;
            }
            // Materialize lazy field; newly appended children will be at higher indices
            // and will be visited when _CurrentIndex reaches them.
            if (_Materialize && _Packet.HasUnpopulatedLazyFields
                && _Packet.GetFieldRef(_CurrentIndex).IsLazyPending)
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
    /// <summary>The packet that owns the fields.</summary>
    private readonly Packet _Packet;

    /// <summary>Whether lazy fields are populated while iterating.</summary>
    private readonly bool _Materialize;

    /// <summary>Storage index of the current field; -1 before the first element.</summary>
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

    /// <summary>The current field. Valid only after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public readonly Field Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, (ushort)_CurrentIndex);
    }

    /// <summary>Advances to the next field in storage order.</summary>
    /// <returns><see langword="false"/> when there is no further field.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _CurrentIndex++;

        // Dynamic check: FieldCount may grow during materialization.
        // materialize: false — this enumerator owns materialization via _Materialize.
        if (!_Packet.HasFieldTree || _CurrentIndex >= _Packet.FieldCount(materialize: false))
        {
            return false;
        }

        // Optionally materialize — newly added children will be appended
        // to the array and visited when _CurrentIndex reaches them.
        // Fast outer guard: if no lazy fields are pending at all, skip the per-field check.
        if (_Materialize && _Packet.HasUnpopulatedLazyFields)
        {
            if (_Packet.GetFieldRef(_CurrentIndex).IsLazyPending)
            {
                _Packet.MaterializeLazyField((ushort)_CurrentIndex);
            }
        }

        return true;
    }
}

#endregion

#region Inline stack for the depth-first enumerators

/// <summary>
/// Lightweight inline stack for <see cref="ushort"/> values.
/// Stores up to 16 entries (32 bytes) directly on the call stack.
/// Falls back to a heap-allocated array for deeper trees.
/// The depth-first enumerators use it for their pending siblings and children: a field tree is shallow in practice, so
/// the common traversal needs no heap allocation, which a <c>Stack&lt;ushort&gt;</c> would always cost.
/// </summary>
internal ref struct InlineStack16
{
    /// <summary>Entries that fit into the inline buffer before the stack spills to the heap.</summary>
    private const int _InlineCapacity = 16;

    /// <summary>Fixed-size inline storage (an <c>[InlineArray]</c> struct, so it lives inside this struct, on the stack).</summary>
    private InlineBuffer16 _InlineBuffer;

    /// <summary>Heap storage; null until the first push beyond <see cref="_InlineCapacity"/>.</summary>
    private ushort[]? _HeapBuffer;

    /// <summary>Number of elements currently on the stack.</summary>
    internal int Count { get; private set; }

    /// <summary>Pushes a value onto the stack.</summary>
    /// <param name="value">The storage index to push.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Push(ushort value)
    {
        // Once the stack has spilled, the heap buffer holds every entry and Pop reads from it. Writing to the inline buffer
        // after the stack shrank below the inline capacity would leave the heap entry stale and Pop would return old data.
        if (Count < _InlineCapacity && _HeapBuffer is null)
        {
            _InlineBuffer[Count] = value;
        }
        else
        {
            _PushSlow(value);
        }
        Count++;
    }

    /// <summary>Pops the top value from the stack.</summary>
    /// <returns>The most recently pushed value that has not been popped.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the stack is empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort Pop()
    {
        if (Count <= 0)
        {
            throw new InvalidOperationException("Cannot pop from an empty stack.");
        }
        Count--;
        if (_HeapBuffer is null)
        {
            return _InlineBuffer[Count];
        }
        return _HeapBuffer[Count];
    }

    /// <summary>
    /// Slow path: spills to heap when inline capacity is exceeded, and keeps writing to the heap afterwards (the heap buffer stays
    /// authoritative even when the stack shrinks below the inline capacity again).
    /// </summary>
    /// <param name="value">The storage index to push; <c>Count</c> is incremented by the caller.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _PushSlow(ushort value)
    {
        if (_HeapBuffer is null)
        {
            // First overflow: copy inline entries to heap
            _HeapBuffer = new ushort[_InlineCapacity * 2];
            ((Span<ushort>)_InlineBuffer).CopyTo(_HeapBuffer);
        }
        else if (Count >= _HeapBuffer.Length)
        {
            Array.Resize(ref _HeapBuffer, _HeapBuffer.Length * 2);
        }
        _HeapBuffer[Count] = value;
    }

    /// <summary>Inline buffer for 16 ushort entries (32 bytes on stack).</summary>
    [InlineArray(_InlineCapacity)]
    private struct InlineBuffer16
    {
        /// <summary>The first element; the <see cref="InlineArrayAttribute"/> repeats it to the declared length.</summary>
        private ushort _Element0;
    }
}

#endregion
