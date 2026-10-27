// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// TCP header struct (20 bytes minimum) decoded from network byte order (RFC 9293 §3.1).
/// Layout: SrcPort(2) DstPort(2) Seq(4) Ack(4) DataOff:4+Reserved:3+NS:1+Flags(1) Win(2) Csum(2) Urg(2)
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset reads with <see cref="BinaryPrimitives"/>) instead of a generated
/// parser, so the JIT elides the per-read bounds checks and the dissector gets plain integers; declaration order
/// mirrors the wire layout for readability only. Options are not part of this struct: they follow the first 20 bytes
/// and are described by <see cref="DataOffset"/>. Parsing only checks that 20 bytes exist; a data offset below 5 words
/// or beyond the segment is rejected by the caller, not here. Thread safety: immutable value type.
/// </remarks>
internal readonly struct TcpHeader
{
    /// <summary>Minimum TCP header size in bytes.</summary>
    internal const int MinSize = 20;

    /// <summary>Source port number (bytes 0..1).</summary>
    public ushort SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number (bytes 2..3).</summary>
    public ushort DstPort
    {
        get; init;
    }

    /// <summary>Sequence number (bytes 4..7).</summary>
    public uint SeqNumber
    {
        get; init;
    }

    /// <summary>Acknowledgment number (bytes 8..11).</summary>
    public uint AckNumber
    {
        get; init;
    }

    /// <summary>Data offset in 32-bit words (upper 4 bits of byte 12).</summary>
    public byte DataOffset
    {
        get; init;
    }

    /// <summary>Reserved bits (bits 3..1 of byte 12, should be zero).</summary>
    public byte Reserved
    {
        get; init;
    }

    /// <summary>NS: ECN-nonce concealment protection flag (bit 0 of byte 12).</summary>
    public byte NsFlag
    {
        get; init;
    }

    /// <summary>TCP flags byte (byte 13: CWR, ECE, URG, ACK, PSH, RST, SYN, FIN).</summary>
    public byte Flags
    {
        get; init;
    }

    /// <summary>Window size (bytes 14..15).</summary>
    public ushort WindowSize
    {
        get; init;
    }

    /// <summary>Checksum (bytes 16..17).</summary>
    public ushort Checksum
    {
        get; init;
    }

    /// <summary>Urgent pointer (bytes 18..19).</summary>
    public ushort UrgentPointer
    {
        get; init;
    }

    /// <summary>Computes the header length in bytes from the DataOffset field.</summary>
    internal int HeaderLength => DataOffset * 4;

    /// <summary>
    /// Decodes the fixed 20-byte header. Returns <see langword="false"/> with a default value when fewer than
    /// <see cref="MinSize"/> bytes are present. Longer input is accepted; only the first 20 bytes are read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out TcpHeader value)
    {
        // One length check up front; a short segment is an expected failure, not an exception.
        if (source.Length < MinSize)
        {
            value = default;
            return false;
        }

        // Exact-size slice: after this the JIT knows every index below is in range and drops the bounds checks.
        ReadOnlySpan<byte> h = source[..MinSize];
        byte offsetAndNs = h[12];
        value = new TcpHeader
        {
            SrcPort = BinaryPrimitives.ReadUInt16BigEndian(h),                // 0..2
            DstPort = BinaryPrimitives.ReadUInt16BigEndian(h[2..]),           // 2..4
            SeqNumber = BinaryPrimitives.ReadUInt32BigEndian(h[4..]),         // 4..8
            AckNumber = BinaryPrimitives.ReadUInt32BigEndian(h[8..]),         // 8..12
            DataOffset = (byte)(offsetAndNs >> 4),                            // byte 12, bits 7..4
            Reserved = (byte)((offsetAndNs >> 1) & 0x07),                     // byte 12, bits 3..1
            NsFlag = (byte)(offsetAndNs & 0x01),                              // byte 12, bit 0
            Flags = h[13],                                                    // 13
            WindowSize = BinaryPrimitives.ReadUInt16BigEndian(h[14..]),       // 14..16
            Checksum = BinaryPrimitives.ReadUInt16BigEndian(h[16..]),         // 16..18
            UrgentPointer = BinaryPrimitives.ReadUInt16BigEndian(h[18..]),    // 18..20
        };
        return true;
    }
}
