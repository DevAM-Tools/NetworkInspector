// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Internal storage for a single field in the packet tree.
/// Uses <see cref="ushort"/> indices for tree links (max 65535 fields per packet).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FieldBody
{
    #region Fields

    internal const ushort NullIndex = ushort.MaxValue;

    private FieldValue _Value;
    private LazyString _CustomText;
    internal readonly FieldId FieldId { get; }
    private ushort _ParentIndex;
    private ushort _FirstChildIndex;
    private ushort _LastChildIndex;
    private ushort _NextIndex;
    private ushort _PrevIndex;
    private ushort _ChildCount;

    // Lazy field support: 1-based index into Packet._LazyPopulators array.
    // 0 = not lazy (or already materialized). Bit 15 set = materialization in progress.
    // Stored as int so Interlocked.CompareExchange works on the volatile field without Unsafe.As.
    private volatile int _LazyIndex;

    /// <summary>Bit set on <see cref="_LazyIndex"/> while a lazy field is being materialized.</summary>
    internal const ushort LazyIndexMaterializingBit = 0x8000;

    #endregion

    #region Constructors

    /// <summary>Creates a field body node with a value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldBody(FieldId fieldId, FieldValue value)
    {
        _Value = value;
        _CustomText = default;
        FieldId = fieldId;
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
        FieldId = fieldId;
        _ParentIndex = NullIndex;
        _FirstChildIndex = NullIndex;
        _LastChildIndex = NullIndex;
        _NextIndex = NullIndex;
        _PrevIndex = NullIndex;
        _ChildCount = 0;
        _LazyIndex = 0;
    }

    #endregion

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetCustomText(LazyString text) => _CustomText = text;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearCustomText() => _CustomText = default;

    #region Tree links

    /// <summary>Index of the parent field in the packet tree, or <see cref="NullIndex"/> if none.</summary>
    internal ushort ParentIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _ParentIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _ParentIndex = value;
    }

    /// <summary>Index of the first child field, or <see cref="NullIndex"/> if this node has no children.</summary>
    internal ushort FirstChildIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _FirstChildIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _FirstChildIndex = value;
    }

    /// <summary>Index of the last child field, or <see cref="NullIndex"/> if this node has no children.</summary>
    internal ushort LastChildIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _LastChildIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _LastChildIndex = value;
    }

    /// <summary>Index of the next sibling field, or <see cref="NullIndex"/> if this is the last sibling.</summary>
    internal ushort NextIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _NextIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _NextIndex = value;
    }

    /// <summary>Index of the previous sibling field, or <see cref="NullIndex"/> if this is the first sibling.</summary>
    internal ushort PrevIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _PrevIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _PrevIndex = value;
    }

    /// <summary>Number of direct child fields linked from this node.</summary>
    internal ushort ChildCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => _ChildCount;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _ChildCount = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementChildCount()
    {
        if (_ChildCount == ushort.MaxValue)
        {
            throw new OverflowException("Field child count exceeded maximum of 65535.");
        }
        _ChildCount++;
    }

    #endregion

    #region Lazy field support

    /// <summary>
    /// The 1-based index into the Packet's lazy populator array.
    /// 0 means this field is not lazy or has already been materialized.
    /// </summary>
    internal ushort LazyIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => (ushort)_LazyIndex;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _LazyIndex = value;
    }

    /// <summary>Whether this field needs materialization (lazy and not yet populated).</summary>
    internal bool NeedsMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ushort lazyIndex = ReadLazyIndexVolatile();
            return lazyIndex > 0 && (lazyIndex & LazyIndexMaterializingBit) == 0;
        }
    }

    /// <summary>Volatile read of the raw lazy-index word (includes materializing marker).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort ReadLazyIndexVolatile() => (ushort)_LazyIndex;

    /// <summary>Whether another thread is currently materializing this lazy field.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsLazyMaterializationInProgress() =>
        (ReadLazyIndexVolatile() & LazyIndexMaterializingBit) != 0;

    /// <summary>
    /// Atomically claims lazy materialization for this field. Returns the 1-based populator index on success.
    /// </summary>
    internal bool TryClaimLazyMaterialization(out ushort lazyPopulatorIndex)
    {
        int current = _LazyIndex;
        if (current == 0 || (current & LazyIndexMaterializingBit) != 0)
        {
            lazyPopulatorIndex = 0;
            return false;
        }

        int materializing = current | LazyIndexMaterializingBit;
        if (Interlocked.CompareExchange(ref _LazyIndex, materializing, current) != current)
        {
            lazyPopulatorIndex = 0;
            return false;
        }

        lazyPopulatorIndex = (ushort)current;
        return true;
    }

    /// <summary>Marks lazy materialization complete (populator index cleared).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void FinishLazyMaterialization() => _LazyIndex = 0;

    internal void AppendCustomText(LazyString suffix) => _CustomText = !_CustomText.IsNull ? _CustomText.Append(suffix) : suffix;

    #endregion
}
