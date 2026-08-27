// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class TcpProtocol
{
    #region Effect types

    /// <summary>
    /// Bitmask flags indicating which TCP analysis conditions were detected for a segment.
    /// Mirrors the live tracker flags without connection references.
    /// </summary>
    [Flags]
    public enum AnalysisEffectFlags : uint
    {
        /// <summary>No analysis flags detected.</summary>
        None = 0,

        /// <summary>Segment is a retransmission.</summary>
        Retransmission = 1 << 0,

        /// <summary>Fast retransmission.</summary>
        FastRetransmission = 1 << 1,

        /// <summary>Out-of-order segment.</summary>
        OutOfOrder = 1 << 2,

        /// <summary>Duplicate ACK.</summary>
        DuplicateAck = 1 << 3,

        /// <summary>Lost segment detected.</summary>
        LostSegment = 1 << 4,

        /// <summary>Keep-alive probe.</summary>
        KeepAlive = 1 << 5,

        /// <summary>Keep-alive acknowledgment.</summary>
        KeepAliveAck = 1 << 6,

        /// <summary>Zero window advertised.</summary>
        ZeroWindow = 1 << 7,

        /// <summary>Zero window probe.</summary>
        ZeroWindowProbe = 1 << 8,

        /// <summary>Zero window probe ACK.</summary>
        ZeroWindowProbeAck = 1 << 9,

        /// <summary>Window update.</summary>
        WindowUpdate = 1 << 10,

        /// <summary>Window is full.</summary>
        WindowFull = 1 << 11,

        /// <summary>Connection reuses previously seen port pair.</summary>
        ReusedPorts = 1 << 12,

        /// <summary>Spurious retransmission.</summary>
        SpuriousRetransmission = 1 << 13,
    }

    /// <summary>How TCP payload upper-layer dispatch occurred during ingest.</summary>
    public enum PayloadDispatchMode : byte
    {
        /// <summary>No payload dispatch (empty payload or early return).</summary>
        None = 0,

        /// <summary>Raw segment payload dispatched via port table.</summary>
        RawPort = 1,

        /// <summary>One or more reassembled PDUs were dispatched.</summary>
        ReassemblyPdu = 2,

        /// <summary>Reassembly path taken but no PDU emitted for this packet.</summary>
        ReassemblyNoEmit = 3,

        /// <summary>Heuristic table matched and dispatched payload.</summary>
        Heuristic = 4,
    }

    /// <summary>
    /// Immutable TCP analysis facts recorded during ingest for redissect replay.
    /// Contains no live connection references.
    /// </summary>
    public readonly record struct AnalysisEffect(
        uint StreamIndex,
        AnalysisEffectFlags Flags,
        uint DupAckNum,
        ulong BytesInFlight,
        float InitialRtt,
        float AckRtt,
        float TimeRelative,
        float TimeDelta,
        ulong ScaledWindowSize,
        int WindowScaleFactor,
        byte Phase,
        bool NoIpLayer);

    /// <summary>A single reassembled PDU emitted during ingest, in dispatch order.</summary>
    public readonly record struct PduEffect(ProtocolId ProtocolId, byte[] PduBytes);

    /// <summary>TCP payload dispatch metadata for redissect replay.</summary>
    public readonly record struct PayloadDispatchEffect(
        PayloadDispatchMode Mode,
        PduEffect[]? Pdus,
        ProtocolId HeuristicProtocolId);

    /// <summary>Combined TCP effects of one layer, recorded once at the end of its first parse.</summary>
    private readonly record struct TcpLayerEffect(AnalysisEffect Analysis, PayloadDispatchEffect Dispatch);

    #endregion

    #region Effect store (protocol-owned, keyed by (PacketId, LayerKey))

    /// <summary>
    /// TCP effects recorded during first parses. Sparse: only packets where TCP actually ran get an
    /// entry. Layer key is <see cref="Packet.GetEffectLayerKey"/> at the parse call.
    /// <para>
    /// <b>Thread-safety:</b> single ordered first-parse writer; lock-free readers once the entry is
    /// published. Lifetime is bound to this protocol instance and therefore to its
    /// <see cref="Stack"/> — a stack swap creates fresh protocol instances with empty stores.
    /// </para>
    /// </summary>
    private readonly EffectStore<TcpLayerEffect> _Effects = new();

    /// <summary>
    /// Highest packet id that completed a first parse; <c>-1</c> until the first packet was parsed.
    /// <para>
    /// The single writer is the ordered first-parse path (<see cref="_RaiseWatermark"/>), which the
    /// session serializes under its parse lock. Readers use <see cref="_IsReplay"/>. Written after
    /// the effect slot of that packet, so a reader that observes the raised watermark also observes
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

    /// <summary>
    /// Records the combined analysis + payload dispatch effects of one TCP layer.
    /// Called only from the first-parse path; costs one array allocation when reassembled PDUs were
    /// emitted.
    /// </summary>
    private void _RecordEffect(
        PacketId id,
        int layerKey,
        in TcpAnalysisResult analysis,
        PayloadDispatchMode mode,
        List<PduEffect>? pdus,
        ProtocolId heuristicProtocolId)
    {
        PduEffect[]? pduArray = null;
        if (pdus is not null)
        {
            pduArray = [.. pdus];
        }

        _Effects.Record(
            id.Value,
            layerKey,
            new TcpLayerEffect(
                _ToAnalysisEffect(in analysis),
                new PayloadDispatchEffect(mode, pduArray, heuristicProtocolId)));
    }

    /// <summary>
    /// Finds the effect recorded for the given layer, or <see langword="null"/> when the layer was
    /// never recorded.
    /// </summary>
    private TcpLayerEffect? _FindRecordedEffect(PacketId id, int layerKey)
    {
        if (_Effects.TryGet(id.Value, layerKey, out TcpLayerEffect effect))
        {
            return effect;
        }

        return null;
    }

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
    /// effects instead of mutating the connection tracker or the reassembly engine.
    /// </summary>
    private bool _IsReplay(PacketId id) => id.Value <= _IngestWatermark;

    #endregion
}
