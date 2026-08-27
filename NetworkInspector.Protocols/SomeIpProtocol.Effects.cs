// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class SomeIpProtocol
{
    #region Effect store (protocol-owned, keyed by (PacketId, LayerKey))

    /// <summary>
    /// TP reassembly results recorded during first parses. Sparse: only packets carrying a TP header
    /// get an entry. Layer key is <see cref="Packet.GetEffectLayerKey"/> at the parse call.
    /// <para>
    /// <b>Thread-safety:</b> single ordered first-parse writer; lock-free readers once the entry is
    /// published.
    /// </para>
    /// </summary>
    private readonly EffectStore<SomeIpTpReassemblyResult> _Effects = new();

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

    /// <summary>Records the TP reassembly result of one SOME/IP layer during its first parse.</summary>
    private void _RecordTpEffect(PacketId id, int layerKey, in SomeIpTpReassemblyResult result) =>
        _Effects.Record(id.Value, layerKey, in result);

    /// <summary>
    /// Finds the TP reassembly result recorded for the given layer. Returns <see langword="false"/>
    /// when the layer never reached the reassembler, in which case the caller degrades to reporting
    /// the segment without reassembly.
    /// </summary>
    private bool _TryGetTpEffect(PacketId id, int layerKey, out SomeIpTpReassemblyResult result) =>
        _Effects.TryGet(id.Value, layerKey, out result);

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
    /// TP result instead of feeding the shared reassembly sessions again.
    /// </summary>
    private bool _IsReplay(PacketId id) => id.Value <= _IngestWatermark;

    #endregion
}
