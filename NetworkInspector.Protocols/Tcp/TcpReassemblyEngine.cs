// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
/// </summary>
internal sealed class TcpReassemblyEngine
{
    /// <summary>Active connections keyed by normalized TCP connection key.</summary>
    private readonly Dictionary<TcpConnectionKey, TcpStreamState> _Connections = [];

    /// <summary>Monotonically increasing stream ID counter.</summary>
    private ulong _NextStreamId;

    /// <summary>Reference to the protocol stack for reassembly config lookups.</summary>
    private readonly Stack _Stack;

    /// <summary>Creates a new reassembly engine bound to the given protocol stack.</summary>
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
        isForward = key.IsForward(srcAddr, srcPort);

        if (_Connections.TryGetValue(key, out TcpStreamState? existing))
        {
            return existing;
        }

        // Check if the protocol has a reassembly config
        StreamReassemblyConfig? config = _Stack.GetStreamReassemblyConfig(protocolId);
        if (config == null)
        {
            return null;
        }

        TcpStreamState state = new(_NextStreamId++, protocolId, config);
        _Connections[key] = state;
        return state;
    }

    /// <summary>
    /// Feeds a TCP segment payload into the appropriate direction buffer of a stream.
    /// </summary>
    /// <param name="stream">The stream state (from <see cref="GetOrCreateStream"/>).</param>
    /// <param name="isForward">Direction of this segment.</param>
    /// <param name="payload">The TCP payload data.</param>
    /// <returns><see langword="true"/> if the segment was accepted.</returns>
    internal static bool FeedSegment(TcpStreamState stream, bool isForward, ReadOnlyMemory<byte> payload)
    {
        SegmentBuffer buffer = stream.GetBuffer(isForward);
        return buffer.AppendSegment(payload);
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
        foreach (TcpStreamState state in _Connections.Values)
        {
            state.Clear();
        }
        _Connections.Clear();
        _NextStreamId = 0;
    }
}
