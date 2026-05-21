// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Constants;

/// <summary>
/// TCP flag bit values for the Flags field in the TCP header.
/// Can be combined with bitwise OR.
/// </summary>
public static class TcpFlags
{
    /// <summary>FIN — No more data from sender (0x01).</summary>
    public const byte Fin = 0x01;

    /// <summary>SYN — Synchronize sequence numbers (0x02).</summary>
    public const byte Syn = 0x02;

    /// <summary>RST — Reset the connection (0x04).</summary>
    public const byte Rst = 0x04;

    /// <summary>PSH — Push function (0x08).</summary>
    public const byte Psh = 0x08;

    /// <summary>ACK — Acknowledgment field is significant (0x10).</summary>
    public const byte Ack = 0x10;

    /// <summary>URG — Urgent pointer field is significant (0x20).</summary>
    public const byte Urg = 0x20;

    /// <summary>ECE — ECN-Echo (0x40).</summary>
    public const byte Ece = 0x40;

    /// <summary>CWR — Congestion Window Reduced (0x80).</summary>
    public const byte Cwr = 0x80;

    /// <summary>SYN + ACK (0x12).</summary>
    public const byte SynAck = Syn | Ack;

    /// <summary>FIN + ACK (0x11).</summary>
    public const byte FinAck = Fin | Ack;

    /// <summary>PSH + ACK (0x18).</summary>
    public const byte PshAck = Psh | Ack;
}
