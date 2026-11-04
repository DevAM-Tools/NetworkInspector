// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

// Payload dispatch of TCP: which application protocol gets the bytes of a segment, and in which shape (the raw segment, a
// reassembled PDU, nothing because the bytes are buffered or already seen). The first parse of a packet decides and records
// the decision as an effect; every later parse replays that record without touching connection state.
public sealed partial class TcpProtocol
{
    #region Dispatch bookkeeping

    /// <summary>
    /// What one first-parse dispatch decided, collected in a struct that is passed by reference so the hot per-segment path
    /// allocates nothing for it. It becomes the recorded <see cref="PayloadDispatchEffect"/>.
    /// </summary>
    private struct DispatchOutcome
    {
        /// <summary>How the payload was dispatched; <see cref="PayloadDispatchMode.None"/> until a path is taken.</summary>
        internal PayloadDispatchMode Mode;

        /// <summary>PDUs dispatched by this segment, in order; null when there were none. May be the shared scratch list.</summary>
        internal List<PduEffect>? Pdus;

        /// <summary><see cref="Pdus"/> is the shared scratch list and must be released after the effect was recorded.</summary>
        internal bool PdusAreScratch;

        /// <summary>Protocol chosen by the heuristic table (valid for <see cref="PayloadDispatchMode.Heuristic"/>).</summary>
        internal ProtocolId HeuristicProtocolId;

        /// <summary>A reassembly limit dropped buffered bytes while this segment was processed.</summary>
        internal bool LimitHit;

        /// <summary>Number of buffered bytes the limit event dropped.</summary>
        internal int LimitDiscardedBytes;

        /// <summary>Bytes the stream buffer skipped to resynchronize while this segment was processed.</summary>
        internal int ResyncSkippedBytes;
    }

    /// <summary>
    /// Scratch list for the PDUs of the segment that is being ingested. Motivation: a list per completing segment plus the
    /// copy into the effect array were two allocations on the ingest hot path; the first parse is serialized, so one
    /// reusable list is enough. <see cref="_PduScratchInUse"/> guards the case that TCP is entered again while a PDU of the
    /// same segment is dispatched (a tunnel that carries TCP inside TCP): the inner parse then allocates its own list instead
    /// of corrupting the one the outer parse is still filling.
    /// </summary>
    private readonly List<PduEffect> _PduScratch = new(4);

    private bool _PduScratchInUse;

    /// <summary>Hands out the scratch list when it is free, otherwise a fresh list.</summary>
    private List<PduEffect> _RentPduList(ref DispatchOutcome outcome)
    {
        if (_PduScratchInUse)
        {
            return new List<PduEffect>(4);
        }

        _PduScratchInUse = true;
        outcome.PdusAreScratch = true;
        return _PduScratch;
    }

    /// <summary>Gives the scratch list back (emptied) once the effect has been recorded, also after errors.</summary>
    private void _ReleasePduList(ref DispatchOutcome outcome)
    {
        if (outcome.PdusAreScratch)
        {
            _PduScratch.Clear();
            _PduScratchInUse = false;
            outcome.PdusAreScratch = false;
        }
    }

    #endregion

    #region Stream identity for application protocols

    /// <summary>The stream a TCP layer is currently dispatching a payload for.</summary>
    private readonly record struct StreamPublication(int PacketId, uint Stream, bool Forward, bool IsReplay);

    // Written by the TCP layer around the payload dispatch of a packet (first parse and replay alike), read by stateful
    // application protocols through TryGetCurrentStream. Thread-static because each thread parses its own packets and the
    // value is only valid inside one dispatch call; it is restored afterwards, so nested TCP layers (tunnels) do not leak.
    [ThreadStatic]
    private static StreamPublication? _PublishedStream;

    /// <summary>
    /// Stream index and direction of the TCP layer that is dispatching <paramref name="packetId"/> on this thread. TCP
    /// publishes the stream identity to the application protocols it calls the way IP publishes its addresses, so a
    /// protocol with per-connection state (a header compression table, a request queue) can key that state without
    /// walking the field tree. Written before the payload dispatch on the first parse and on replay (the direction comes
    /// from the recorded effect), so both paths see the same values. False when no TCP layer is dispatching this packet on
    /// this thread, which includes a connection that has no stream (untracked: no IP layer, full stream table).
    /// </summary>
    /// <param name="packetId">Packet the caller is parsing.</param>
    /// <param name="stream">Stream index (the value of <c>tcp.stream</c>).</param>
    /// <param name="isForward">Direction relative to the normalized stream key (the direction of the lower endpoint).</param>
    /// <param name="isReplay">
    /// <see langword="true"/> when this is a re-parse of a packet whose first parse already happened: the application protocol
    /// must then read what it recorded and must not change its connection state. TCP decides this (by its ingest watermark),
    /// because one TCP packet can hand several PDUs to the same application protocol, so a watermark of the application
    /// protocol itself could not tell the PDUs of the first parse from a replay.
    /// </param>
    /// <returns><see langword="true"/> when the identity is available.</returns>
    internal static bool TryGetCurrentStream(PacketId packetId, out uint stream, out bool isForward, out bool isReplay)
    {
        StreamPublication? published = _PublishedStream;
        if (published is { } value && value.PacketId == packetId.Value)
        {
            stream = value.Stream;
            isForward = value.Forward;
            isReplay = value.IsReplay;
            return true;
        }

        stream = 0;
        isForward = false;
        isReplay = false;
        return false;
    }

    #endregion

    #region Payload dispatch

    /// <summary>
    /// Dispatches the payload of a segment on the first parse, or replays the recorded dispatch. Called for every segment,
    /// also for one without payload: a FIN or RST must reach the reassembly engine, and the effect of every first parse is
    /// recorded here.
    /// </summary>
    private ParseResult _DispatchTcpPayload(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint sequence,
        byte flags,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context,
        in TcpAnalysisResult analysis,
        int layerKey,
        bool isReplay)
    {
        // Publish the ports for the application protocols called from here, on the first parse and on a replay alike: most of the
        // ways TCP reaches a protocol (framed messages of a stream, the single-owner port cache, the heuristic table) carry no
        // dispatch key, and a protocol that decides by port reads them here. The finally withdraws them, so a protocol reached
        // later without TCP never sees the ports of an earlier segment that happened to carry the same packet id on this thread.
        TcpPortCache.Publish(parentField.Packet.Id, srcPort, dstPort);
        try
        {
            return _DispatchTcpPayloadPublished(
                in parentField, srcPort, dstPort, sequence, flags, payload, totalConsumed, in context, in analysis, layerKey, isReplay);
        }
        finally
        {
            TcpPortCache.Clear();
        }
    }

    /// <summary>The payload dispatch proper; runs with the ports of the segment published (see <see cref="_DispatchTcpPayload"/>).</summary>
    private ParseResult _DispatchTcpPayloadPublished(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint sequence,
        byte flags,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context,
        in TcpAnalysisResult analysis,
        int layerKey,
        bool isReplay)
    {
        if (isReplay)
        {
            return _DispatchTcpPayloadReplay(
                in parentField, srcPort, dstPort, payload, totalConsumed, in context, layerKey);
        }

        // Direction of this segment for the stream publication and for the effect (a replay cannot recompute it: the
        // connection key and the source address are first-parse-only).
        bool tracked = analysis.ConnectionState is not null;
        bool forward = tracked && analysis.ConnectionKey.IsForward(analysis.SrcAddr, srcPort);
        StreamPublication? previous = _PublishedStream;
        if (tracked)
        {
            _PublishedStream = new StreamPublication(parentField.Packet.Id.Value, analysis.StreamIndex, forward, IsReplay: false);
        }

        DispatchOutcome outcome = default;
        outcome.HeuristicProtocolId = ProtocolId.Invalid;

        // The effect is recorded after the dispatch attempt so error exits also capture the analysis facts and the dispatch
        // mode reached so far. The finally block restores the publication and returns the scratch list on every exit,
        // including an exception (which records nothing, see Parse).
        try
        {
            ParseResult result = _DispatchTcpPayloadFirstParse(
                in parentField, srcPort, dstPort, sequence, flags, payload, in context, in analysis, totalConsumed, ref outcome);

            // The learned server port is copied into the effect now: the live connection state keeps changing with later
            // packets, so a replay could not read it from there and must not (it would race with the ingest thread).
            bool hasPreferred = analysis.ConnectionState is { ServerPortSet: true };
            ushort preferred = hasPreferred ? analysis.ConnectionState!.ServerPort : (ushort)0;
            _RecordEffect(parentField.Packet.Id, layerKey, in analysis, in outcome, forward, preferred, hasPreferred);
            return result;
        }
        finally
        {
            _PublishedStream = previous;
            _ReleasePduList(ref outcome);
        }
    }

    /// <summary>
    /// Dispatches TCP payload on the first parse of a packet and reports what happened through <paramref name="outcome"/>
    /// so the caller can record it as the replay effect.
    /// </summary>
    private ParseResult _DispatchTcpPayloadFirstParse(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint sequence,
        byte flags,
        ReadOnlyMemory<byte> payload,
        in ParseContext context,
        in TcpAnalysisResult analysis,
        int totalConsumed,
        ref DispatchOutcome outcome)
    {
        bool hasPayload = !payload.IsEmpty;
        bool closing = (flags & 0x05) != 0; // FIN 0x01 or RST 0x04
        outcome.Mode = PayloadDispatchMode.None;

        // Fast exit: a pure ACK has nothing to dispatch and nothing to close. This is the most common segment of a transfer,
        // so it must cost nothing here.
        if (!hasPayload && !closing)
        {
            return totalConsumed;
        }

        ushort lowPort = Math.Min(srcPort, dstPort);
        ushort highPort = Math.Max(srcPort, dstPort);

        // Step 1: pick the application protocol that owns this connection, for reassembly. Server port learned from the SYN
        // wins (a client port that happens to be well-known must not select a protocol); without it, the lower port is the
        // usual server side, and the higher port is only consulted when the lower port has no protocol at all. Reassembly
        // is configured per protocol, so a port with zero or several registered protocols is not reassembled (several owners
        // are ambiguous and fall through to the plain port dispatch below).
        bool serverPortKnown = analysis.ConnectionState is { ServerPortSet: true };
        ushort serverPort = serverPortKnown ? analysis.ConnectionState!.ServerPort : (ushort)0;
        ProtocolId targetProtocol = default;
        bool singleOwner = false;
        if (serverPortKnown)
        {
            ReadOnlySpan<ProtocolId> onServer = _PortProtocols(serverPort, in context);
            if (onServer.Length == 1)
            {
                singleOwner = true;
                targetProtocol = onServer[0];
            }
        }
        else
        {
            ReadOnlySpan<ProtocolId> onLow = _PortProtocols(lowPort, in context);
            if (onLow.Length == 1)
            {
                singleOwner = true;
                targetProtocol = onLow[0];
            }
            else if (onLow.IsEmpty && lowPort != highPort)
            {
                ReadOnlySpan<ProtocolId> onHigh = _PortProtocols(highPort, in context);
                if (onHigh.Length == 1)
                {
                    singleOwner = true;
                    targetProtocol = onHigh[0];
                }
            }
        }

        // Step 1b: a protocol already detected by content on this connection owns it from then on, like a port owner. This is
        // what makes reassembly independent of the port number: HTTP on port 8000 is framed the same way as on port 80.
        if (!singleOwner && analysis.ConnectionState?.HeuristicProtocolId is ProtocolId cachedOwner)
        {
            singleOwner = true;
            targetProtocol = cachedOwner;
        }

        // Step 2: reassembly path. Segments are buffered per direction; every complete PDU is dispatched to the owning protocol
        // (zero, one or several per segment: a segment can complete a pending PDU, carry several, or only add to a partial one).
        // A FIN or RST also arrives here, without payload, to flush or drop the stream.
        if (singleOwner && targetProtocol.IsValid && _ReassemblyEngine is not null && analysis.ConnectionState is not null)
        {
            ParseResult viaStream = _TryReassembleAndDispatch(
                in parentField, targetProtocol, srcPort, sequence, flags, payload, in context, in analysis, ref outcome,
                out bool handled);
            if (viaStream.TryPropagateError(out ParseResult streamError))
            {
                return streamError;
            }

            if (handled)
            {
                return totalConsumed;
            }
        }

        // Without payload there is nothing left to hand to a protocol on the plain path.
        if (!hasPayload)
        {
            return totalConsumed;
        }

        // Step 3: plain port dispatch of the raw segment payload (no reassembly). Retransmitted or reordered data is not
        // handed to the application protocol: it would be parsed twice, or out of order. It is shown as segment data instead,
        // as the bytes of a segment the stream already has or will deliver in sequence.
        const TcpAnalysisFlags notDissected = TcpAnalysisFlags.Retransmission | TcpAnalysisFlags.FastRetransmission
            | TcpAnalysisFlags.SpuriousRetransmission | TcpAnalysisFlags.OutOfOrder;
        if ((analysis.Flags & notDissected) != 0)
        {
            outcome.Mode = PayloadDispatchMode.SegmentData;
            _AppendSegmentData(in parentField, payload, analysis.Flags, in context);
            return totalConsumed;
        }

        // With a learned server port only that port is tried; otherwise the lower port first, then the higher one if the
        // lower port consumed nothing.
        outcome.Mode = PayloadDispatchMode.RawPort;
        ParseResult result;
        if (serverPortKnown)
        {
            result = _DispatchPort(in parentField, serverPort, payload, in context);
            if (result.TryPropagateError(out ParseResult serverError))
            {
                return serverError;
            }
        }
        else
        {
            result = _DispatchPort(in parentField, lowPort, payload, in context);
            if (result.TryPropagateError(out ParseResult error))
            {
                return error;
            }

            if (!result.TryGetConsumed(out _) && lowPort != highPort)
            {
                result = _DispatchPort(in parentField, highPort, payload, in context);
                if (result.TryPropagateError(out ParseResult highError))
                {
                    return highError;
                }
            }
        }

        // Step 4: content-based detection as the last resort when no port matched. A hit is cached on the connection (the
        // cache is first-parse state; a replay uses the recorded protocol id and never reads or writes it) and, when the
        // matched protocol has a reassembly configuration, the matching segment is fed through the stream like a port
        // owner's, so the segments that follow are framed too.
        if (!result.TryGetConsumed(out _) && _HeuristicTableId.IsValid)
        {
            ProtocolId? matched = analysis.ConnectionState?.HeuristicProtocolId
                ?? context.Stack?.TryMatchHeuristic(_HeuristicTableId, payload);
            if (matched is ProtocolId heuristicOwner)
            {
                if (analysis.ConnectionState is not null)
                {
                    analysis.ConnectionState.HeuristicProtocolId = heuristicOwner;
                }

                outcome.Mode = PayloadDispatchMode.Heuristic;
                outcome.HeuristicProtocolId = heuristicOwner;

                if (_ReassemblyEngine is not null && analysis.ConnectionState is not null)
                {
                    ParseResult viaHeuristicStream = _TryReassembleAndDispatch(
                        in parentField, heuristicOwner, srcPort, sequence, flags, payload, in context, in analysis, ref outcome,
                        out bool heuristicHandled);
                    if (viaHeuristicStream.TryPropagateError(out ParseResult heuristicStreamError))
                    {
                        return heuristicStreamError;
                    }

                    if (heuristicHandled)
                    {
                        return totalConsumed;
                    }
                }

                // No reassembly configuration for the matched protocol (or no stream): hand over the raw segment.
                outcome.Mode = PayloadDispatchMode.Heuristic;
                ParseResult heuristicResult = parentField.CallProtocol(heuristicOwner, payload, in context);
                if (heuristicResult.TryPropagateError(out ParseResult heuristicError))
                {
                    return heuristicError;
                }
            }
        }

        return totalConsumed;
    }

    #endregion

    #region Reassembly dispatch

    /// <summary>
    /// Feeds a segment into the reassembly stream of <paramref name="owner"/> and dispatches every PDU that completes. A FIN
    /// delivers what is still buffered as the last PDU of the direction (a message that ends with the connection); an RST drops
    /// it, because the peer aborted and the bytes were never a complete message. Both close the direction for good.
    /// </summary>
    /// <param name="parentField">Field that receives the nested protocol layers (sibling of the TCP container).</param>
    /// <param name="owner">Application protocol that owns the connection (by port or by content).</param>
    /// <param name="srcPort">Source port, used to find the direction of the segment.</param>
    /// <param name="sequence">Wire sequence number of the first payload byte.</param>
    /// <param name="flags">TCP flags byte; FIN and RST close the direction.</param>
    /// <param name="payload">The segment payload; empty for a FIN or RST without data.</param>
    /// <param name="context">Parse context of the nested dispatch.</param>
    /// <param name="analysis">Analysis of the segment (connection state, key and source address, anomaly flags).</param>
    /// <param name="outcome">Collects what happened so the caller can record it as the replay effect.</param>
    /// <param name="handled">
    /// <see langword="true"/> when the stream took the segment (the caller must not dispatch it again); false when there is
    /// no stream or it refused the segment, so the caller falls back to the plain dispatch.
    /// </param>
    /// <returns>No error (default result) or the error of a nested parse, which the caller propagates.</returns>
    private ParseResult _TryReassembleAndDispatch(
        in MutField parentField,
        ProtocolId owner,
        ushort srcPort,
        uint sequence,
        byte flags,
        ReadOnlyMemory<byte> payload,
        in ParseContext context,
        in TcpAnalysisResult analysis,
        ref DispatchOutcome outcome,
        out bool handled)
    {
        handled = false;
        TcpReassemblyEngine engine = _ReassemblyEngine!; // Invariant: the caller checked for null before calling.
        TcpConnectionState connection = analysis.ConnectionState!; // Invariant: the caller checked for null before calling.
        TcpConnectionKey connKey = analysis.ConnectionKey;
        bool hasPayload = !payload.IsEmpty;
        bool isFin = (flags & 0x01) != 0;
        bool isRst = (flags & 0x04) != 0;

        // A segment with payload creates the stream on demand. A segment without payload (FIN or RST) only looks it up: a
        // connection that never carried data must not get a stream (two buffers and their bookkeeping) just because it
        // ended, which would let a port scan allocate one per refused connection.
        TcpStreamState? stream;
        bool forward;
        if (hasPayload)
        {
            stream = engine.GetOrCreateStream(in connKey, owner, analysis.SrcAddr, srcPort, out forward);
        }
        else
        {
            forward = connKey.IsForward(analysis.SrcAddr, srcPort);
            stream = engine.TryGetStream(in connKey);
        }

        // A null stream state means the protocol has no reassembly configuration (or nothing was ever buffered): the caller
        // continues with the plain dispatch.
        if (stream is null)
        {
            return 0;
        }

        // Passed to context-aware PDU detectors via StreamDetectionContext.HandshakeObserved: set once the capture
        // contains the SYN or SYN-ACK, i.e. the stream is seen from its start rather than joined mid-flow.
        if ((connection.Completeness & (TcpConnectionState.SynSeen | TcpConnectionState.SynAckSeen)) != 0)
        {
            stream.HandshakeObserved = true;
        }

        // A segment that the stream buffer refuses (the direction is closed or failed, or the segment alone exceeds the
        // buffer limit) also falls through to the plain dispatch: the plain dispatch shows what the segment holds on its own,
        // where swallowing it would hide the bytes without any sign.
        int packetId = parentField.Packet.Id.Value;
        bool accepted = !hasPayload || TcpReassemblyEngine.FeedSegment(stream, forward, sequence, payload, packetId);
        if (hasPayload && TcpReassemblyEngine.TakeLimitHit(stream, forward, out int discardedByFeed))
        {
            outcome.LimitHit = true;
            outcome.LimitDiscardedBytes += discardedByFeed;
        }

        if (!accepted)
        {
            return 0;
        }

        handled = true;

        // Complete PDUs first. With a FIN (not an RST) the detector may now frame a message that is complete only because
        // the stream ended, such as a response body that runs until the connection closes.
        ParseResult extractResult = _ExtractAndDispatch(
            in parentField, stream, forward, owner, closing: isFin && !isRst, in context, ref outcome, out bool emitted);
        if (extractResult.TryPropagateError(out ParseResult extractError))
        {
            // An error can only come from a PDU that was dispatched, so the mode is the one of the success path below. The effect
            // is recorded on error exits too; with the mode left at None a replay of this packet would dispatch nothing and show
            // neither the PDUs that were parsed before the error nor the error itself (a nested limit error, for example).
            outcome.Mode = PayloadDispatchMode.ReassemblyPdu;
            return extractError;
        }

        // Close the direction. Whatever is left after the extraction is by definition incomplete: on FIN it is delivered as
        // the final PDU (the detector said it needs more bytes, and no more will come), on RST it is dropped.
        if (isFin || isRst)
        {
            if (TcpReassemblyEngine.Close(stream, forward, isRst, out ReadOnlyMemory<byte> tail, out int[] tailPacketIds))
            {
                emitted = true;
                ParseResult tailResult = _DispatchPdu(in parentField, owner, tail, tailPacketIds, in context, ref outcome);
                if (tailResult.TryPropagateError(out ParseResult tailError))
                {
                    // Same reason as for the error of the extraction above: the effect of an error exit keeps the PDUs.
                    outcome.Mode = PayloadDispatchMode.ReassemblyPdu;
                    return tailError;
                }
            }

            if (TcpReassemblyEngine.TakeLimitHit(stream, forward, out int discardedByClose))
            {
                outcome.LimitHit = true;
                outcome.LimitDiscardedBytes += discardedByClose;
            }
        }

        outcome.Mode = emitted
            ? PayloadDispatchMode.ReassemblyPdu
            : PayloadDispatchMode.ReassemblyNoEmit;

        // A segment whose bytes only went into the buffer is shown as such: its payload as segment data, with the info text
        // saying it belongs to a PDU that completes later (or that it repeats bytes the stream already has).
        if (!emitted && hasPayload)
        {
            _AppendSegmentData(in parentField, payload, analysis.Flags, in context);
        }

        // Bytes the stream buffer skipped to resynchronize (a detector said Invalid, the heuristic found the next header). Read
        // after the extraction and the close, which are the places that can trigger it; reported once, on this packet.
        outcome.ResyncSkippedBytes += TcpReassemblyEngine.TakeResyncSkip(stream, forward);

        // The limit event and the resynchronization are reported last, after the PDUs, so a replay (which reports them at the
        // same place, in the same order) and the first parse produce the fields in the same order.
        if (outcome.LimitHit)
        {
            _AppendReassemblyLimitField(in parentField, outcome.LimitDiscardedBytes, in context);
        }

        if (outcome.ResyncSkippedBytes > 0)
        {
            _AppendResyncField(in parentField, outcome.ResyncSkippedBytes, in context);
        }

        return 0;
    }

    /// <summary>
    /// Extracts every PDU that is complete in one direction and dispatches it to <paramref name="owner"/>.
    /// </summary>
    /// <param name="parentField">Field that receives the nested protocol layers.</param>
    /// <param name="stream">Reassembly stream of the connection.</param>
    /// <param name="forward">Direction of the segment relative to the normalized stream key.</param>
    /// <param name="owner">Application protocol that receives the PDUs.</param>
    /// <param name="closing">The sender of this direction closed it with a FIN (see <see cref="StreamDetectionContext.StreamClosed"/>).</param>
    /// <param name="context">Parse context of the nested dispatch.</param>
    /// <param name="outcome">Collects the dispatched PDUs for the replay effect.</param>
    /// <param name="emitted"><see langword="true"/> when at least one PDU was dispatched.</param>
    /// <returns>No error (default result) or the error of a nested parse.</returns>
    private ParseResult _ExtractAndDispatch(
        in MutField parentField,
        TcpStreamState stream,
        bool forward,
        ProtocolId owner,
        bool closing,
        in ParseContext context,
        ref DispatchOutcome outcome,
        out bool emitted)
    {
        emitted = false;
        while (TcpReassemblyEngine.TryExtractPdu(stream, forward, closing, out ReadOnlyMemory<byte> pdu, out int[] packetIds))
        {
            emitted = true;
            ParseResult pduResult = _DispatchPdu(in parentField, owner, pdu, packetIds, in context, ref outcome);
            if (pduResult.TryPropagateError(out ParseResult pduError))
            {
                return pduError;
            }
        }

        return 0;
    }

    /// <summary>
    /// Attaches one reassembled PDU to the packet, records it for replay and parses it with the owning protocol.
    /// </summary>
    private ParseResult _DispatchPdu(
        in MutField parentField,
        ProtocolId owner,
        ReadOnlyMemory<byte> pdu,
        int[] packetIds,
        in ParseContext context,
        ref DispatchOutcome outcome)
    {
        // The PDU is already a caller-owned byte[] (the segment buffer copied it out, never a slice of Frame.Data), so it is
        // not copied again. It is attached to the packet as an additional buffer before the nested parse so the inner layer's
        // data slice gets an effect layer key. The recorded PduEffect keeps the same bytes: a replay binds them again in the
        // same order and therefore gets the same buffer index and layer key as this first parse. A zero-copy slice of the
        // frame here would key as the frame on ingest but as an extra buffer on replay, and the replay lookup would miss.
        if (!parentField.TryBindBuffer(pdu, out ReadOnlyMemory<byte> bound, out _))
        {
            // The packet holds at most 255 bound buffers (a constant of the packet's effect keys), so a segment that
            // completes more PDUs than that cannot nest-parse the rest. An empty slice would reach the inner
            // protocol as "no data" or as an unrelated error, so the limit is named here. The PDU is not recorded
            // as an effect: it was not dispatched.
            return ParseError.Custom(ProtocolName, _BoundBufferLimitMessage);
        }

        outcome.Pdus ??= _RentPduList(ref outcome);
        outcome.Pdus.Add(new PduEffect(owner, pdu, packetIds));

        _AppendSegmentFields(in parentField, packetIds, pdu.Length, in context);
        return parentField.CallProtocol(owner, bound, in context);
    }

    #endregion

    #region Replay

    /// <summary>
    /// Replays TCP payload dispatch from the recorded effect without mutating trackers.
    /// Without a recorded effect the payload falls back to the stateless raw-port dispatch.
    /// </summary>
    private ParseResult _DispatchTcpPayloadReplay(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context,
        int layerKey)
    {
        // No recorded effect (the first parse threw before recording): degrade to the stateless low-then-high port dispatch.
        // It never falls back into mutation (tracker/reassembly), so a reader thread stays race-free.
        TcpLayerEffect? recorded = _FindRecordedEffect(parentField.Packet.Id, layerKey);
        if (recorded is null)
        {
            if (payload.IsEmpty)
            {
                return totalConsumed;
            }

            return _DispatchPortsLowThenHigh(in parentField, srcPort, dstPort, payload, totalConsumed, in context);
        }

        // Publish the stream identity exactly as the first parse did, from the recorded values.
        PayloadDispatchEffect dispatch = recorded.Value.Dispatch;
        AnalysisEffect analysisEffect = recorded.Value.Analysis;
        bool published = !analysisEffect.NoIpLayer && !analysisEffect.StreamLimitReached;
        StreamPublication? previous = _PublishedStream;
        if (published)
        {
            _PublishedStream = new StreamPublication(parentField.Packet.Id.Value, analysisEffect.StreamIndex, dispatch.Forward, IsReplay: true);
        }

        try
        {
            return _ReplayRecordedDispatch(
                in parentField, srcPort, dstPort, payload, totalConsumed, in context, in dispatch, analysisEffect.Flags);
        }
        finally
        {
            _PublishedStream = previous;
        }
    }

    /// <summary>Replays one recorded dispatch; the switch mirrors the modes written by the first parse.</summary>
    private ParseResult _ReplayRecordedDispatch(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context,
        in PayloadDispatchEffect dispatch,
        AnalysisEffectFlags analysisFlags)
    {
        switch (dispatch.Mode)
        {
            case PayloadDispatchMode.None:
                // Nothing was dispatched on the first parse (a pure ACK, or a FIN that found no stream).
                return totalConsumed;

            case PayloadDispatchMode.ReassemblyPdu:
                if (dispatch.Pdus is not null)
                {
                    foreach (PduEffect pdu in dispatch.Pdus)
                    {
                        // Binding in recorded order reproduces the buffer indices (and thus effect layer keys) of the first
                        // parse, so nested stateful layers find their own recorded effects.
                        if (!parentField.TryBindBuffer(pdu.PduBytes, out ReadOnlyMemory<byte> bound, out _))
                        {
                            // Same limit as on the first parse (the replay binds the same PDUs in the same order).
                            return ParseError.Custom(ProtocolName, _BoundBufferLimitMessage);
                        }

                        _AppendSegmentFields(in parentField, pdu.SegmentPacketIds, pdu.PduBytes.Length, in context);
                        ParseResult pduResult = parentField.CallProtocol(pdu.ProtocolId, bound, in context);
                        if (pduResult.TryPropagateError(out ParseResult pduError))
                        {
                            return pduError;
                        }
                    }
                }

                _ReplayLimit(in parentField, in dispatch, in context);
                return totalConsumed;

            case PayloadDispatchMode.ReassemblyNoEmit:
                // No complete PDU was available on the first parse, so nothing was dispatched then and nothing is now; the
                // payload is shown as segment data, exactly as then.
                if (!payload.IsEmpty)
                {
                    _AppendSegmentData(in parentField, payload, (TcpAnalysisFlags)analysisFlags, in context);
                }

                _ReplayLimit(in parentField, in dispatch, in context);
                return totalConsumed;

            case PayloadDispatchMode.SegmentData:
                _AppendSegmentData(in parentField, payload, (TcpAnalysisFlags)analysisFlags, in context);
                return totalConsumed;

            case PayloadDispatchMode.Heuristic:
                if (dispatch.HeuristicProtocolId.IsValid)
                {
                    ParseResult heuristicResult = parentField.CallProtocol(
                        dispatch.HeuristicProtocolId, payload, in context);
                    if (heuristicResult.TryPropagateError(out ParseResult heuristicError))
                    {
                        return heuristicError;
                    }
                }

                return totalConsumed;

            case PayloadDispatchMode.RawPort:
            default:
                // RawPort also covers unknown modes. A learned server port was the only port tried on the first parse, so only
                // it is replayed; otherwise the low-then-high order of the first parse applies.
                if (dispatch.HasPreferredPort)
                {
                    ParseResult preferredResult = _DispatchPort(
                        in parentField, dispatch.PreferredPort, payload, in context);
                    if (preferredResult.TryPropagateError(out ParseResult preferredError))
                    {
                        return preferredError;
                    }

                    return totalConsumed;
                }

                return _DispatchPortsLowThenHigh(in parentField, srcPort, dstPort, payload, totalConsumed, in context);
        }
    }

    /// <summary>Plain dispatch by port: the lower port first, then the higher one if the lower consumed nothing.</summary>
    private ParseResult _DispatchPortsLowThenHigh(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context)
    {
        ushort low = Math.Min(srcPort, dstPort);
        ushort high = Math.Max(srcPort, dstPort);
        ParseResult portResult = _DispatchPort(in parentField, low, payload, in context);
        if (portResult.TryPropagateError(out ParseResult portError))
        {
            return portError;
        }

        if (!portResult.TryGetConsumed(out _) && low != high)
        {
            portResult = _DispatchPort(in parentField, high, payload, in context);
            if (portResult.TryPropagateError(out ParseResult highPortError))
            {
                return highPortError;
            }
        }

        return totalConsumed;
    }

    /// <summary>Re-reports a reassembly limit event that the first parse recorded.</summary>
    private void _ReplayLimit(in MutField parentField, in PayloadDispatchEffect dispatch, in ParseContext context)
    {
        if (dispatch.ReassemblyLimitHit)
        {
            _AppendReassemblyLimitField(in parentField, dispatch.ReassemblyLimitDiscardedBytes, in context);
        }

        if (dispatch.ResyncSkippedBytes > 0)
        {
            _AppendResyncField(in parentField, dispatch.ResyncSkippedBytes, in context);
        }
    }

    #endregion

    #region Segment fields

    /// <summary>
    /// Shows the payload of a segment that is not dispatched on its own, and says why in the info text: its bytes wait in a
    /// reassembly buffer for a PDU that completes later, or it repeats or reorders data the stream already has.
    /// </summary>
    private void _AppendSegmentData(
        in MutField parentField, ReadOnlyMemory<byte> payload, TcpAnalysisFlags flags, in ParseContext context)
    {
        parentField.Append(_SegmentDataFieldId, FieldValue.NewBytes(payload));
        context.RecordGroupPresence(_TcpSegmentGroupId);

        // The first matching anomaly names the segment; a segment with none of them is plain buffered data.
        string suffix;
        if ((flags & TcpAnalysisFlags.SpuriousRetransmission) != 0)
        {
            suffix = _InfoSpuriousRetransmission;
        }
        else if ((flags & TcpAnalysisFlags.FastRetransmission) != 0)
        {
            suffix = _InfoFastRetransmission;
        }
        else if ((flags & TcpAnalysisFlags.Retransmission) != 0)
        {
            suffix = _InfoRetransmission;
        }
        else if ((flags & TcpAnalysisFlags.OutOfOrder) != 0)
        {
            suffix = _InfoOutOfOrder;
        }
        else
        {
            suffix = _InfoSegmentOfPdu;
        }

        parentField.AppendToPacketInfo(suffix);
    }

    /// <summary>
    /// On the segment that completes a PDU from several segments: lists the packets that carried its bytes
    /// (<c>tcp.segments</c> with one <c>tcp.segment</c> per packet), their count and the PDU length. Nothing is shown for a
    /// PDU that arrived in one packet, because no reassembly happened.
    /// </summary>
    private void _AppendSegmentFields(in MutField parentField, int[] packetIds, int pduLength, in ParseContext context)
    {
        if (packetIds.Length < 2)
        {
            return;
        }

        MutField segments = parentField.Append(_SegmentsFieldId, FieldValue.None);
        foreach (int packetId in packetIds)
        {
            // Frame numbers count from 1, packet ids from 0: the number a user sees in the packet list is id + 1.
            segments.Append(_SegmentFieldId, FieldValue.NewU64((ulong)packetId + 1));
        }

        segments.Append(_SegmentCountFieldId, FieldValue.NewU64((ulong)packetIds.Length));
        segments.Append(_ReassembledLengthFieldId, FieldValue.NewU64((ulong)pduLength));
        context.RecordGroupPresence(_TcpSegmentGroupId);
    }

    /// <summary>
    /// Reports that a reassembly limit dropped the message in progress, naming the limit and the number of dropped bytes.
    /// An expected failure that is named instead of truncating silently.
    /// </summary>
    private void _AppendReassemblyLimitField(in MutField parentField, int discardedBytes, in ParseContext context)
    {
        parentField.Append(_ReassemblyLimitFieldId, FieldValue.NewString(string.Create(
            CultureInfo.InvariantCulture,
            $"TCP reassembly limit (MaxBufferSize or MaxPduSize) reached: {discardedBytes} buffered bytes of the message in progress were dropped")));
        context.RecordGroupPresence(_TcpErrorGroupId);
    }

    /// <summary>
    /// Shows that the stream buffer discarded bytes to find the next message boundary again. The bytes are gone from the
    /// stream (they never become a message), so the packet that carried them is the only place that can say so.
    /// </summary>
    private void _AppendResyncField(in MutField parentField, int skippedBytes, in ParseContext context)
    {
        parentField.Append(_ResyncFieldId, FieldValue.NewString(string.Create(
            CultureInfo.InvariantCulture,
            $"{skippedBytes} bytes skipped to resynchronize the stream")));
        context.RecordGroupPresence(_TcpErrorGroupId);
    }

    #endregion

    #region Analysis

    /// <summary>
    /// Copies live analysis into an immutable effect. The mutable <see cref="TcpConnectionState"/> reference is dropped on
    /// purpose: it is owned by the ingest thread, and a stored effect is read by any number of replay threads.
    /// </summary>
    private static AnalysisEffect _ToAnalysisEffect(in TcpAnalysisResult analysis) =>
        // The enum casts are value casts: AnalysisEffectFlags must mirror TcpAnalysisFlags bit for bit (the internal enum cannot
        // be part of the public effect type), and TcpConnectionPhase is a byte enum. Keep both in sync when adding a value.
        new(
            StreamIndex: analysis.StreamIndex,
            Flags: (AnalysisEffectFlags)analysis.Flags,
            DupAckNum: analysis.DupAckNum,
            BytesInFlight: analysis.BytesInFlight,
            InitialRtt: analysis.InitialRtt,
            AckRtt: analysis.AckRtt,
            TimeRelative: analysis.TimeRelative,
            TimeDelta: analysis.TimeDelta,
            ScaledWindowSize: analysis.ScaledWindowSize,
            WindowScaleFactor: analysis.WindowScaleFactor,
            Phase: (byte)analysis.Phase,
            NoIpLayer: analysis.NoIpLayer,
            RelativeSeq: analysis.RelativeSeq,
            RelativeAck: analysis.RelativeAck,
            SequenceIsRelative: analysis.SequenceIsRelative,
            AckIsRelative: analysis.AckIsRelative,
            StreamLimitReached: analysis.StreamLimitReached,
            AcksPacketId: analysis.AcksPacketId,
            Rto: analysis.Rto,
            RtoPacketId: analysis.RtoPacketId,
            DupAckPacketId: analysis.DupAckPacketId,
            Completeness: analysis.Completeness);

    /// <summary>Rebuilds a tracker-free analysis result from a recorded effect.</summary>
    private static TcpAnalysisResult _AnalysisFromEffect(in AnalysisEffect effect) =>
        new()
        {
            Flags = (TcpAnalysisFlags)effect.Flags,
            StreamIndex = effect.StreamIndex,
            DupAckNum = effect.DupAckNum,
            BytesInFlight = effect.BytesInFlight,
            InitialRtt = effect.InitialRtt,
            AckRtt = effect.AckRtt,
            TimeRelative = effect.TimeRelative,
            TimeDelta = effect.TimeDelta,
            ScaledWindowSize = effect.ScaledWindowSize,
            WindowScaleFactor = effect.WindowScaleFactor,
            Phase = (TcpConnectionPhase)effect.Phase,
            NoIpLayer = effect.NoIpLayer,
            RelativeSeq = effect.RelativeSeq,
            RelativeAck = effect.RelativeAck,
            SequenceIsRelative = effect.SequenceIsRelative,
            AckIsRelative = effect.AckIsRelative,
            StreamLimitReached = effect.StreamLimitReached,
            AcksPacketId = effect.AcksPacketId,
            Rto = effect.Rto,
            RtoPacketId = effect.RtoPacketId,
            DupAckPacketId = effect.DupAckPacketId,
            Completeness = effect.Completeness,
        };

    /// <summary>
    /// Runs TCP connection tracking and segment analysis.
    /// Creates/finds the connection state, determines direction, and invokes the analyzer.
    /// For SYN packets with options, extracts the Window Scale value and passes it
    /// to the analyzer so it can be stored for future window calculations.
    /// </summary>
    private TcpAnalysisResult _RunAnalysis(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte flags, ushort window,
        int payloadLen,
        ReadOnlySpan<byte> tcpSegment, in ParseContext context)
    {
        Packet packet = parentField.Packet;

        // Try to build a connection key from IP addresses via sibling navigation.
        // Uses sibling walk to find the immediately enclosing IP layer,
        // which correctly handles tunnel scenarios (IP-in-IP, GRE).
        if (!_TryCreateConnectionKey(in parentField, srcPort, dstPort, out TcpConnectionKey key, out UInt128 srcAddr, in context))
        {
            // No IP layer found — return empty result with NoIpLayer flag
            // so the caller can append a diagnostic error field.
            return TcpAnalysisResult.Empty with
            {
                NoIpLayer = true
            };
        }

        // Direction relative to the normalized key (the key orders the two endpoints, so both directions share one state).
        // It is needed before the lookup: whether a SYN starts a new connection depends on which direction sent it.
        bool isForward = key.IsForward(srcAddr, srcPort);

        // Stream table limit (tcp.max_tracked_streams, see the setting). The setting is a ulong while the tracker compares
        // against a Dictionary count (int), so values from int.MaxValue up are clamped instead of overflowing the cast.
        // A full table is an expected failure: report it to the caller (it appends tcp.error.stream_limit) and keep going.
        int maxStreams = _MaxTrackedStreams >= int.MaxValue ? int.MaxValue : (int)_MaxTrackedStreams;
        if (!_ConnectionTracker.TryGetOrCreate(
            in key, maxStreams, isForward, flags, seqNum, out TcpConnectionState? conn, out bool reusedPorts))
        {
            return TcpAnalysisResult.Empty with
            {
                StreamLimitReached = true,
                ConnectionKey = key,
                SrcAddr = srcAddr,
            };
        }

        // A reused address/port pair is a new connection: the reassembly stream of the old one holds a cursor and detector
        // state that belong to bytes that no longer matter. Dropping it here keeps the new connection from starting
        // "behind a hole" of the old cursor.
        if (reusedPorts)
        {
            _ReassemblyEngine?.Remove(in key);
        }

        // Extract Window Scale from SYN/SYN-ACK options for connection state tracking.
        // This must be done eagerly (not lazily) because subsequent packets need the
        // scale factor to compute the correct scaled window size. The option is only valid in SYN segments (RFC 7323),
        // so other segments are not scanned. The lightweight scan avoids running the full options parser, which would
        // append fields, just to read one byte.
        int windowScale = -1;
        bool isSyn = (flags & 0x02) != 0;
        if (isSyn && tcpSegment.Length > TcpHeader.MinSize)
        {
            // Data offset is the high nibble of header byte 12, in 32-bit words. The options span is bounds-checked against
            // the segment length before slicing, so the scan stays inside the segment.
            int headerLen = (tcpSegment[12] >> 4) * 4; // data offset from raw header
            if (headerLen > TcpHeader.MinSize && headerLen <= tcpSegment.Length)
            {
                ReadOnlySpan<byte> optionsData = tcpSegment[TcpHeader.MinSize..headerLen];
                windowScale = _ExtractWindowScale(optionsData) ?? -1;
            }
        }

        // The analyzer mutates the connection state, which is why this method is first-parse only. The connection key and
        // source address are attached to the result for the reassembly step.
        TcpSegmentView segment = new(
            seqNum, ackNum, (uint)payloadLen, flags, window, packet.Timestamp.AsNanos, packet.Id.Value, srcPort, dstPort, windowScale);
        return TcpConnectionTracker.Analyze(conn, isForward, in segment, reusedPorts, _RelativeSequenceNumbers)
            with
        {
            ConnectionKey = key,
            SrcAddr = srcAddr
        };
    }

    #endregion

    #region Analysis fields

    /// <summary>
    /// Eagerly appends tcp.analysis container and its sub-fields to the parent field.
    /// Called for every tracked segment (the container always carries at least the connection state);
    /// the individual flag fields appear only for the conditions that were detected. Works on a recorded analysis
    /// as well as a live one, so a replay yields identical fields.
    /// </summary>
    /// <remarks>
    /// Layout: every anomaly flag sits under <c>tcp.analysis.flags</c>, which exists only when there is at least one, so a
    /// filter on that container selects every segment with an anomaly. Values that describe an anomaly (duplicate ACK number,
    /// the packet it repeats, the retransmission timeout) and the connection facts (acknowledged packet, bytes in flight,
    /// round trips, state) sit directly under <c>tcp.analysis</c>.
    /// </remarks>
    private void _AppendAnalysisFields(in MutField parentField, in TcpAnalysisResult analysis, in ParseContext context)
    {
        MutField analysisContainer = parentField.Append(_AnalysisFieldId, FieldValue.None);

        // An ambiguous acknowledgement is a property of the acknowledged packet, not an anomaly of this segment: it is shown
        // next to the "acknowledges frame" reference and does not make the segment count as one with analysis flags.
        TcpAnalysisFlags flags = analysis.Flags;
        if ((flags & ~TcpAnalysisFlags.AmbiguousAck) != TcpAnalysisFlags.None)
        {
            MutField flagsContainer = analysisContainer.Append(_AnalysisFlagsFieldId, FieldValue.None);
            _AppendAnalysisFlagFields(in flagsContainer, flags);
        }

        if ((flags & TcpAnalysisFlags.DuplicateAck) != 0)
        {
            analysisContainer.Append(_AnalysisDupAckNumFieldId, FieldValue.NewU64(analysis.DupAckNum));
            if (analysis.DupAckPacketId >= 0)
            {
                analysisContainer.Append(_AnalysisDupAckFrameFieldId, FieldValue.NewU64((ulong)analysis.DupAckPacketId + 1));
            }
        }

        if ((flags & (TcpAnalysisFlags.Retransmission | TcpAnalysisFlags.FastRetransmission)) != 0 && !double.IsNaN(analysis.Rto))
        {
            analysisContainer.Append(_AnalysisRtoFieldId, FieldValue.NewF64(analysis.Rto));
            if (analysis.RtoPacketId >= 0)
            {
                analysisContainer.Append(_AnalysisRtoFrameFieldId, FieldValue.NewU64((ulong)analysis.RtoPacketId + 1));
            }
        }

        if (analysis.AcksPacketId >= 0)
        {
            analysisContainer.Append(_AnalysisAcksFrameFieldId, FieldValue.NewU64((ulong)analysis.AcksPacketId + 1));
            if ((flags & TcpAnalysisFlags.AmbiguousAck) != 0)
            {
                analysisContainer.Append(_AnalysisAmbiguousAckFieldId, FieldValue.None);
            }
        }

        if (analysis.BytesInFlight > 0)
        {
            analysisContainer.Append(_AnalysisBytesInFlightFieldId, FieldValue.NewU64(analysis.BytesInFlight));
        }

        if (!double.IsNaN(analysis.AckRtt))
        {
            analysisContainer.Append(_AnalysisAckRttFieldId, FieldValue.NewF64(analysis.AckRtt));
        }

        // The initial round trip belongs to the connection, so it is shown on the packets after it was measured - but only
        // where the analysis has something else to say (an anomaly, an acknowledged packet, bytes in flight). On a packet
        // without any analysis result the container would otherwise carry nothing but this connection constant.
        bool hasAnalysisResult = flags != TcpAnalysisFlags.None || analysis.AcksPacketId >= 0 || analysis.BytesInFlight > 0
            || !double.IsNaN(analysis.AckRtt);
        if (hasAnalysisResult && !double.IsNaN(analysis.InitialRtt))
        {
            analysisContainer.Append(_AnalysisInitialRttFieldId, FieldValue.NewF64(analysis.InitialRtt));
        }

        // Connection state (always present when analysis container is shown)
        string phaseText = TcpConnectionTracker.GetPhaseDisplayText(analysis.Phase);
        analysisContainer.Append(_AnalysisConnectionStateFieldId, FieldValue.NewString(phaseText));
    }

    /// <summary>Appends one field per set analysis flag under the <c>tcp.analysis.flags</c> container.</summary>
    private void _AppendAnalysisFlagFields(in MutField flagsContainer, TcpAnalysisFlags flags)
    {
        if ((flags & TcpAnalysisFlags.ReusedPorts) != 0)
        {
            flagsContainer.Append(_AnalysisReusedPortsFieldId, FieldValue.None);
        }
        if ((flags & TcpAnalysisFlags.Retransmission) != 0)
        {
            flagsContainer.Append(_AnalysisRetransmissionFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.FastRetransmission) != 0)
        {
            flagsContainer.Append(_AnalysisFastRetransmissionFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.SpuriousRetransmission) != 0)
        {
            flagsContainer.Append(_AnalysisSpuriousRetransmissionFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.OutOfOrder) != 0)
        {
            flagsContainer.Append(_AnalysisOutOfOrderFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.DuplicateAck) != 0)
        {
            flagsContainer.Append(_AnalysisDuplicateAckFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.LostSegment) != 0)
        {
            flagsContainer.Append(_AnalysisLostSegmentFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.AckLostSegment) != 0)
        {
            flagsContainer.Append(_AnalysisAckLostSegmentFieldId, FieldValue.None);
        }
        if ((flags & TcpAnalysisFlags.PartialAck) != 0)
        {
            flagsContainer.Append(_AnalysisPartialAckFieldId, FieldValue.None);
        }
        if ((flags & TcpAnalysisFlags.KeepAlive) != 0)
        {
            flagsContainer.Append(_AnalysisKeepAliveFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.KeepAliveAck) != 0)
        {
            flagsContainer.Append(_AnalysisKeepAliveAckFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.ZeroWindow) != 0)
        {
            flagsContainer.Append(_AnalysisZeroWindowFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.ZeroWindowProbe) != 0)
        {
            flagsContainer.Append(_AnalysisZeroWindowProbeFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.ZeroWindowProbeAck) != 0)
        {
            flagsContainer.Append(_AnalysisZeroWindowProbeAckFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.WindowUpdate) != 0)
        {
            flagsContainer.Append(_AnalysisWindowUpdateFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.WindowFull) != 0)
        {
            flagsContainer.Append(_AnalysisWindowFullFieldId, FieldValue.NewBool(true));
        }
    }

    /// <summary>
    /// Appends <c>tcp.completeness</c> (the bitmask of what the capture contains of this conversation so far). Only the
    /// container with the numeric value is eager; the text and the six flag fields are built when somebody opens the
    /// container. Motivation: the value is on every packet of every connection, and eight appends plus a string per packet
    /// would be paid by ingest although almost nobody expands it.
    /// </summary>
    private void _AppendCompletenessFields(in MutField parentField, byte completeness, in ParseContext context)
    {
        parentField.AppendLazy(_CompletenessFieldId, FieldValue.NewU64(completeness), _CompletenessPopulator);
        context.RecordGroupPresence(_TcpCompletenessGroupId);
    }

    /// <summary>Lazy populator of <c>tcp.completeness</c>: the letter string and one boolean per conversation part.</summary>
    private ParseResult _PopulateCompleteness(in MutField container)
    {
        if (!container.Value.Data.TryGetAsU64(out ulong value))
        {
            return ParseError.InvalidData(ProtocolName, "Completeness container value is not of type U64");
        }

        byte bits = (byte)value;
        container.Append(_CompletenessStrFieldId, FieldValue.NewString(TcpConnectionTracker.FormatCompleteness(bits)));
        container.Append(_CompletenessSynFieldId, FieldValue.NewBool((bits & TcpConnectionState.SynSeen) != 0));
        container.Append(_CompletenessSynAckFieldId, FieldValue.NewBool((bits & TcpConnectionState.SynAckSeen) != 0));
        container.Append(_CompletenessAckFieldId, FieldValue.NewBool((bits & TcpConnectionState.AckSeen) != 0));
        container.Append(_CompletenessDataFieldId, FieldValue.NewBool((bits & TcpConnectionState.DataSeen) != 0));
        container.Append(_CompletenessFinFieldId, FieldValue.NewBool((bits & TcpConnectionState.FinSeen) != 0));
        container.Append(_CompletenessRstFieldId, FieldValue.NewBool((bits & TcpConnectionState.RstSeen) != 0));
        return 0;
    }

    #endregion
}
