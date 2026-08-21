// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

#region MutField Children Iteration

/// <summary>
/// Iterates direct children of a <see cref="MutField"/> as mutable cursors.
/// <para>
/// Ref struct — duck-typed <c>foreach</c> only; does not implement <see cref="IEnumerable{T}"/>.
/// </para>
/// When <c>materialize</c> is <see langword="true"/>, lazy parents are materialized before iteration.
/// </summary>
public ref struct MutFieldChildEnumerable
{
    private readonly Packet _Packet;
    private readonly ushort _ParentIndex;
    private readonly bool _Materialize;

    /// <summary>Creates a mutable child enumerable.</summary>
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MutFieldChildEnumerator GetEnumerator() => new(_Packet, _ParentIndex, _Materialize);
}

/// <summary>
/// Struct-based enumerator for direct children of a <see cref="MutField"/>.
/// Walks the sibling linked list — zero allocation, O(1) per step.
/// </summary>
public ref struct MutFieldChildEnumerator
{
    private readonly Packet _Packet;
    private ushort _CurrentIndex;
    private bool _Started;

    /// <summary>Creates a mutable child enumerator.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="parentIndex">Storage index of the parent field.</param>
    /// <param name="materialize">Whether to materialize the parent's lazy children before iterating.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutFieldChildEnumerator(Packet packet, ushort parentIndex, bool materialize)
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

    /// <summary>The current child field as a mutable cursor.</summary>
    public readonly MutField Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _CurrentIndex, _Packet.GetFieldRef(_CurrentIndex).FieldId);
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

#region MutField Descendant Iteration

/// <summary>
/// Iterates all descendants of a <see cref="MutField"/> in depth-first pre-order as mutable cursors
/// (excludes the root itself).
/// <para>
/// Ref struct — duck-typed <c>foreach</c> only; does not implement <see cref="IEnumerable{T}"/>.
/// </para>
/// When <c>materialize</c> is <see langword="true"/>, lazy fields are materialized during traversal.
/// </summary>
public ref struct MutFieldDescendantEnumerable
{
    private readonly Packet _Packet;
    private readonly ushort _RootIndex;
    private readonly bool _Materialize;

    /// <summary>Creates a mutable descendant enumerable.</summary>
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MutFieldDescendantEnumerator GetEnumerator() => new(_Packet, _RootIndex, _Materialize);
}

/// <summary>
/// Struct-based DFS pre-order enumerator over descendants of a <see cref="MutField"/>.
/// Uses an inline stack (no heap allocation for trees with depth ≤ 16).
/// Falls back to a heap-allocated <c>ushort[]</c> for deeper trees.
/// </summary>
public ref struct MutFieldDescendantEnumerator
{
    private readonly Packet _Packet;
    private readonly bool _Materialize;
    private InlineStack16 _Stack;
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

    /// <summary>The current descendant field as a mutable cursor.</summary>
    public readonly MutField Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Packet, _Current, _Packet.GetFieldRef(_Current).FieldId);
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
