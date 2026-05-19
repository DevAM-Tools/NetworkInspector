// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Construction-time configuration for a
/// <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/>.  All fields have
/// safe defaults; callers override only what their test scenario needs.
/// </summary>
/// <param name="ClientIsn">Initial Send Sequence number used by the client (SEQ of the SYN). Default <c>1000</c>.</param>
/// <param name="ServerIsn">Initial Send Sequence number used by the server (SEQ of the SYN+ACK). Default <c>9000</c>.</param>
/// <param name="Mss">Maximum Segment Size used to split application-data Writes into TCP segments. Default <c>1460</c> bytes.</param>
/// <param name="WindowSize">Default window size advertised in every emitted segment. Default <c>65535</c>.</param>
public readonly record struct TcpConnectionOptions(
    uint ClientIsn = 1000,
    uint ServerIsn = 9000,
    ushort Mss = 1460,
    ushort WindowSize = 65535);
