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
    public Packet Packet { get; }

    /// <summary>Index into the packet's flat field list.</summary>
    internal ushort StorageIndex { get; }

    /// <summary>Cached field identifier — fits in the padding between StorageIndex (2 bytes) and the next 8-byte slot.</summary>
    public FieldId FieldId { get; }

    /// <summary>Creates a field wrapper for the given packet and index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Field(Packet packet, ushort index)
    {
        Packet = packet;
        StorageIndex = index;
        FieldId = packet.GetFieldRef(index).FieldId;
    }

    /// <summary>Creates a field wrapper when the caller already knows <paramref name="fieldId"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Field(Packet packet, ushort index, FieldId fieldId)
    {
        Packet = packet;
        StorageIndex = index;
        FieldId = fieldId;
    }

    #region Validity

    /// <summary>Whether this field reference points to a valid packet and index.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet is not null && StorageIndex != FieldBody.NullIndex;
    }

    #endregion

    #region Public Accessors

    /// <summary>Gets the field's metadata from the stack registry.</summary>
    public FieldInfo? FieldInfo => Packet.Stack.GetField(FieldId);

    /// <summary>The field's value.</summary>
    public FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).Value;
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
        get => Packet.GetFieldRef(StorageIndex).CustomText;
    }

    /// <summary>Whether this is the root field (index 0).</summary>
    public bool IsRoot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StorageIndex == 0;
    }

    /// <summary>
    /// Whether this field has child fields.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// When <see langword="false"/>, an unmaterialized lazy container reports no children.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before checking.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasChildren(bool materialize)
    {
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        return Packet.GetFieldRef(StorageIndex).FirstChildIndex != FieldBody.NullIndex;
    }

    /// <summary>
    /// Number of direct children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// When <see langword="false"/>, an unmaterialized lazy container reports zero children.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before counting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ChildCount(bool materialize)
    {
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        return Packet.GetFieldRef(StorageIndex).ChildCount;
    }

    /// <summary>Whether this field is lazy (has deferred children that need materialization).
    /// Internal so the lazy mechanism stays transparent to external consumers.</summary>
    internal bool NeedsLazyMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).NeedsMaterialization;
    }

    #endregion

    #region Tree Navigation

    /// <summary>Tries to get the parent field. Returns false if this is a root field.</summary>
    public bool TryGetParent(out Field parent)
    {
        ushort parentIdx = Packet.GetFieldRef(StorageIndex).ParentIndex;
        if (parentIdx != FieldBody.NullIndex)
        {
            parent = new Field(Packet, parentIdx);
            return true;
        }
        parent = default;
        return false;
    }

    /// <summary>
    /// Tries to get the first child field. Returns false if there are no children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// Parent/sibling navigation does not take this parameter because it only follows index links.
    /// </summary>
    /// <param name="firstChild">The first child when present.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    public bool TryGetFirstChild(out Field firstChild, bool materialize)
    {
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        ushort idx = Packet.GetFieldRef(StorageIndex).FirstChildIndex;
        if (idx != FieldBody.NullIndex)
        {
            firstChild = new Field(Packet, idx);
            return true;
        }
        firstChild = default;
        return false;
    }

    /// <summary>
    /// Tries to get the last child field. Returns false if there are no children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// </summary>
    /// <param name="lastChild">The last child when present.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    public bool TryGetLastChild(out Field lastChild, bool materialize)
    {
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        ushort idx = Packet.GetFieldRef(StorageIndex).LastChildIndex;
        if (idx != FieldBody.NullIndex)
        {
            lastChild = new Field(Packet, idx);
            return true;
        }
        lastChild = default;
        return false;
    }

    /// <summary>Tries to get the next sibling field. Returns false if this is the last sibling.</summary>
    public bool TryGetNext(out Field next)
    {
        ushort idx = Packet.GetFieldRef(StorageIndex).NextIndex;
        if (idx != FieldBody.NullIndex)
        {
            next = new Field(Packet, idx);
            return true;
        }
        next = default;
        return false;
    }

    /// <summary>Tries to get the previous sibling field. Returns false if this is the first sibling.</summary>
    public bool TryGetPrev(out Field prev)
    {
        ushort idx = Packet.GetFieldRef(StorageIndex).PrevIndex;
        if (idx != FieldBody.NullIndex)
        {
            prev = new Field(Packet, idx);
            return true;
        }
        prev = default;
        return false;
    }

    #endregion

    #region Iterators

    /// <summary>
    /// Iterates direct children of this field.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are materialized first.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before iterating.</param>
    public FieldChildEnumerable Children(bool materialize) => new(Packet, StorageIndex, materialize);

    /// <summary>
    /// Iterates all descendants in depth-first pre-order.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    public FieldDescendantEnumerable Descendants(bool materialize) => new(Packet, StorageIndex, materialize);

    #endregion

    #region Equality

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Field other) => ReferenceEquals(Packet, other.Packet) && StorageIndex == other.StorageIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Field other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Packet), StorageIndex);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Field left, Field right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Field left, Field right) => !left.Equals(right);
    #endregion
}
