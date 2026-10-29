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
    /// First parse versus replay for this protocol (<see cref="IngestGate"/>). Only the ordered first-parse path enters a
    /// scope; the outermost scope raises the watermark after the effect slot of that packet was written, so a reader that
    /// observes the raised watermark also observes the effect (release/acquire). The same type serves every stateful
    /// protocol; it replaces the watermark and nesting fields this class used to carry.
    /// </summary>
    private readonly IngestGate _Gate = new();

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

    #endregion
}
