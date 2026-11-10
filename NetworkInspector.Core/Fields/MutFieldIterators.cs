// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

#region MutField Children Iteration

/// <summary>
/// Iterates direct children of a <see cref="MutField"/> as mutable cursors.
/// <para>
/// Ref struct — duck-typed <c>foreach</c> only; does not implement <see cref="IEnumerable{T}"/>.
/// </para>
/// When <c>materialize</c> is <see langword="true"/>, lazy parents are materialized before iteration.
/// <para>
/// The ref struct and the duck-typed <c>GetEnumerator</c> let <c>foreach</c> run without allocating an enumerator object or
/// boxing; being a ref struct also means it cannot outlive the parse (it holds a packet reference but no ownership).
/// </para>
/// <para><b>Thread-safety:</b> stack-only value for the thread that iterates; no synchronization of its own.</para>
/// </summary>
public readonly ref struct MutFieldChildEnumerable
{
    /// <summary>The packet whose field storage is walked.</summary>
    private readonly Packet _Packet;

    /// <summary>Storage index of the parent whose direct children are visited.</summary>
    private readonly ushort _ParentIndex;

    /// <summary>Whether a lazy parent is materialized before its child list is read.</summary>
    private readonly bool _Materialize;

    /// <summary>Creates a mutable child enumerable.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="parentIndex">Storage index of the parent field.</param>
    /// <param name="materialize">Whether to materialize the parent's lazy children before iterating.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutFieldChildEnumerable(Packet packet, ushort parentIndex, bool materialize)
    {
        _Packet = packet;
        _ParentIndex = parentIndex;
        _Materialize = materialize;
    }

    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    /// <returns>An enumerator positioned before the first child; empty on a skip packet.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MutFieldChildEnumerator GetEnumerator() => new(_Packet, _ParentIndex, _Materialize);
}

/// <summary>
/// Struct-based enumerator for direct children of a <see cref="MutField"/>.
/// Walks the sibling linked list — zero allocation, O(1) per step.
/// <para>
/// The next index is read from the current field when <see cref="MoveNext"/> is called, so a sibling inserted after the
/// current field before the next <see cref="MoveNext"/> is visited. <see cref="Current"/> is valid only after
/// <see cref="MoveNext"/> returned <see langword="true"/>. On a skip packet (no tree) the sequence is empty.
/// </para>
/// <para><b>Thread-safety:</b> stack-only value for the thread that iterates; no synchronization of its own.</para>
/// </summary>
public ref struct MutFieldChildEnumerator
{
    /// <summary>The packet whose field storage is walked.</summary>
    private readonly Packet _Packet;

    /// <summary>
    /// Storage index of the child the cursor is on. Before the first <see cref="MoveNext"/> it is the parent's first child;
    /// <see cref="FieldBody.NullIndex"/> when the parent has no children, on a skip packet, and once the last child has been passed.
    /// </summary>
    private ushort _CurrentIndex;

    /// <summary>
    /// Distinguishes the first <see cref="MoveNext"/> (which must yield the first child that the constructor already positioned
    /// on) from later ones (which advance along the next-sibling link).
    /// </summary>
    private bool _Started;

    /// <summary>Creates a mutable child enumerator.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="parentIndex">Storage index of the parent field.</param>
    /// <param name="materialize">Whether to materialize the parent's lazy children before iterating.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutFieldChildEnumerator(Packet packet, ushort parentIndex, bool materialize)
    {
        _Packet = packet;

        // A skip packet keeps no tree: yield nothing instead of reading field storage that does not exist.
        if (!packet.HasFieldTree)
        {
            _CurrentIndex = FieldBody.NullIndex;
            _Started = false;
            return;
        }

        // Optionally materialize lazy parent before reading its child list; otherwise a lazy container would look empty.
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

    /// <summary>The current child field as a mutable cursor. Valid only after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public readonly MutField Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _CurrentIndex, _Packet.GetFieldRef(_CurrentIndex).FieldId);
    }

    /// <summary>Advances to the next child. Returns <see langword="false"/> after the last child.</summary>
    /// <returns><see langword="true"/> when <see cref="Current"/> is a child; <see langword="false"/> when there are no more.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        // First call: the constructor already positioned the cursor on the first child (or on NullIndex when empty).
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

#region MutField Descendant Iteration

/// <summary>
/// Iterates all descendants of a <see cref="MutField"/> in depth-first pre-order as mutable cursors
/// (excludes the root itself).
/// <para>
/// Ref struct — duck-typed <c>foreach</c> only; does not implement <see cref="IEnumerable{T}"/>.
/// </para>
/// When <c>materialize</c> is <see langword="true"/>, lazy fields are materialized during traversal.
/// <para><b>Thread-safety:</b> stack-only value for the thread that iterates; no synchronization of its own.</para>
/// </summary>
public readonly ref struct MutFieldDescendantEnumerable
{
    /// <summary>The packet whose field storage is walked.</summary>
    private readonly Packet _Packet;

    /// <summary>Storage index of the root whose descendants are visited.</summary>
    private readonly ushort _RootIndex;

    /// <summary>Whether lazy fields are materialized as the walk reaches them.</summary>
    private readonly bool _Materialize;

    /// <summary>Creates a mutable descendant enumerable.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="rootIndex">Storage index of the root field (not yielded).</param>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutFieldDescendantEnumerable(Packet packet, ushort rootIndex, bool materialize)
    {
        _Packet = packet;
        _RootIndex = rootIndex;
        _Materialize = materialize;
    }

    /// <summary>
    /// Returns the zero-alloc struct-based enumerator for <see langword="foreach"/> loops.
    /// </summary>
    /// <returns>An enumerator positioned before the first descendant; empty on a skip packet.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MutFieldDescendantEnumerator GetEnumerator() => new(_Packet, _RootIndex, _Materialize);
}

/// <summary>
/// Struct-based DFS pre-order enumerator over descendants of a <see cref="MutField"/>.
/// Uses an inline stack (no heap allocation while the stack holds at most 16 entries).
/// Falls back to a heap-allocated <c>ushort[]</c> for deeper trees.
/// <para>
/// <b>Why a stack of indexes:</b> the tree links are child, next and parent indexes, so a pre-order walk needs no recursion
/// and no parent pointer. The stack holds the pending next sibling of every ancestor plus the child just pushed, so its
/// size is bounded by the tree depth (plus one), not by the number of fields; shallow protocol trees therefore never
/// leave the inline buffer.
/// </para>
/// <para>
/// The next sibling of a field is captured when the field is popped. A sibling inserted after the field later in the same
/// walk is therefore not visited (unlike the direct-child enumerator, which reads the link on every step).
/// <see cref="Current"/> is valid only after <see cref="MoveNext"/> returned <see langword="true"/>.
/// </para>
/// <para><b>Thread-safety:</b> stack-only value for the thread that iterates; no synchronization of its own.</para>
/// </summary>
public ref struct MutFieldDescendantEnumerator
{
    /// <summary>The packet whose field storage is walked.</summary>
    private readonly Packet _Packet;

    /// <summary>Whether lazy fields are materialized before the walk descends into them.</summary>
    private readonly bool _Materialize;

    /// <summary>Pending storage indexes in visit order (top = next to visit). Inline up to 16 entries, then spills to the heap.</summary>
    private InlineStack16 _Stack;

    /// <summary>Storage index of the field returned by the last successful <see cref="MoveNext"/>.</summary>
    private ushort _Current;

    /// <summary>Creates a DFS pre-order mutable descendant enumerator.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="rootIndex">Storage index of the root field (not yielded).</param>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    internal MutFieldDescendantEnumerator(Packet packet, ushort rootIndex, bool materialize)
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

    /// <summary>The current descendant field as a mutable cursor. Valid only after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public readonly MutField Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _Current, _Packet.GetFieldRef(_Current).FieldId);
    }

    /// <summary>Advances to the next descendant in DFS pre-order.</summary>
    /// <returns><see langword="true"/> when <see cref="Current"/> is a descendant; <see langword="false"/> when the walk is finished.</returns>
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
