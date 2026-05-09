// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Internal storage for a single field in the packet tree.
/// Uses <see cref="ushort"/> indices for tree links (max 65535 fields per packet).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FieldBody
{
    internal const ushort NullIndex = ushort.MaxValue;

    private FieldValue _Value;
    private LazyString _CustomText;
    private readonly FieldId _FieldId;
    private ushort _ParentIndex;
    private ushort _FirstChildIndex;
    private ushort _LastChildIndex;
    private ushort _NextIndex;
    private ushort _PrevIndex;
    private ushort _ChildCount;

    // Lazy field support: 1-based index into Packet._LazyPopulators array.
    // 0 = not lazy (or already materialized). After materialization, reset to 0.
    private ushort _LazyIndex;

    /// <summary>Creates a field body node with a value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldBody(FieldId fieldId, FieldValue value)
    {
        _Value = value;
        _CustomText = default;
        _FieldId = fieldId;
        _ParentIndex = NullIndex;
        _FirstChildIndex = NullIndex;
        _LastChildIndex = NullIndex;
        _NextIndex = NullIndex;
        _PrevIndex = NullIndex;
        _ChildCount = 0;
        _LazyIndex = 0;
    }

    /// <summary>Creates a field body node with no value (container).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldBody(FieldId fieldId)
    {
        _Value = FieldValue.None;
        _CustomText = default;
        _FieldId = fieldId;
        _ParentIndex = NullIndex;
        _FirstChildIndex = NullIndex;
        _LastChildIndex = NullIndex;
        _NextIndex = NullIndex;
        _PrevIndex = NullIndex;
        _ChildCount = 0;
        _LazyIndex = 0;
    }

    internal readonly FieldId FieldId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _FieldId;
    }
    internal FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _Value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _Value = value;
    }
    /// <summary>
    /// Optional custom display text. Check <see cref="LazyString.IsNull"/> for absence.
    /// <para>
    /// <b>⚠ Side-effecting getter with in-place materialization:</b> This getter is deliberately
    /// NOT marked <c>readonly</c>. When called through a <b>mutable ref</b> to the
    /// <see cref="FieldBody"/> (e.g. <c>array[i].CustomText</c>), <c>_CustomText</c> is
    /// accessed by mutable ref — no defensive copy. <see cref="LazyString.AsString"/> then
    /// uses <c>Unsafe.AsRef</c> + <c>Interlocked.CompareExchange</c> to atomically replace
    /// a <c>Func&lt;string&gt;</c> factory with the evaluated string in the actual storage.
    /// Subsequent reads see the cached result without re-evaluating the factory.
    /// This mutation is the intentional design: the getter caches the result in-place so the
    /// factory is only invoked once regardless of how many times the getter is called.
    /// </para>
    /// <para>
    /// <b>Warning:</b> Calling through <c>ref readonly FieldBody</c> would cause a defensive
    /// copy, and the CAS swap would apply to the copy only (caching lost). All read-side
    /// callers (<see cref="Field.CustomText"/>, <see cref="MutField.CustomText"/>) use
    /// <see cref="Packet.GetFieldRef"/><c>(index)</c> which yields a mutable ref.
    /// </para>
    /// </summary>
    internal LazyString CustomText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Trigger evaluation on the actual storage so the CAS swap persists.
            // Cost for already-cached or null: one type-check branch (optimized by JIT).
            _ = _CustomText.AsString;
            return _CustomText;
        }
    }

    internal ushort ParentIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _ParentIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _ParentIndex = value;
    }
    internal ushort FirstChildIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _FirstChildIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _FirstChildIndex = value;
    }
    internal ushort LastChildIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _LastChildIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _LastChildIndex = value;
    }
    internal ushort NextIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _NextIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _NextIndex = value;
    }
    internal ushort PrevIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _PrevIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _PrevIndex = value;
    }
    internal ushort ChildCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _ChildCount;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _ChildCount = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetCustomText(LazyString text) => _CustomText = text;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearCustomText() => _CustomText = default;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementChildCount()
    {
        if (_ChildCount == ushort.MaxValue)
        {
            throw new OverflowException("Field child count exceeded maximum of 65535.");
        }
        _ChildCount++;
    }

    #region Lazy field support

    /// <summary>
    /// The 1-based index into the Packet's lazy populator array.
    /// 0 means this field is not lazy or has already been materialized.
    /// </summary>
    internal ushort LazyIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _LazyIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _LazyIndex = value;
    }

    /// <summary>Whether this field needs materialization (lazy and not yet populated).</summary>
    internal readonly bool NeedsMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _LazyIndex > 0;
    }

    internal void AppendCustomText(LazyString suffix) => _CustomText = !_CustomText.IsNull ? _CustomText.Append(suffix) : suffix;
    #endregion
}
