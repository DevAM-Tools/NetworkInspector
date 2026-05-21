// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Lightweight read-only wrapper over a field in the packet tree.
/// <para>
/// Can be stored in collections and passed across method boundaries.
/// A default-constructed <see cref="Field"/> is invalid — check <see cref="IsValid"/>
/// before accessing properties that depend on the underlying packet.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Field : IEquatable<Field>
{
    /// <summary>The owning packet.</summary>
    private readonly Packet _Packet;

    /// <summary>Index into the packet's flat field list.</summary>
    private readonly ushort _Index;

    /// <summary>Cached field identifier — fits in the padding between _Index (2 bytes) and the next 8-byte slot.</summary>
    private readonly FieldId _FieldId;

    /// <summary>Creates a field wrapper for the given packet and index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Field(Packet packet, ushort index)
    {
        _Packet = packet;
        _Index = index;
        _FieldId = packet.GetFieldRef(index).FieldId;
    }

    #region Validity

    /// <summary>Whether this field reference points to a valid packet and index.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet is not null && _Index != FieldBody.NullIndex;
    }

    #endregion

    #region Public Accessors

    /// <summary>The owning packet.</summary>
    public Packet Packet
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet;
    }

    /// <summary>The storage index within the packet's field list (internal implementation detail).</summary>
    internal ushort StorageIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Index;
    }

    /// <summary>The field's registered identifier (cached — avoids array indirection).</summary>
    public FieldId FieldId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _FieldId;
    }

    /// <summary>Gets the field's metadata from the stack registry.</summary>
    public FieldInfo? FieldInfo => _Packet.Stack.GetField(FieldId);

    /// <summary>The field's value.</summary>
    public FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet.GetFieldRef(_Index).Value;
    }

    /// <summary>
    /// Optional custom display text (check <see cref="LazyString.IsNull"/> for absence).
    /// <para>
    /// Accesses <see cref="FieldBody.CustomText"/> through a mutable ref to the actual
    /// array element. If the text is a lazy <c>Func&lt;string&gt;</c>, it is evaluated
    /// in-place and the result is atomically cached for all future reads.
    /// </para>
    /// </summary>
    public LazyString CustomText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet.GetFieldRef(_Index).CustomText;
    }

    /// <summary>Whether this is the root field (index 0).</summary>
    public bool IsRoot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Index == 0;
    }

    /// <summary>Whether this field has child fields.</summary>
    public bool HasChildren
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly FieldBody body = ref _Packet.GetFieldRef(_Index);
            if (body.NeedsMaterialization)
            {
                _Packet.MaterializeLazyField(_Index);
            }
            return _Packet.GetFieldRef(_Index).FirstChildIndex != FieldBody.NullIndex;
        }
    }

    /// <summary>Number of direct children.</summary>
    public ushort ChildCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly FieldBody body = ref _Packet.GetFieldRef(_Index);
            if (body.NeedsMaterialization)
            {
                _Packet.MaterializeLazyField(_Index);
            }
            return _Packet.GetFieldRef(_Index).ChildCount;
        }
    }

    /// <summary>Whether this field is lazy (has deferred children that need materialization).
    /// Internal so the lazy mechanism stays transparent to external consumers.</summary>
    internal bool NeedsLazyMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet.GetFieldRef(_Index).NeedsMaterialization;
    }

    #endregion

    #region Tree Navigation

    /// <summary>Tries to get the parent field. Returns false if this is a root field.</summary>
    public bool TryGetParent(out Field parent)
    {
        ushort parentIdx = _Packet.GetFieldRef(_Index).ParentIndex;
        if (parentIdx != FieldBody.NullIndex)
        {
            parent = new Field(_Packet, parentIdx);
            return true;
        }
        parent = default;
        return false;
    }

    /// <summary>Tries to get the first child field. Returns false if there are no children.</summary>
    public bool TryGetFirstChild(out Field firstChild)
    {
        ref readonly FieldBody body = ref _Packet.GetFieldRef(_Index);
        if (body.NeedsMaterialization)
        {
            _Packet.MaterializeLazyField(_Index);
        }
        ushort idx = _Packet.GetFieldRef(_Index).FirstChildIndex;
        if (idx != FieldBody.NullIndex)
        {
            firstChild = new Field(_Packet, idx);
            return true;
        }
        firstChild = default;
        return false;
    }

    /// <summary>Tries to get the last child field. Returns false if there are no children.</summary>
    public bool TryGetLastChild(out Field lastChild)
    {
        ref readonly FieldBody body = ref _Packet.GetFieldRef(_Index);
        if (body.NeedsMaterialization)
        {
            _Packet.MaterializeLazyField(_Index);
        }
        ushort idx = _Packet.GetFieldRef(_Index).LastChildIndex;
        if (idx != FieldBody.NullIndex)
        {
            lastChild = new Field(_Packet, idx);
            return true;
        }
        lastChild = default;
        return false;
    }

    /// <summary>Tries to get the next sibling field. Returns false if this is the last sibling.</summary>
    public bool TryGetNext(out Field next)
    {
        ushort idx = _Packet.GetFieldRef(_Index).NextIndex;
        if (idx != FieldBody.NullIndex)
        {
            next = new Field(_Packet, idx);
            return true;
        }
        next = default;
        return false;
    }

    /// <summary>Tries to get the previous sibling field. Returns false if this is the first sibling.</summary>
    public bool TryGetPrev(out Field prev)
    {
        ushort idx = _Packet.GetFieldRef(_Index).PrevIndex;
        if (idx != FieldBody.NullIndex)
        {
            prev = new Field(_Packet, idx);
            return true;
        }
        prev = default;
        return false;
    }

    #endregion

    #region Iterators

    /// <summary>
    /// Iterates direct children of this field.
    /// When <paramref name="materialize"/> is true (default), lazy children are materialized first.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before iterating.</param>
    public FieldChildEnumerable Children(bool materialize = true) => new(_Packet, _Index, materialize);

    /// <summary>
    /// Iterates all descendants in depth-first pre-order.
    /// When <paramref name="materialize"/> is true (default), lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    public FieldDescendantEnumerable Descendants(bool materialize = true) => new(_Packet, _Index, materialize);

    #endregion

    #region Equality

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Field other) => ReferenceEquals(_Packet, other._Packet) && _Index == other._Index;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Field other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_Packet), _Index);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Field left, Field right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Field left, Field right) => !left.Equals(right);
    #endregion
}
