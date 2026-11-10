// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Master coordinator for TCP stream reassembly across all connections.
/// Manages per-connection <see cref="TcpStreamState"/> instances and routes
/// segments to the correct direction buffer for PDU extraction.
/// <para>
/// The engine looks up reassembly configurations from the protocol stack
/// based on the dispatched application-layer protocol. Connections whose
/// application protocol has no reassembly config are not tracked.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The engine is used only by the ordered first parse of each packet; replays
/// re-dispatch the recorded PDUs and never reach it.
/// </para>
/// <para>
/// <b>Limits:</b> there is no own cap on the number of streams here. Streams are created only for connections that the
/// connection tracker accepted, so the tracker's stream limit bounds this table, and each direction buffer is bounded
/// by <see cref="StreamReassemblyConfig.MaxBufferSize"/>.
/// </para>
/// <para>
/// <b>Lifetime:</b> a stream ends in one of three ways, so a finished connection stops holding memory: each direction is
/// closed by its own FIN or RST (<see cref="Close"/>), a reused address/port pair drops the whole stream
/// (<see cref="Remove"/>), and the engine as a whole lives as long as its protocol instance (a stack swap builds a new one).
/// </para>
/// </summary>
internal sealed class TcpReassemblyEngine
{
    /// <summary>
    /// Active connections keyed by normalized TCP connection key (seeded hash, see <see cref="TcpConnectionKey"/>).
    /// A stream is created on the first segment for a connection whose application protocol has a reassembly config.
    /// </summary>
    private readonly Dictionary<TcpConnectionKey, TcpStreamState> _Connections = [];

    /// <summary>Monotonically increasing stream ID counter.</summary>
    private ulong _NextStreamId;

    /// <summary>Reference to the protocol stack for reassembly config lookups.</summary>
    private readonly Stack _Stack;

    /// <summary>Creates a new reassembly engine bound to the given protocol stack.</summary>
    /// <param name="stack">Stack that owns the protocols and their reassembly configurations.</param>
    internal TcpReassemblyEngine(Stack stack)
    {
        _Stack = stack;
    }

    /// <summary>
    /// Gets (or creates) the stream state for a connection, if the dispatched protocol
    /// has a reassembly config registered.
    /// </summary>
    /// <param name="key">The normalized connection key.</param>
    /// <param name="protocolId">The application-layer protocol ID from port dispatch.</param>
    /// <param name="isForward">
    /// Output: <see langword="true"/> if the current packet is in the forward direction
    /// (source matches the first endpoint in the normalized key).
    /// </param>
    /// <param name="srcAddr">Source IP address as UInt128.</param>
    /// <param name="srcPort">Source TCP port.</param>
    /// <returns>
    /// The stream state for this connection, or <see langword="null"/> if the protocol
    /// has no reassembly config.
    /// </returns>
    internal TcpStreamState? GetOrCreateStream(
        in TcpConnectionKey key,
        ProtocolId protocolId,
        UInt128 srcAddr,
        ushort srcPort,
        out bool isForward)
    {
        // The direction is computed even when no stream exists, so callers can use it either way. "Forward" is the
        // lower endpoint of the normalized key, not the client.
        isForward = key.IsForward(srcAddr, srcPort);

        // Known connection: reuse. Note the stream keeps the protocol it was created with; a later segment dispatched
        // to a different protocol on the same connection does not create a second stream.
        if (_Connections.TryGetValue(key, out TcpStreamState? existing))
        {
            return existing;
        }

        // Check if the protocol has a reassembly config. Absence is the normal case (most protocols are
        // message-oriented) and is reported as null, not as an error; nothing is stored for such connections, so
        // they are looked up again on the next segment.
        StreamReassemblyConfig? config = _Stack.GetStreamReassemblyConfig(protocolId);
        if (config == null)
        {
            return null;
        }

        // A size the registration left unassigned is the current value of its limit (settings reassembly.*), read once for the
        // new stream: a change applies to streams that start afterwards. One small allocation per connection, not per segment.
        config = config.WithLimits(_Stack.Limits);

        // The stream id is an engine-local counter handed to stream-aware boundary detectors as their per-stream key. It is
        // never reused for the life of the engine, so a detector that still holds state for a removed stream cannot confuse
        // it with a later one.
        TcpStreamState state = new(_NextStreamId++, protocolId, config);
        _Connections[key] = state;
        return state;
    }

    /// <summary>
    /// Finds the stream of a connection without creating it. Used by segments without payload (FIN, RST): a connection that
    /// never carried data must not get a stream just because it ended.
    /// </summary>
    /// <param name="key">The normalized connection key.</param>
    /// <returns>The stream state, or <see langword="null"/> when none exists.</returns>
    internal TcpStreamState? TryGetStream(in TcpConnectionKey key) =>
        _Connections.TryGetValue(key, out TcpStreamState? state)
            ? state
            : null;

    /// <summary>
    /// Drops the stream of <paramref name="key"/>, if there is one. Called when the connection tracker started a new
    /// connection on a reused address/port pair: the cursors and detector state of the old connection must not leak into
    /// the new one (its first bytes would sit "behind a hole" of the old cursor until the buffer limit kills the stream).
    /// </summary>
    /// <param name="key">The normalized connection key.</param>
    internal void Remove(in TcpConnectionKey key)
    {
        if (_Connections.Remove(key, out TcpStreamState? state))
        {
            // Clear tells stream-aware detectors to forget the stream id before the buffered bytes go away.
            state.Clear();
        }
    }

    /// <summary>
    /// Feeds a TCP segment payload into the appropriate direction buffer of a stream.
    /// </summary>
    /// <param name="stream">The stream state (from <see cref="GetOrCreateStream"/>).</param>
    /// <param name="isForward">Direction of this segment.</param>
    /// <param name="sequence">TCP sequence number of the first payload byte.</param>
    /// <param name="payload">The TCP payload data.</param>
    /// <param name="packetId">Id of the packet that carries the payload, reported back with each extracted PDU.</param>
    /// <returns><see langword="true"/> if the segment was accepted.</returns>
    internal static bool FeedSegment(
        TcpStreamState stream, bool isForward, uint sequence, ReadOnlyMemory<byte> payload, int packetId)
    {
        SegmentBuffer buffer = stream.GetBuffer(isForward);
        StreamDetectionContext context = _CreateContext(stream, isForward, streamClosed: false);
        return buffer.TryAppend(sequence, payload, packetId, in context);
    }

    /// <summary>
    /// Tries to extract a complete PDU from the stream's direction buffer.
    /// </summary>
    /// <param name="stream">The stream state.</param>
    /// <param name="isForward">Direction to extract from.</param>
    /// <param name="streamClosed">
    /// <see langword="true"/> when the sender of this direction sent FIN: a detector may then frame a message that ends
    /// with the connection.
    /// </param>
    /// <param name="pdu">The extracted PDU on success.</param>
    /// <param name="packetIds">Ids of the packets that carried the PDU's bytes.</param>
    /// <returns><see langword="true"/> if a PDU was extracted.</returns>
    internal static bool TryExtractPdu(
        TcpStreamState stream, bool isForward, bool streamClosed, out ReadOnlyMemory<byte> pdu, out int[] packetIds)
    {
        SegmentBuffer buffer = stream.GetBuffer(isForward);
        StreamDetectionContext context = _CreateContext(stream, isForward, streamClosed);
        return buffer.TryExtractPdu(in context, out pdu, out packetIds);
    }

    /// <summary>
    /// Closes one direction. On FIN the bytes that are still buffered in order are handed out as a final PDU; on RST they are
    /// dropped. Returns <see langword="true"/> when <paramref name="tail"/> holds bytes to dispatch. After this call the
    /// direction refuses every further append, so later segments of this direction fall back to the plain port dispatch.
    /// </summary>
    /// <param name="stream">The stream state.</param>
    /// <param name="isForward">Direction whose sender closed.</param>
    /// <param name="isRst"><see langword="true"/> for RST (discard), <see langword="false"/> for FIN (deliver).</param>
    /// <param name="tail">The final PDU on success.</param>
    /// <param name="packetIds">Ids of the packets that carried the tail's bytes.</param>
    /// <returns><see langword="true"/> if a final PDU was produced.</returns>
    internal static bool Close(
        TcpStreamState stream, bool isForward, bool isRst, out ReadOnlyMemory<byte> tail, out int[] packetIds)
    {
        SegmentBuffer buffer = stream.GetBuffer(isForward);
        StreamDetectionContext context = _CreateContext(stream, isForward, streamClosed: true);
        return buffer.Close(in context, isRst, out tail, out packetIds);
    }

    /// <summary>
    /// Reads and clears the pending limit event of one direction (see <see cref="SegmentBuffer.TakeLimitHit"/>).
    /// </summary>
    /// <param name="stream">The stream state.</param>
    /// <param name="isForward">Direction to read.</param>
    /// <param name="discardedBytes">Number of buffered bytes the event dropped.</param>
    /// <returns><see langword="true"/> when a limit event was pending.</returns>
    internal static bool TakeLimitHit(TcpStreamState stream, bool isForward, out int discardedBytes) =>
        stream.GetBuffer(isForward).TakeLimitHit(out discardedBytes);

    /// <summary>
    /// Reads and clears the bytes one direction skipped to resynchronize (see <see cref="SegmentBuffer.TakeResyncSkip"/>).
    /// </summary>
    /// <param name="stream">The stream state.</param>
    /// <param name="isForward">Direction to read.</param>
    /// <returns>The number of skipped bytes; 0 when the stream stayed in sync.</returns>
    internal static int TakeResyncSkip(TcpStreamState stream, bool isForward) =>
        stream.GetBuffer(isForward).TakeResyncSkip();

    /// <summary>
    /// Builds the per-call context for context-aware detectors. A value type, so there is no allocation on the per-segment
    /// path.
    /// </summary>
    private static StreamDetectionContext _CreateContext(TcpStreamState stream, bool isForward, bool streamClosed) => new()
    {
        StreamId = stream.StreamId,
        ProtocolId = stream.ProtocolId,
        HandshakeObserved = stream.HandshakeObserved,
        IsForward = isForward,
        StreamClosed = streamClosed,
    };
}
