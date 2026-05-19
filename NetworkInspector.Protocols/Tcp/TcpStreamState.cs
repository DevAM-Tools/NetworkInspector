// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Per-TCP-connection reassembly state with forward and reverse segment buffers.
/// Each direction maintains an independent <see cref="SegmentBuffer"/> for PDU extraction.
/// </summary>
internal sealed class TcpStreamState
{
    /// <summary>Gets the forward direction (client → server) segment buffer.</summary>
    internal SegmentBuffer Forward
    {
        get;
    }

    /// <summary>Gets the reverse direction (server → client) segment buffer.</summary>
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

    /// <summary>Whether the TCP handshake was observed for this connection.</summary>
    internal bool HandshakeObserved
    {
        get; set;
    }

    /// <summary>Creates a new stream state with both direction buffers.</summary>
    internal TcpStreamState(ulong streamId, ProtocolId protocolId, StreamReassemblyConfig config)
    {
        StreamId = streamId;
        ProtocolId = protocolId;
        Forward = new SegmentBuffer(config);
        Reverse = new SegmentBuffer(config);
    }

    /// <summary>Gets the segment buffer for the given direction.</summary>
    internal SegmentBuffer GetBuffer(bool isForward) => isForward ? Forward : Reverse;

    /// <summary>Clears both direction buffers.</summary>
    internal void Clear()
    {
        Forward.Clear();
        Reverse.Clear();
    }
}
