// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Direction tag attached to every TCP segment emitted by a
/// <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/>.  Used by the
/// optional <see cref="TcpSegmentMutator"/> callback so a single mutator
/// can branch on which half of the bidirectional flow is producing the
/// current segment.
/// </summary>
public enum TcpDirection : byte
{
    /// <summary>Segment emitted by the client side (initiator of the connection).</summary>
    ClientToServer = 0,

    /// <summary>Segment emitted by the server side (passive opener).</summary>
    ServerToClient = 1,
}

/// <summary>
/// Lifecycle phase of a TCP segment within a
/// <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/>.  Conveyed to the
/// optional <see cref="TcpSegmentMutator"/> via
/// <see cref="TcpSegmentContext"/> so callers can branch by phase
/// (e.g. set PSH only on the last data segment).
/// </summary>
public enum TcpLifecycle : byte
{
    /// <summary>3-way-handshake segment (SYN / SYN+ACK / final ACK).</summary>
    Handshake = 0,

    /// <summary>Application-data carrying segment (one or more per Write call).</summary>
    Data = 1,

    /// <summary>Bare ACK segment (no payload, no SYN/FIN).</summary>
    Ack = 2,

    /// <summary>Window-update segment (bare ACK whose Window field changed).</summary>
    WindowUpdate = 3,

    /// <summary>FIN segment (or FIN+ACK) emitted during a graceful teardown.</summary>
    Fin = 4,

    /// <summary>RST segment emitted during an abortive teardown.</summary>
    Rst = 5,
}
