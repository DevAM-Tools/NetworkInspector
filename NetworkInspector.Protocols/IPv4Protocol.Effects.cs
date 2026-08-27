// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class IPv4Protocol
{
    #region Effect types

    /// <summary>
    /// Outcome of feeding one IPv4 fragment into the defragmenter, recorded during the first parse of
    /// that fragment so later parses can reproduce it without touching the fragment buffers.
    /// <see cref="ReassembledDatagram"/> is non-<see langword="null"/> only for the fragment that
    /// completed a datagram.
    /// </summary>
    private readonly record struct DefragLayerEffect(byte[]? ReassembledDatagram);

    #endregion

    #region Effect store (protocol-owned, keyed by (PacketId, LayerKey))

    /// <summary>
    /// Defragmentation outcomes recorded during first parses. Sparse: only fragmented packets get
    /// an entry. Layer key is <see cref="Packet.GetEffectLayerKey"/> at the parse call.
    /// <para>
    /// <b>Thread-safety:</b> single ordered first-parse writer; lock-free readers once the entry is
    /// published.
    /// </para>
    /// </summary>
    private readonly EffectStore<DefragLayerEffect> _Effects = new();

    /// <summary>
    /// Highest packet id that completed a first parse; <c>-1</c> until the first packet was parsed.
    /// <para>
    /// The single writer is the ordered first-parse path (<see cref="_RaiseWatermark"/>). Written
    /// after the effect slot of that packet, so a reader that observes the raised watermark also
    /// observes the effect (release/acquire via <see langword="volatile"/>).
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

    /// <summary>Records the defragmentation outcome of one IPv4 layer during its first parse.</summary>
    private void _RecordDefragEffect(PacketId id, int layerKey, byte[]? reassembled) =>
        _Effects.Record(id.Value, layerKey, new DefragLayerEffect(reassembled));

    /// <summary>
    /// Finds the defragmentation outcome recorded for the given layer, or <see langword="null"/> when
    /// the layer never reached the defragmenter.
    /// </summary>
    private byte[]? _FindReassembledDatagram(PacketId id, int layerKey) =>
        _Effects.TryGet(id.Value, layerKey, out DefragLayerEffect effect)
            ? effect.ReassembledDatagram
            : null;

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
    /// defragmentation outcome instead of feeding the shared fragment buffers again.
    /// </summary>
    private bool _IsReplay(PacketId id) => id.Value <= _IngestWatermark;

    #endregion
}
