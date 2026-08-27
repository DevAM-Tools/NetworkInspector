// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class UdpProtocol
{
    #region Effect types

    /// <summary>
    /// Immutable UDP stream assignment recorded during the first parse of a packet so that every
    /// later parse of the same packet id can replay it without touching the stream tracker.
    /// </summary>
    public readonly record struct StreamEffect(uint StreamIndex);

    #endregion

    #region Effect store (protocol-owned, keyed by (PacketId, LayerKey))

    /// <summary>
    /// Stream indices recorded during first parses. Sparse: only packets where UDP actually ran get
    /// an entry. Layer key is <see cref="Packet.GetEffectLayerKey"/> at the parse call.
    /// <para>
    /// <b>Thread-safety:</b> single ordered first-parse writer; lock-free readers once the entry is
    /// published. Lifetime is bound to this protocol instance and therefore to its
    /// <see cref="Stack"/> — a stack swap creates fresh protocol instances with empty stores.
    /// </para>
    /// </summary>
    private readonly EffectStore<StreamEffect> _Effects = new();

    /// <summary>
    /// Highest packet id that completed a first parse; <c>-1</c> until the first packet was parsed.
    /// <para>
    /// The single writer is the ordered first-parse path (<see cref="_RaiseWatermark"/>), which the
    /// session serializes under its parse lock. Readers use <see cref="_IsReplay"/>. Written after
    /// the effect slots of that packet, so a reader that observes the raised watermark also observes
    /// the effects (release/acquire via <see langword="volatile"/>).
    /// </para>
    /// <para>
    /// <see cref="PacketId.Value"/> is an <see cref="int"/>, so this is <see langword="volatile"/>
    /// <see cref="int"/> with plain reads and writes — same pattern as
    /// <see cref="Stack"/>'s parse watermark.
    /// </para>
    /// </summary>
    private volatile int _IngestWatermark = -1;

    /// <summary>
    /// In-flight first-parse nesting for this protocol on the serialized ingest thread.
    /// Replay does not touch this field. The outermost finally raises the watermark.
    /// </summary>
    private int _ParseNesting;

    #endregion

    #region Effect store access

    /// <summary>Records the stream effect of one UDP layer during its first parse.</summary>
    private void _RecordStreamEffect(PacketId id, int layerKey, StreamEffect effect) =>
        _Effects.Record(id.Value, layerKey, effect);

    /// <summary>
    /// Reads the stream effect recorded for the given layer.
    /// Returns <see langword="false"/> when no first parse recorded an effect for it, which makes
    /// the caller degrade to a stateless path instead of touching the tracker.
    /// </summary>
    private bool _TryGetStreamEffect(PacketId id, int layerKey, out StreamEffect effect) =>
        _Effects.TryGet(id.Value, layerKey, out effect);

    /// <summary>
    /// Raises the first-parse watermark monotonically. Only the ordered first-parse path calls this,
    /// so a plain read-then-write is sufficient — no competing writer can interleave.
    /// </summary>
    private void _RaiseWatermark(PacketId id)
    {
        if (id.Value > _IngestWatermark)
        {
            _IngestWatermark = id.Value;
        }
    }

    /// <summary>
    /// Whether the given packet id was already parsed once and must therefore replay its recorded
    /// effects instead of mutating cross-packet state.
    /// </summary>
    private bool _IsReplay(PacketId id) => id.Value <= _IngestWatermark;

    #endregion
}
