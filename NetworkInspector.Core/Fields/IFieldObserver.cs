// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Receives fields as a parse produces them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BeginPacket"/> runs on the parse thread before any <see cref="OnField"/> for that
/// frame. <see cref="FieldVisit"/> is valid only for the <see cref="OnField"/> call.
/// A <see cref="Field"/> copied out of the visit must not be held across a later recycle of that
/// packet; that was already unsupported for any field handle.
/// </para>
/// <para>
/// <b>Failure containment:</b> an observer is user code running inside the parse. An exception thrown from
/// <see cref="BeginPacket"/> or <see cref="OnField"/> does not fail the parse: the packet catches it, records it as a
/// caught error, and stops all further observer calls for that packet. Implementations should therefore not rely on
/// throwing to abort a parse and must tolerate being muted after a failure.
/// </para>
/// <para>
/// <b>Cost:</b> <see cref="OnField"/> runs once per accepted field on the parse path. Which fields are accepted is decided
/// by the <see cref="FieldWatch"/> paired with the observer in <see cref="ParseObserver"/> before the visit is built, so an
/// observer should be paired with the narrowest watch it needs.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the parse calls one observer from the thread that called
/// <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>.
/// Do not pass one instance into two overlapping parses.
/// </para>
/// </remarks>
public interface IFieldObserver
{
    #region Public API

    /// <summary>A frame is starting. No field of this frame has been reported yet.</summary>
    /// <param name="id">The packet id assigned to this frame.</param>
    /// <param name="timestamp">The frame timestamp. Flank uses it as the sample clock.</param>
    void BeginPacket(PacketId id, Timestamp timestamp);

    /// <summary>One produced field. Not called for the synthetic packet root.</summary>
    /// <param name="visit">Identity, value, and, when a tree was kept, a cursor. Valid only for this call.</param>
    void OnField(in FieldVisit visit);

    #endregion
}
