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

        // The stream id is an engine-local counter handed to stream-aware boundary detectors as their per-stream key,
        // so it is unique until Clear, which restarts the counter together with the detector state.
        TcpStreamState state = new(_NextStreamId++, protocolId, config);
        _Connections[key] = state;
        return state;
    }

    /// <summary>
    /// Feeds a TCP segment payload into the appropriate direction buffer of a stream.
    /// </summary>
    /// <param name="stream">The stream state (from <see cref="GetOrCreateStream"/>).</param>
    /// <param name="isForward">Direction of this segment.</param>
    /// <param name="sequence">TCP sequence number of the first payload byte.</param>
    /// <param name="payload">The TCP payload data.</param>
    /// <returns><see langword="true"/> if the segment was accepted.</returns>
    internal static bool FeedSegment(TcpStreamState stream, bool isForward, uint sequence, ReadOnlyMemory<byte> payload)
    {
        SegmentBuffer buffer = stream.GetBuffer(isForward);
        return buffer.TryAppend(sequence, payload);
    }

    /// <summary>
    /// Tries to extract a complete PDU from the stream's direction buffer.
    /// </summary>
    /// <param name="stream">The stream state.</param>
    /// <param name="isForward">Direction to extract from.</param>
    /// <param name="pdu">The extracted PDU on success.</param>
    /// <returns><see langword="true"/> if a PDU was extracted.</returns>
    internal static bool TryExtractPdu(TcpStreamState stream, bool isForward, out ReadOnlyMemory<byte> pdu)
    {
        // Context for context-aware detectors; built per call as a value type, so there is no allocation.
        StreamDetectionContext context = new()
        {
            StreamId = stream.StreamId,
            ProtocolId = stream.ProtocolId,
            HandshakeObserved = stream.HandshakeObserved,
        };

        SegmentBuffer buffer = stream.GetBuffer(isForward);
        return buffer.TryExtractPdu(in context, out pdu);
    }

    /// <summary>Clears all tracked connections and resets the stream counter.</summary>
    internal void Clear()
    {
        // Each stream is cleared first so stream-aware detectors drop their per-stream state; only then are the
        // streams forgotten and the id counter restarted (ids would otherwise be reused with stale detector state).
        foreach (TcpStreamState state in _Connections.Values)
        {
            state.Clear();
        }
        _Connections.Clear();
        _NextStreamId = 0;
    }
}
