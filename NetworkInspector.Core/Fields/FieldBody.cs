// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Internal storage for a single field in the packet tree.
/// Uses <see cref="ushort"/> indices for tree links instead of object references: a slot is plain data without GC
/// references to other slots, links cost 2 bytes instead of 8, and the slab can be cleared and reused as a whole.
/// <para>
/// The usable indices are <c>0 … 0xFFFD</c> (at most 65 534 fields per packet): <see cref="NullIndex"/> (0xFFFF) and
/// <see cref="SkipStorageIndex"/> (0xFFFE) are reserved sentinels, so a count that reaches them is rejected by the packet.
/// </para>
/// Tree-link stores and loads use <see cref="Volatile"/> so a <c>materialize: false</c>
/// walker on another thread cannot observe a default <see cref="FieldId"/> after a parent
/// publishes a child pointer. <see cref="LazyIndex"/> is a separate CAS word.
/// <para>
/// <b>Thread-safety:</b> a mutable struct that lives in the packet's slab and is accessed by reference. Not
/// synchronized on its own: the single parse thread (before Seal) or the thread that claimed a lazy field (after Seal)
/// writes; other threads only read the volatile link fields of slots below the packet's published field count.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FieldBody
{
    #region Fields

    /// <summary>"No link" sentinel (0xFFFF) for every tree link; never a valid slot index.</summary>
    internal const ushort NullIndex = ushort.MaxValue;

    /// <summary>
    /// Storage index returned by skip-tree appends. Distinct from <see cref="NullIndex"/> so
    /// <see cref="MutField.IsValid"/> stays true. Not a slab slot.
    /// </summary>
    internal const ushort SkipStorageIndex = unchecked((ushort)(ushort.MaxValue - 1)); // 0xFFFE

    private FieldValue _Value;
    private LazyString _CustomText;

    /// <summary>Identifier of the field definition; set once at construction and never changed.</summary>
    internal readonly FieldId FieldId { get; }

    // The six link fields below are plain ushort fields accessed only through the Volatile properties in the
    // "Tree links" region, so a reader that sees a link also sees the slot it points to.
    private ushort _ParentIndex;
    private ushort _FirstChildIndex;
    private ushort _LastChildIndex;
    private ushort _NextIndex;
    private ushort _PrevIndex;
    private ushort _ChildCount;

    // Lazy field support: 1-based index into Packet._LazyPopulators array.
    // 0 = not lazy (or already materialized). Bit 15 set = materialization in progress, so a populator index uses
    // the low 15 bits only.
    // Stored as int so Interlocked.CompareExchange works on the volatile field without Unsafe.As.
    private volatile int _LazyIndex;

    /// <summary>Bit set on <see cref="_LazyIndex"/> while a lazy field is being materialized.</summary>
    internal const ushort LazyIndexMaterializingBit = 0x8000;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a field body node with a value and no links. Plain stores, as in the other constructors: the node reaches
    /// other threads only through the packet's published field count.
    /// </summary>
    /// <param name="fieldId">Identifier of the field definition.</param>
    /// <param name="value">The field value.</param>
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

    /// <summary>
    /// Creates a detached child node for the shared insert path: value, optional custom text (default = none), and
    /// parent; all sibling and child links <see cref="NullIndex"/>, child count 0, not lazy. Plain stores: the node is
    /// published to other threads only through the packet's field-count release store.
    /// </summary>
    /// <param name="fieldId">Identifier of the field definition.</param>
    /// <param name="value">The field value.</param>
    /// <param name="customText">Custom display text, or <c>default</c> for none.</param>
    /// <param name="parentIndex">Storage index of the parent, or <see cref="NullIndex"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FieldBody(FieldId fieldId, FieldValue value, LazyString customText, ushort parentIndex)
    {
        _Value = value;
        _CustomText = customText;
        FieldId = fieldId;
        _ParentIndex = parentIndex;
        _FirstChildIndex = NullIndex;
        _LastChildIndex = NullIndex;
        _NextIndex = NullIndex;
        _PrevIndex = NullIndex;
        _ChildCount = 0;
        _LazyIndex = 0;
    }

    /// <summary>Creates a field body node with no value (container).</summary>
    /// <param name="fieldId">Identifier of the field definition.</param>
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

    /// <summary>
    /// The storage location of the field value. Plain read (no volatile): the value is written before the slot becomes visible
    /// to other threads through the packet's published field count.
    /// <para>
    /// <b>Why a reference:</b> the value carries a custom representation inline. The string of a deferred representation is
    /// built once however the value is read (the deferred object remembers it, see <see cref="LazyString"/>); reading through
    /// this reference additionally replaces the deferred object in the slab slot by its string, which releases the object and
    /// its captured state. A copy of the value never does that.
    /// </para>
    /// </summary>
    /// <seealso cref="SetValue"/>
    // readonly: a call through "ref readonly FieldBody" must not take a defensive copy, or the slot would never be replaced by its string.
    internal readonly ref readonly FieldValue Value
    {
        // UnscopedRef: see CustomText.
        [UnscopedRef]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _Value;
    }

    /// <summary>Replaces the field value. Plain store, not synchronized.</summary>
    /// <param name="value">The new value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetValue(FieldValue value) => _Value = value;

    /// <summary>
    /// The storage location of the custom display text (absent: <see cref="LazyString.IsNull"/>). Reading it never evaluates a
    /// deferred text.
    /// <para>
    /// <b>Why a reference:</b> the string of a deferred text is built once however it is read (the deferred object remembers
    /// it, see <see cref="LazyString"/>), and only for a reader that needs the string; a reader that checks
    /// <see cref="LazyString.IsNull"/> or hands the text on pays nothing. Evaluating through this reference
    /// (<c>body.CustomText.AsString</c>) additionally replaces the deferred object in the slot by its string, which releases the
    /// object and its captured state; the read of a copy leaves the slot holding the object.
    /// </para>
    /// <para>
    /// <b>Lifetime:</b> valid as long as the slab chunk that holds this slot (chunks never move). A later
    /// <see cref="SetCustomText"/> replaces the content, so the reference sees the replacement, not the old text. A copy
    /// (<c>LazyString text = body.CustomText;</c>) keeps the text that was stored when it was taken.
    /// </para>
    /// </summary>
    /// <seealso cref="FieldVisit.CustomText"/>
    // readonly: a call through "ref readonly FieldBody" must not take a defensive copy, or the slot would never be replaced by its string.
    internal readonly ref readonly LazyString CustomText
    {
        // UnscopedRef: a struct member returns a reference to its own field only when it opts out of the implicit
        // "this is scoped" rule (CS8170). The callers hold the FieldBody by ref (a slab slot), so the reference stays valid.
        [UnscopedRef]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _CustomText;
    }

    /// <summary>Replaces the custom display text. Plain store, not synchronized.</summary>
    /// <param name="text">New text; <c>default</c> clears it.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetCustomText(LazyString text) => _CustomText = text;

    /// <summary>Removes the custom display text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearCustomText() => _CustomText = default;

    #region Tree links

    // Every link is read with Volatile.Read (acquire) and written with Volatile.Write (release). A writer links a fully
    // initialized slot into the tree last, so a concurrent walker that follows a link finds an initialized FieldBody.
    // Alternative rejected: a lock around tree walks, which would serialize every reader of a shared packet.

    /// <summary>Index of the parent field in the packet tree, or <see cref="NullIndex"/> if none.</summary>
    internal ushort ParentIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _ParentIndex);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _ParentIndex, value);
    }

    /// <summary>Index of the first child field, or <see cref="NullIndex"/> if this node has no children.</summary>
    internal ushort FirstChildIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _FirstChildIndex);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _FirstChildIndex, value);
    }

    /// <summary>Index of the last child field, or <see cref="NullIndex"/> if this node has no children.</summary>
    internal ushort LastChildIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _LastChildIndex);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _LastChildIndex, value);
    }

    /// <summary>Index of the next sibling field, or <see cref="NullIndex"/> if this is the last sibling.</summary>
    internal ushort NextIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _NextIndex);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _NextIndex, value);
    }

    /// <summary>Index of the previous sibling field, or <see cref="NullIndex"/> if this is the first sibling.</summary>
    internal ushort PrevIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _PrevIndex);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _PrevIndex, value);
    }

    /// <summary>Number of direct child fields linked from this node.</summary>
    internal ushort ChildCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _ChildCount);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Volatile.Write(ref _ChildCount, value);
    }

    /// <summary>
    /// Increments <see cref="ChildCount"/> unless it is already <see cref="ushort.MaxValue"/>.
    /// Saturation is silent: the reserved slot is still published as a flat tombstone if the
    /// caller wraps the insert in try/finally. One writer owns a parent at a time (the claiming
    /// materializer), so a volatile load/store is enough.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementChildCount()
    {
        ushort current = Volatile.Read(ref _ChildCount);
        if (current == ushort.MaxValue)
        {
            return;
        }

        Volatile.Write(ref _ChildCount, (ushort)(current + 1));
    }

    #endregion

    #region Lazy field support

    /// <summary>
    /// The 1-based index into the Packet's lazy populator array.
    /// 0 means this field is not lazy or has already been materialized.
    /// The getter includes <see cref="LazyIndexMaterializingBit"/> while a materialization is in progress. Reads and
    /// writes go through the volatile field, so they are ordered with <see cref="TryClaimLazyMaterialization"/>.
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

    /// <summary>
    /// Whether a populator of this field is still to run <i>or is running right now</i> on some thread. This is the guard
    /// of every reader that is about to read the child list with <c>materialize: true</c>: such a reader must enter
    /// <see cref="Packet.MaterializeLazyField"/> also while another thread is populating, because that is where it waits
    /// for the populator to finish. Rejected: guarding with <see cref="NeedsMaterialization"/> (false while another thread
    /// populates), which let a second reader skip the wait and read a child list the first thread had not finished
    /// (found as a rare missing-children failure of the eight-thread lazy-growth test). Residual risk: the wait is a spin,
    /// bounded by the length of one populator; a populator never waits (see <see cref="Packet.MaterializeLazyField"/>).
    /// </summary>
    internal bool IsLazyPending
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ReadLazyIndexVolatile() != 0;
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
    /// A single compare-exchange sets the materializing bit, so of several threads that try to materialize the same
    /// field exactly one wins and populates it; the others get <see langword="false"/> immediately. This method never
    /// waits: the caller (<see cref="Packet.MaterializeLazyField"/>) decides whether to spin until the winner finishes
    /// (see <see cref="IsLazyMaterializationInProgress"/>).
    /// </summary>
    /// <param name="lazyPopulatorIndex">The populator index (without the marker bit) when the claim succeeded; 0 otherwise.</param>
    internal bool TryClaimLazyMaterialization(out ushort lazyPopulatorIndex)
    {
        // Not lazy (0), already being materialized, or already finished: nothing to claim.
        int current = _LazyIndex;
        if (current == 0 || (current & LazyIndexMaterializingBit) != 0)
        {
            lazyPopulatorIndex = 0;
            return false;
        }

        // Compare against the value just read, so a concurrent claim that changed the word makes this attempt fail
        // instead of overwriting the other thread's marker.
        int materializing = current | LazyIndexMaterializingBit;
        if (Interlocked.CompareExchange(ref _LazyIndex, materializing, current) != current)
        {
            lazyPopulatorIndex = 0;
            return false;
        }

        lazyPopulatorIndex = (ushort)current;
        return true;
    }

    /// <summary>
    /// Marks lazy materialization complete (populator index cleared). The volatile write publishes the children linked
    /// by the populator to threads that then observe the cleared word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void FinishLazyMaterialization() => _LazyIndex = 0;

    /// <summary>Appends <paramref name="suffix"/> to the existing custom text, or sets it when there is none. Not synchronized.</summary>
    /// <param name="suffix">Text to append.</param>
    internal void AppendCustomText(LazyString suffix) => _CustomText = !_CustomText.IsNull ? _CustomText.Append(suffix) : suffix;

    #endregion
}
