// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Per-TCP-connection reassembly state with forward and reverse segment buffers.
/// Each direction maintains an independent <see cref="SegmentBuffer"/> for PDU extraction.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> not thread-safe; used only by the ordered first parse of each packet (see
/// <see cref="TcpReassemblyEngine"/>). Both buffers use the same configuration but never share bytes.
/// </remarks>
internal sealed class TcpStreamState
{
    /// <summary>
    /// Gets the forward direction segment buffer. "Forward" follows <see cref="TcpConnectionKey.IsForward"/>: traffic
    /// from the numerically lower endpoint of the normalized key, which is not necessarily client to server.
    /// </summary>
    internal SegmentBuffer Forward
    {
        get;
    }

    /// <summary>Gets the reverse direction segment buffer (traffic from the higher endpoint of the normalized key).</summary>
    internal SegmentBuffer Reverse
    {
        get;
    }

    /// <summary>The protocol ID of the application-layer protocol being reassembled.</summary>
    internal ProtocolId ProtocolId
    {
        get;
    }

    /// <summary>Unique stream identifier assigned by the reassembly engine.</summary>
    internal ulong StreamId
    {
        get;
    }

    /// <summary>
    /// Whether the TCP handshake was observed for this connection (the capture contains its SYN or SYN-ACK, so the
    /// stream is seen from its start rather than joined mid-flow). Passed to context-aware PDU boundary detectors.
    /// </summary>
    internal bool HandshakeObserved
    {
        get; set;
    }

    /// <summary>Creates a new stream state with both direction buffers.</summary>
    /// <param name="streamId">Engine-assigned stream identifier.</param>
    /// <param name="protocolId">Application protocol being reassembled.</param>
    /// <param name="config">Reassembly configuration; each direction gets its own buffer built from it.</param>
    internal TcpStreamState(ulong streamId, ProtocolId protocolId, StreamReassemblyConfig config)
    {
        StreamId = streamId;
        ProtocolId = protocolId;
        Forward = new SegmentBuffer(config);
        Reverse = new SegmentBuffer(config);
    }

    /// <summary>Gets the segment buffer for the given direction.</summary>
    /// <param name="isForward"><see langword="true"/> for the forward direction.</param>
    /// <returns>The direction's buffer (never null).</returns>
    internal SegmentBuffer GetBuffer(bool isForward)
    {
        if (isForward)
        {
            return Forward;
        }

        return Reverse;
    }

    /// <summary>Clears both direction buffers and any per-stream detector state.</summary>
    internal void Clear()
    {
        // Order matters: a stream-aware detector is told to drop its per-stream state before the buffered bytes go away.
        Forward.ResetDetector(StreamId);
        Reverse.ResetDetector(StreamId);
        Forward.Clear();
        Reverse.Clear();
    }
}
