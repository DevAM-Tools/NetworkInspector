// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class TcpProtocol
{
    #region Effect types

    /// <summary>
    /// Bitmask flags indicating which TCP analysis conditions were detected for a segment.
    /// Mirrors the live tracker flags without connection references.
    /// </summary>
    /// <remarks>
    /// The bit values must stay identical to the internal <c>TcpAnalysisFlags</c> enum: the two are converted with a plain
    /// cast when an effect is recorded and when a result is rebuilt from it. This copy exists because the effect is a
    /// public, self-contained type, while the tracker's enum is internal.
    /// </remarks>
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

        /// <summary>The ACK acknowledges bytes that were never captured.</summary>
        AckLostSegment = 1 << 14,

        /// <summary>The ACK acknowledges only the front part of a segment.</summary>
        PartialAck = 1 << 15,

        /// <summary>The ACK acknowledges bytes that were sent more than once.</summary>
        AmbiguousAck = 1 << 16,
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

        /// <summary>Heuristic table matched and dispatched the raw payload (no reassembly configuration for the matched protocol).</summary>
        Heuristic = 4,

        /// <summary>
        /// The payload was not dispatched because it repeats or reorders bytes the application protocol already has or will
        /// receive in sequence (a retransmission or out-of-order segment on the plain port path). It is shown as segment data.
        /// </summary>
        SegmentData = 5,
    }

    /// <summary>
    /// Immutable TCP analysis facts recorded during ingest for redissect replay.
    /// Contains no live connection references.
    /// </summary>
    /// <remarks>
    /// Why a copy of the analysis instead of reading the tracker again: the tracker state moves on with every later
    /// packet, so a re-parse (UI, filter, export) must see the values as they were when this packet was ingested.
    /// Timing values are <see cref="double"/> seconds with NaN meaning "not measured"; the sequence numbers are the
    /// precomputed relative values, so replay does not need the ISNs. Thread safety: immutable value type.
    /// </remarks>
    public readonly record struct AnalysisEffect(
        uint StreamIndex,
        AnalysisEffectFlags Flags,
        uint DupAckNum,
        ulong BytesInFlight,
        double InitialRtt,
        double AckRtt,
        double TimeRelative,
        double TimeDelta,
        ulong ScaledWindowSize,
        int WindowScaleFactor,
        byte Phase,
        bool NoIpLayer,
        uint RelativeSeq,
        uint RelativeAck,
        bool SequenceIsRelative,
        bool AckIsRelative,
        bool StreamLimitReached,
        int AcksPacketId,
        double Rto,
        int RtoPacketId,
        int DupAckPacketId,
        byte Completeness)
    {
        /// <summary>
        /// The record text (<c>AnalysisEffect { StreamIndex = …, … }</c>) with the invariant culture, so it does not
        /// depend on the thread culture (a <c>double</c> would otherwise print <c>0,025</c> or <c>∞</c> on de-DE).
        /// </summary>
        /// <returns>The record text.</returns>
        public override string ToString()
        {
            StringBuilder builder = new(512);
            builder.Append("AnalysisEffect { ");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"StreamIndex = {StreamIndex}, Flags = {Flags}, DupAckNum = {DupAckNum}, BytesInFlight = {BytesInFlight}, ");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"InitialRtt = {InitialRtt}, AckRtt = {AckRtt}, TimeRelative = {TimeRelative}, TimeDelta = {TimeDelta}, ");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"ScaledWindowSize = {ScaledWindowSize}, WindowScaleFactor = {WindowScaleFactor}, Phase = {Phase}, NoIpLayer = {NoIpLayer}, ");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"RelativeSeq = {RelativeSeq}, RelativeAck = {RelativeAck}, SequenceIsRelative = {SequenceIsRelative}, ");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"AckIsRelative = {AckIsRelative}, StreamLimitReached = {StreamLimitReached}, ");
            builder.Append(
                CultureInfo.InvariantCulture,
                $"AcksPacketId = {AcksPacketId}, Rto = {Rto}, RtoPacketId = {RtoPacketId}, DupAckPacketId = {DupAckPacketId}, Completeness = {Completeness}");
            builder.Append(" }");
            return builder.ToString();
        }
    }

    /// <summary>
    /// A single reassembled PDU emitted during ingest, in dispatch order. <paramref name="PduBytes"/> is the caller-owned
    /// array the segment buffer copied out; it is kept so a replay can re-dispatch the identical bytes (the stream buffer
    /// has long since moved on). The bytes therefore live as long as the effect store.
    /// </summary>
    /// <param name="ProtocolId">Application protocol the PDU was dispatched to.</param>
    /// <param name="PduBytes">The PDU bytes.</param>
    /// <param name="SegmentPacketIds">
    /// Ids of the packets that carried the bytes of the PDU, in stream order. Recorded so a replay shows the same
    /// <c>tcp.segments</c> list: the stream buffer that knew the contributors has long since moved on.
    /// </param>
    public readonly record struct PduEffect(ProtocolId ProtocolId, ReadOnlyMemory<byte> PduBytes, int[] SegmentPacketIds);

    /// <summary>TCP payload dispatch metadata for redissect replay.</summary>
    /// <param name="Mode">How the payload was dispatched during ingest.</param>
    /// <param name="Pdus">Reassembled PDUs in dispatch order; null unless <paramref name="Mode"/> is a reassembly mode.</param>
    /// <param name="HeuristicProtocolId">Protocol chosen by the heuristic table (valid only for the heuristic mode).</param>
    /// <param name="PreferredPort">
    /// Server port learned by the connection tracker, tried first on replay (valid when <paramref name="HasPreferredPort"/>).
    /// </param>
    /// <param name="HasPreferredPort">Whether <paramref name="PreferredPort"/> carries a value.</param>
    /// <param name="Forward">
    /// Direction of the segment relative to the normalized stream key. Recorded because the connection key and the source
    /// address exist only on the first parse, while a replay must publish the same direction to application protocols
    /// (see <c>TryGetCurrentStream</c>).
    /// </param>
    /// <param name="ReassemblyLimitHit">A reassembly limit dropped buffered bytes while this segment was processed.</param>
    /// <param name="ReassemblyLimitDiscardedBytes">Number of bytes that limit event dropped.</param>
    public readonly record struct PayloadDispatchEffect(
        PayloadDispatchMode Mode,
        PduEffect[]? Pdus,
        ProtocolId HeuristicProtocolId,
        ushort PreferredPort,
        bool HasPreferredPort,
        bool Forward,
        bool ReassemblyLimitHit,
        int ReassemblyLimitDiscardedBytes);

    /// <summary>Combined TCP effects of one layer, recorded once at the end of its first parse.</summary>
    private readonly record struct TcpLayerEffect(AnalysisEffect Analysis, PayloadDispatchEffect Dispatch);

    #endregion

    #region Effect store (protocol-owned, keyed by (PacketId, LayerKey))

    /// <summary>
    /// TCP effects recorded during first parses. Sparse: only packets where TCP actually ran get an
    /// entry. Layer key is <see cref="MutField.TryGetEffectLayerKey"/> at the parse call.
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
        in DispatchOutcome outcome,
        bool forward,
        ushort preferredPort,
        bool hasPreferredPort)
    {
        // Snapshot the working list into an exact-size array: the list is a per-parse scratch object (it is reused for the
        // next segment), while the stored effect must be immutable and as small as possible (it lives as long as the
        // store). The common case is exactly one PDU, which gets a one-element array without the enumerator a collection
        // expression over the list would create.
        PduEffect[]? pduArray = null;
        List<PduEffect>? pdus = outcome.Pdus;
        if (pdus is { Count: 1 })
        {
            pduArray = [pdus[0]];
        }
        else if (pdus is { Count: > 1 })
        {
            pduArray = [.. pdus];
        }

        _Effects.Record(
            id.Value,
            layerKey,
            new TcpLayerEffect(
                _ToAnalysisEffect(in analysis),
                new PayloadDispatchEffect(
                    outcome.Mode,
                    pduArray,
                    outcome.HeuristicProtocolId,
                    preferredPort,
                    hasPreferredPort,
                    forward,
                    outcome.LimitHit,
                    outcome.LimitDiscardedBytes)));
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
