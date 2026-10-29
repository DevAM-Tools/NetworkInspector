// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class SomeIpProtocol
{
    #region Effect types

    /// <summary>
    /// The facts of one unit or one message that depend on other packets and are therefore decided once, by the first parse of the
    /// packet, and replayed by every later parse.
    /// <para>
    /// Only the first parse of a packet may change cross-packet state (segment reassembly, request pairing, learned ports); a
    /// re-parse on any thread must produce the tree of the first parse without touching that state, so it reads this record
    /// instead. The record is immutable and its arrays are never written after it is stored.
    /// </para>
    /// <para>
    /// One record serves two keys of the effect store. The key of a unit (the layer key of its first byte) carries the acceptance
    /// decision of a unit that arrived without a dispatch key. The key of a message (the layer key of that message's own first
    /// byte) carries the segment outcome and the timing pair of that message. Both are layer keys of a slice of the packet's
    /// buffers, which pack a buffer index and an offset, so every message of a unit has a distinct key without arithmetic on
    /// the caller's side. A message with no cross-packet fact stores nothing.
    /// </para>
    /// </summary>
    /// <param name="Accepted">The unit was accepted as SOME/IP (content-based detection, a decrypted hand-off or a learned port).</param>
    /// <param name="Learned">The unit was accepted because its port was learned from service discovery.</param>
    /// <param name="Tp">The segment outcome of a segmented message; <see cref="FragmentOutcome.NotRecorded"/> for any other message.</param>
    /// <param name="TimingRequestPacket">Packet id of the paired request, or -1 when the message is no response or has no request.</param>
    /// <param name="TimingDelayNanos">Delay between request and response in nanoseconds; valid when <paramref name="TimingRequestPacket"/> is not -1.</param>
    /// <param name="TimingPendingEvicted">The request of this response was forgotten because the pending table was full.</param>
    internal readonly record struct SomeIpMessageEffect(
        bool Accepted,
        bool Learned,
        FragmentOutcome Tp,
        int TimingRequestPacket,
        long TimingDelayNanos,
        bool TimingPendingEvicted)
    {
        /// <summary>The effect of a unit whose acceptance was decided and that carries no other cross-packet fact.</summary>
        /// <param name="accepted">Whether the unit was accepted.</param>
        /// <param name="learned">Whether the acceptance came from a learned port.</param>
        /// <returns>The effect with no segment outcome and no timing pair.</returns>
        internal static SomeIpMessageEffect ForAcceptance(bool accepted, bool learned) =>
            new(accepted, learned, FragmentOutcome.NotRecorded, -1, 0, false);
    }

    #endregion

    #region State of the first parse

    /// <summary>
    /// The cross-packet facts recorded by first parses. Sparse: a plain message that arrived through a port records nothing, so
    /// the store grows only with units that needed a decision.
    /// <para>
    /// <b>Thread-safety:</b> a single ordered writer (the first parse, guarded by <see cref="_Gate"/>) and lock-free readers once a
    /// slot is published. The lifetime is bound to this protocol instance and therefore to its stack: a new stack starts with an
    /// empty store, so two stacks built from different configurations never share a fact.
    /// </para>
    /// </summary>
    private readonly EffectStore<SomeIpMessageEffect> _Effects = new();

    /// <summary>
    /// First parse versus replay (<see cref="IngestGate"/>). Only the ordered first-parse path, which the session serializes under its
    /// parse lock, enters a scope; the outermost scope raises the watermark after every effect of the packet was recorded, so a
    /// reader that observes the raised watermark also observes the effects (release/acquire through the volatile write). Only
    /// the outermost scope raises it, because SOME/IP can be entered again for the same packet (a tunnel, a reassembled payload
    /// that carries SOME/IP); an inner scope that raised it would turn the rest of the packet into a replay that finds no effect.
    /// Rejected alternative: a lock around the effect store, which would make every re-parsing thread wait for the ingest thread.
    /// </summary>
    private readonly IngestGate _Gate = new();

    #endregion

    #region Effect store access

    /// <summary>
    /// Records an effect during the first parse. Must run before the scope of the packet ends (the outermost scope raises the
    /// watermark), so that a reader which sees the packet as parsed also finds the effect.
    /// </summary>
    private void _RecordEffect(PacketId id, int layerKey, in SomeIpMessageEffect effect) =>
        _Effects.Record(id.Value, layerKey, in effect);

    /// <summary>
    /// Finds the effect recorded for a layer key. Returns <see langword="false"/> when the first parse recorded none (the layer
    /// had no cross-packet fact, or its slice could not be keyed), in which case the caller takes the stateless path.
    /// </summary>
    private bool _TryGetEffect(PacketId id, int layerKey, out SomeIpMessageEffect effect) =>
        _Effects.TryGet(id.Value, layerKey, out effect);

    #endregion
}
