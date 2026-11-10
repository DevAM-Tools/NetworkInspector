// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// One produced field, valid only for the <see cref="IFieldObserver.OnField"/> call.
/// </summary>
/// <remarks>
/// <see cref="Value"/> refers to the stored value: the field's slab slot when the parse keeps a tree, the append argument
/// otherwise. Do not keep that reference.
/// On a skip parse <see cref="HasField"/> is false and <see cref="Field"/> is default.
/// On a build parse the cursor can see the parent and siblings already linked, not a sibling
/// appended later.
/// <para>
/// <b>Why a ref struct:</b> the visit is built on the parse path for every field a watch accepts. Holding the value and the
/// custom text as <c>ref readonly</c> avoids copying the <see cref="FieldValue"/> (which carries a custom-text reference) and the
/// <see cref="LazyString"/> into the visit and into the observer call, and a ref struct cannot be stored on the heap, captured by
/// a lambda, or kept across the call, which enforces the "valid only for this call" contract at compile time.
/// </para>
/// <para><b>Thread-safety:</b> not shareable; it lives on the parse thread's stack for the duration of one observer call.</para>
/// </remarks>
/// <seealso cref="IFieldObserver"/>
public readonly ref struct FieldVisit
{
    #region Visit state

    /// <summary>
    /// Reference to the value of this append, not a copy: the field's slab slot when the parse keeps a tree (a custom
    /// representation evaluated through it caches its string there for every later reader), the append argument otherwise.
    /// Valid only while the observer call is running.
    /// </summary>
    private readonly ref readonly FieldValue _Value;

    /// <summary>
    /// Reference to the location that holds the custom text: the field's slab slot when the parse keeps a tree, the append
    /// argument otherwise. A <see cref="LazyString"/> caches its evaluated string in the location it is read from, so reading
    /// through this reference (instead of a copy) caches into the slot that later readers use.
    /// </summary>
    private readonly ref readonly LazyString _CustomText;

    /// <summary>Creates a visit for one produced field. Internal: only the packet builds visits.</summary>
    /// <param name="packetId">Packet id of the frame being parsed.</param>
    /// <param name="fieldId">Identity of the produced field.</param>
    /// <param name="value">
    /// The location of the value (the slab slot of the field when <paramref name="hasField"/> is true); must stay valid for the
    /// whole observer call.
    /// </param>
    /// <param name="customText">
    /// The location of the custom text (default content when none); must stay valid for the whole observer call. The slab slot
    /// of the field when <paramref name="hasField"/> is true.
    /// </param>
    /// <param name="field">Tree cursor of the field, default when <paramref name="hasField"/> is false.</param>
    /// <param name="hasField">Whether <paramref name="field"/> can be navigated.</param>
    /// <remarks>
    /// An explicit internal constructor on purpose: a primary constructor has the visibility of the type (public), and a visit
    /// built outside the packet would refer to locations nobody guarantees for the observer call.
    /// </remarks>
    internal FieldVisit(
        PacketId packetId,
        FieldId fieldId,
        ref readonly FieldValue value,
        ref readonly LazyString customText,
        Field field,
        bool hasField)
    {
        PacketId = packetId;
        FieldId = fieldId;
        _Value = ref value;
        _CustomText = ref customText;
        Field = field;
        HasField = hasField;
    }

    /// <summary>Packet id of the frame being parsed.</summary>
    public PacketId PacketId { get; }

    /// <summary>Identity of the produced field.</summary>
    public FieldId FieldId { get; }

    /// <summary>The value stored for this field. Valid only for the observer call.</summary>
    public ref readonly FieldValue Value => ref _Value;

    /// <summary>
    /// Custom text set when the field was appended (default content when none), as a reference to the location that holds it.
    /// Valid only for the observer call. The reference points to the field's slab slot when <see cref="HasField"/> is true, so
    /// an evaluation through it replaces the deferred object in the slot by its string; copying the struct
    /// (<c>LazyString text = visit.CustomText;</c>) is cheap, keeps the deferred object, and shares the one build of the string
    /// with the slot and every other copy (see <see cref="LazyString"/>).
    /// </summary>
    /// <remarks>
    /// <b>Do not evaluate the string in <see cref="IFieldObserver.OnField"/>.</b> A dissector may complete a deferred text after
    /// the append (before the parse ends), and the first evaluation freezes the string for the field, the tree, and every copy.
    /// Keep the text for <see cref="IFieldObserver.EndPacket"/>: with a tree (<see cref="HasField"/>), keep <see cref="Field"/>
    /// and read <see cref="Field.CustomText"/> there, which sees a replaced text; without a tree, keep the copied struct, which
    /// is the only holder. In a tree-free parse the dissectors build gated texts only for fields that some observer's
    /// <see cref="ParseObserver.DisplayText"/> watch accepts.
    /// </remarks>
    /// <seealso cref="Field.CustomText"/>
    public ref readonly LazyString CustomText => ref _CustomText;

    /// <summary>Tree cursor when <see cref="HasField"/> is true. Default on a skip parse.</summary>
    public Field Field { get; }

    /// <summary>Whether <see cref="Field"/> can be navigated. False when the parse did not keep a tree.</summary>
    public bool HasField { get; }

    #endregion
}
