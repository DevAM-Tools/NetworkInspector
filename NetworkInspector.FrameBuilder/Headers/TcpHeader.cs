// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// TCP header (20 bytes, without options).
/// Layout per RFC 9293: SrcPort(2), DstPort(2), SeqNum(4), AckNum(4),
/// DataOffsetFlags(2), WindowSize(2), Checksum(2), UrgentPointer(2).
/// </summary>
/// <remarks>
/// <para>Checksum is left at 0 and patched by <c>TcpLayer</c>'s <c>FixPhase.InnerChecksum</c> post-fix.</para>
/// <para>Options can be appended after this header by using <c>TcpLayerWithOptions</c>.</para>
/// </remarks>
[BinaryWritable]
internal readonly partial struct TcpHeader
{
    /// <summary>Size of the base TCP header without options in bytes.</summary>
    internal const int Size = 20;

    /// <summary>Maximum TCP header size including options in bytes (data offset 15 × 4).</summary>
    internal const int MaxSize = 60;

    /// <summary>Source port number.</summary>
    internal U16BE SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number.</summary>
    internal U16BE DstPort
    {
        get; init;
    }

    /// <summary>Sequence number.</summary>
    internal U32BE SeqNum
    {
        get; init;
    }

    /// <summary>Acknowledgment number (significant when ACK flag is set).</summary>
    internal U32BE AckNum
    {
        get; init;
    }

    /// <summary>
    /// Data Offset(4 bits) + Reserved(3 bits) + Flags(9 bits).
    /// Use <see cref="MakeDataOffsetFlags"/> to construct.
    /// </summary>
    internal U16BE DataOffsetFlags
    {
        get; init;
    }

    /// <summary>Window size in bytes.</summary>
    internal U16BE WindowSize
    {
        get; init;
    }

    /// <summary>Checksum (including pseudo-header). Set to 0 for fixup.</summary>
    internal U16BE Checksum
    {
        get; init;
    }

    /// <summary>Urgent pointer (significant when URG flag is set).</summary>
    internal U16BE UrgentPointer
    {
        get; init;
    }

    /// <summary>
    /// Constructs the 16-bit DataOffset+Reserved+NS+Flags field.
    /// </summary>
    /// <param name="dataOffsetWords">
    /// Header length in 32-bit words (5 = 20 bytes, up to 15 = 60 bytes with options).
    /// Stored in bits 15-12.
    /// </param>
    /// <param name="flags">8-bit TCP flag bits (CWR/ECE/URG/ACK/PSH/RST/SYN/FIN — see <see cref="TcpFlags"/>).
    /// Stored in bits 7-0.</param>
    /// <param name="reservedNibble">
    /// 3-bit Reserved field (RFC 9293 §3.1) stored in bits 11-9. Must be 0 in
    /// conforming implementations; exposed for protocol-conformance and corruption-test
    /// scenarios. Only the low 3 bits are honoured. Default: 0.
    /// </param>
    /// <param name="nsFlag">
    /// NS flag (ECN-Nonce, RFC 3540). Stored in bit 8. Default: <c>false</c>.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort MakeDataOffsetFlags(int dataOffsetWords, byte flags, byte reservedNibble = 0, bool nsFlag = false)
    {
        // Bit layout (high -> low):
        //   15-12  DataOffset (4 bits)
        //   11-9   Reserved   (3 bits)
        //    8     NS flag    (1 bit)
        //    7-0   Flags      (8 bits)
        int reservedBits = (reservedNibble & 0b0000_0111) << 9;
        int nsBit = nsFlag ? (1 << 8) : 0;
        return (ushort)((dataOffsetWords << 12) | reservedBits | nsBit | flags);
    }

    /// <summary>
    /// Creates a TCP header with common defaults.
    /// Checksum is left at 0 and patched by <c>TcpLayer</c> <c>FixPhase.InnerChecksum</c>.
    /// </summary>
    /// <param name="srcPort">Source port number.</param>
    /// <param name="dstPort">Destination port number.</param>
    /// <param name="seqNum">Sequence number.</param>
    /// <param name="ackNum">Acknowledgment number; default 0.</param>
    /// <param name="flags">TCP control flags (see <see cref="TcpFlags"/>); default <see cref="TcpFlags.Syn"/>.</param>
    /// <param name="windowSize">Receive window size; default 65535.</param>
    /// <param name="dataOffsetWords">Header length in 32-bit words (5 = 20 bytes); default 5.</param>
    /// <param name="urgentPointer">Urgent pointer; default 0.</param>
    /// <param name="reservedNibble">
    /// 3-bit Reserved field (bits 11-9 of DataOffsetFlags). Must be 0 per RFC 9293;
    /// exposed for corruption-test scenarios.
    /// </param>
    /// <param name="nsFlag">
    /// NS flag (ECN-Nonce, RFC 3540, bit 8 of DataOffsetFlags). Default: <c>false</c>.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TcpHeader Create(
        ushort srcPort,
        ushort dstPort,
        uint seqNum,
        uint ackNum = 0,
        byte flags = TcpFlags.Syn,
        ushort windowSize = 65535,
        int dataOffsetWords = 5,
        ushort urgentPointer = 0,
        byte reservedNibble = 0,
        bool nsFlag = false)
    {
        return new TcpHeader
        {
            SrcPort = srcPort,
            DstPort = dstPort,
            SeqNum = seqNum,
            AckNum = ackNum,
            DataOffsetFlags = MakeDataOffsetFlags(dataOffsetWords, flags, reservedNibble, nsFlag),
            WindowSize = windowSize,
            Checksum = (ushort)0, // patched by TcpLayer FixPhase.InnerChecksum
            UrgentPointer = urgentPointer,
        };
    }
}
