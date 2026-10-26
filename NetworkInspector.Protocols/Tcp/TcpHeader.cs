// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// TCP header struct (20 bytes minimum) parsed via ZeroAlloc [BinaryParsable].
/// Layout: SrcPort(2) DstPort(2) Seq(4) Ack(4) DataOff:4+Reserved:3+NS:1+Flags(1) Win(2) Csum(2) Urg(2)
/// </summary>
/// <remarks>
/// The binary parser generator reads the members in declaration order, so the declaration order (including the three
/// bit fields that share byte 12) must match the wire layout above; multi-byte values use the big-endian wrappers
/// (<c>U16BE</c>/<c>U32BE</c>). Options are not part of this struct: they follow the first 20 bytes and are described by
/// <see cref="DataOffset"/>. Parsing only checks that 20 bytes exist; a data offset below 5 words or beyond the segment
/// is rejected by the caller, not here. Thread safety: immutable value type.
/// </remarks>
[BinaryParsable]
internal readonly partial struct TcpHeader
{
    /// <summary>Minimum TCP header size in bytes.</summary>
    internal const int MinSize = 20;

    /// <summary>Source port number.</summary>
    public U16BE SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number.</summary>
    public U16BE DstPort
    {
        get; init;
    }

    /// <summary>Sequence number.</summary>
    public U32BE SeqNumber
    {
        get; init;
    }

    /// <summary>Acknowledgment number.</summary>
    public U32BE AckNumber
    {
        get; init;
    }

    /// <summary>Data offset in 32-bit words (upper 4 bits of byte 12).</summary>
    [BinaryField(BitCount = 4)]
    public byte DataOffset
    {
        get; init;
    }

    /// <summary>Reserved bits (3 bits, should be zero).</summary>
    [BinaryField(BitCount = 3)]
    public byte Reserved
    {
        get; init;
    }

    /// <summary>NS: ECN-nonce concealment protection flag (1 bit).</summary>
    [BinaryField(BitCount = 1)]
    public byte NsFlag
    {
        get; init;
    }

    /// <summary>TCP flags byte (CWR, ECE, URG, ACK, PSH, RST, SYN, FIN).</summary>
    public byte Flags
    {
        get; init;
    }

    /// <summary>Window size.</summary>
    public U16BE WindowSize
    {
        get; init;
    }

    /// <summary>Checksum.</summary>
    public U16BE Checksum
    {
        get; init;
    }

    /// <summary>Urgent pointer.</summary>
    public U16BE UrgentPointer
    {
        get; init;
    }

    /// <summary>Computes the header length in bytes from the DataOffset field.</summary>
    internal int HeaderLength => DataOffset * 4;
}
