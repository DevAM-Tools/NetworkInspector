// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// One produced field, valid only for the <see cref="IFieldObserver.OnField"/> call.
/// </summary>
/// <remarks>
/// <see cref="Value"/> aliases the value the parse just stored. Do not keep that reference.
/// On a skip parse <see cref="HasField"/> is false and <see cref="Field"/> is default.
/// On a build parse the cursor can see the parent and siblings already linked, not a sibling
/// appended later.
/// <para>
/// <b>Why a ref struct:</b> the visit is built on the parse path for every field a watch accepts. Holding the value as a
/// <c>ref readonly</c> avoids copying the <see cref="FieldValue"/> (which carries a custom-text reference) into the visit and
/// into the observer call, and a ref struct cannot be stored on the heap, captured by a lambda, or kept across the call,
/// which enforces the "valid only for this call" contract at compile time.
/// </para>
/// <para><b>Thread-safety:</b> not shareable; it lives on the parse thread's stack for the duration of one observer call.</para>
/// </remarks>
public readonly ref struct FieldVisit
{
    #region Fields

    // Reference to the value the parse is appending (the argument passed down by the packet), not a copy. It is only
    // valid while the observer call is running; nothing guarantees the referenced location afterwards.
    private readonly ref readonly FieldValue _Value;

    #endregion

    #region Construction

    /// <summary>Creates a visit for one produced field. Internal: only the packet builds visits.</summary>
    internal FieldVisit(
        PacketId packetId,
        FieldId fieldId,
        ref readonly FieldValue value,
        Field field,
        bool hasField)
    {
        PacketId = packetId;
        FieldId = fieldId;
        _Value = ref value;
        Field = field;
        HasField = hasField;
    }

    #endregion

    #region Properties

    /// <summary>Packet id of the frame being parsed.</summary>
    public PacketId PacketId { get; }

    /// <summary>Identity of the produced field.</summary>
    public FieldId FieldId { get; }

    /// <summary>The value stored for this field. Valid only for the observer call.</summary>
    public ref readonly FieldValue Value => ref _Value;

    /// <summary>Tree cursor when <see cref="HasField"/> is true. Default on a skip parse.</summary>
    public Field Field { get; }

    /// <summary>Whether <see cref="Field"/> can be navigated. False when the parse did not keep a tree.</summary>
    public bool HasField { get; }

    #endregion
}
