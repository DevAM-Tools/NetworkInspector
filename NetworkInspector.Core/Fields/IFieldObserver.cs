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
/// packet; no field handle survives a recycle.
/// </para>
/// <para>
/// <b>Failure containment:</b> an observer is user code running inside the parse. An exception thrown from
/// <see cref="BeginPacket"/> or <see cref="OnField"/> does not fail the parse: the packet catches it, records it as a
/// caught error, and stops all further <see cref="BeginPacket"/> and <see cref="OnField"/> calls for that packet
/// (<see cref="EndPacket"/> still follows, with <c>complete</c> false). Implementations should therefore not rely on
/// throwing to abort a parse and must tolerate being muted after a failure.
/// </para>
/// <para>
/// <b>Cost:</b> <see cref="OnField"/> runs once per accepted field on the parse path. Which fields are accepted is decided
/// by the <see cref="FieldWatch"/> paired with the observer in <see cref="ParseObserver"/> before the visit is built, so an
/// observer should be paired with the narrowest watch it needs.
/// </para>
/// <para>
/// <b>Replays:</b> observers are called on every parse of a frame, including replays; the value cache records only the
/// first parse unless <see cref="ParseOptions.RecordOnReplay"/> is set, and the index records only the first parse.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the parse calls one observer from the thread that called
/// <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>.
/// Do not pass one instance into two overlapping parses.
/// </para>
/// </remarks>
public interface IFieldObserver
{
    /// <summary>A frame is starting. No field of this frame has been reported yet.</summary>
    /// <param name="id">The packet id assigned to this frame.</param>
    /// <param name="timestamp">The frame timestamp.</param>
    void BeginPacket(PacketId id, Timestamp timestamp);

    /// <summary>One produced field. Not called for the synthetic packet root.</summary>
    /// <param name="visit">Identity, value, custom text, and, when a tree was kept, a cursor. Valid only for this call.</param>
    void OnField(in FieldVisit visit);

    /// <summary>
    /// The parse of this frame ended — also when the dissectors recorded parse errors (those are fields) — and the packet is
    /// not sealed yet. Called exactly once for every <see cref="BeginPacket"/> that ran, on the parse thread.
    /// </summary>
    /// <param name="id">The packet id of the matching <see cref="BeginPacket"/>.</param>
    /// <param name="complete">
    /// <see langword="false"/> when the observers of this packet were muted (an observer threw in BeginPacket or OnField) or an
    /// exception escaped the dissector boundaries (for example from the end-of-parse step of a value cache); the observer then
    /// did not receive every field and must not treat the packet as complete. <see langword="true"/> does not mean "no error":
    /// a dissector that threw or returned an error is contained, the packet ends complete, and the error is counted in
    /// <see cref="Packet.ErrorCount"/>.
    /// </param>
    /// <remarks>
    /// <b>Why:</b> a consumer that collects fields during the parse (a tree-free export) needs a point where the packet's
    /// fields are complete, to close or discard what it staged and to evaluate deferred custom texts — a deferred text may
    /// be completed by the dissector after the field was appended, but always before this call. Every observer implements
    /// it: the member is required, so an observer that forgets it fails to compile instead of leaking staged state. An
    /// observer with no per-packet state leaves the body empty. An exception thrown here is recorded like the other observer
    /// failures: as a <c>packet.error</c> field when the parse keeps a tree, and in an attached value cache that records
    /// <c>packet.error</c>; a parse without tree and without such a cache still counts it in <see cref="Packet.ErrorCount"/>.
    /// The remaining observers still get their call. Fields that an end call expands (a tree read with
    /// <c>materialize: true</c>) reach the tree and the cache, but no observer: the observers are detached before the first
    /// end call.
    /// </remarks>
    void EndPacket(PacketId id, bool complete);
}
