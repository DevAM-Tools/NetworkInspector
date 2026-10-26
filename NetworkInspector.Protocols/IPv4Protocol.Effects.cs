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
    /// an entry. Layer key is <see cref="MutField.TryGetEffectLayerKey"/> at the parse call.
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
    // Plain int, no volatile or Interlocked: only the serialized first-parse thread reads or writes it (a replay
    // returns before touching it), so there is no concurrent access to protect.
    private int _ParseNesting;

    #endregion

    #region Effect store access

    /// <summary>
    /// Records the defragmentation outcome of one IPv4 layer during its first parse. Called only from the ordered
    /// first parse (single writer), and before the watermark is raised so a replay always sees the entry.
    /// </summary>
    /// <param name="id">The packet being parsed.</param>
    /// <param name="layerKey">The IPv4 layer within the packet.</param>
    /// <param name="reassembled">The completed datagram payload, or <see langword="null"/> when the fragment did not complete one.</param>
    private void _RecordDefragEffect(PacketId id, int layerKey, byte[]? reassembled) =>
        _Effects.Record(id.Value, layerKey, new DefragLayerEffect(reassembled));

    /// <summary>
    /// Finds the defragmentation outcome recorded for the given layer, or <see langword="null"/> when
    /// the layer never reached the defragmenter.
    /// </summary>
    /// <param name="id">The packet being replayed.</param>
    /// <param name="layerKey">The IPv4 layer within the packet.</param>
    private byte[]? _FindReassembledDatagram(PacketId id, int layerKey) =>
        _Effects.TryGet(id.Value, layerKey, out DefragLayerEffect effect)
            ? effect.ReassembledDatagram
            : null;

    /// <summary>
    /// Raises the first-parse watermark monotonically. Only the ordered first-parse path calls this,
    /// so a plain read-then-write is sufficient — no competing writer can interleave. The volatile write is the
    /// release that publishes the packet's recorded effect to replay threads; an <c>Interlocked</c> maximum was not
    /// chosen because there is no second writer to race with.
    /// </summary>
    /// <param name="id">The packet whose first parse has just completed.</param>
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
    /// <param name="id">The packet being parsed.</param>
    /// <remarks>
    /// A single volatile read decides the verdict. Packet ids are first parsed in dense ascending order, so
    /// <c>id &lt;= watermark</c> means this id (or a later one) already completed its first parse.
    /// </remarks>
    private bool _IsReplay(PacketId id) => id.Value <= _IngestWatermark;

    #endregion
}
