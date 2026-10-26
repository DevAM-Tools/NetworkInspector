// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Builds a single TCP frame by index without any mutable state.
/// <para>
/// Given a <see cref="TcpStreamLayout"/>, a master seed, and a global frame index,
/// this class deterministically produces the complete frame bytes — including correct
/// SYN/SYN-ACK/ACK handshake, properly sequenced data segments with ACKs for received
/// data, and a full FIN teardown. Every frame is independently reproducible from its
/// index alone.
/// </para>
/// <para>
/// <b>Sequence number computation:</b> Data segment payload sizes are deterministic
/// (derived from per-segment seeds). To compute the accumulated sequence numbers for
/// data frame N, we iterate data frames 0..N-1 and sum their payload sizes by direction.
/// This is O(N) per frame, bounded by <see cref="TcpStreamOptions.SegmentsPerStream"/>
/// which is typically small (10–100).
/// </para>
/// <para>
/// <b>Why stateless:</b> the source supports random access by frame id, so a frame must not depend on the frames
/// generated before it. The alternative (a generator that carries the per-stream sequence state from frame to frame)
/// would make <c>FrameById</c> replay the stream. The price is the repeated derivation of endpoints and sequence numbers
/// per frame, acceptable for a synthetic test source. Sequence numbers are <see cref="uint"/> sums that wrap, which is
/// exactly TCP's arithmetic modulo 2^32 (the project does not enable overflow checking). The generated TCP checksum is
/// zero and the window is 65535; only the IPv4 header checksum is computed.
/// </para>
/// <para><b>Thread-safety:</b> static and pure; safe from any thread.</para>
/// </summary>
internal static class TcpStreamFrameBuilder
{
    #region Constants

    // ─── Protocol constants ───────────────────────────────────────────────────
    private const int _EthHeaderSize = 14;   // bytes
    private const int _IPv4HeaderSize = 20;  // bytes
    private const int _IPv6HeaderSize = 40;  // bytes
    private const int _TcpHeaderSize = 20;   // bytes
    private const ushort _EtherTypeIPv4 = 0x0800;
    private const ushort _EtherTypeIPv6 = 0x86DD;
    private const byte _IpProtoTcp = 6;
    private const byte _IPv4VersionIhl = 0x45;

    // ─── Seed namespaces (see SegmentSeedId) ─────────────────────────────────
    /// <summary>Historical seed namespace of the payload size of a data segment: 2 x 2^32.</summary>
    internal const ulong PayloadSizeNamespace = 0x2_0000_0000UL;

    /// <summary>Historical seed namespace of the payload bytes of a data segment: 3 x 2^32.</summary>
    internal const ulong PayloadBytesNamespace = 0x3_0000_0000UL;

    // ─── TCP flags ────────────────────────────────────────────────────────────
    // Bit values of the flags byte of the TCP header (RFC 9293 section 3.1).
    private const byte _FlagSyn = 0x02;
    private const byte _FlagAck = 0x10;
    private const byte _FlagPsh = 0x08;
    private const byte _FlagFin = 0x01;

    #endregion

    #region Internal API

    /// <summary>
    /// Deterministically generates a single TCP stream frame.
    /// </summary>
    /// <param name="layout">Precomputed frame layout.</param>
    /// <param name="options">TCP stream configuration.</param>
    /// <param name="masterSeed">Master PRNG seed.</param>
    /// <param name="globalIndex">Global frame index.</param>
    /// <param name="isIpv6">Whether to generate IPv6 frames.</param>
    /// <returns>
    /// Complete frame bytes, or <c>null</c> if <paramref name="globalIndex"/>
    /// is out of range.
    /// </returns>
    internal static byte[]? BuildFrame(
        in TcpStreamLayout layout,
        TcpStreamOptions options,
        ulong masterSeed,
        int globalIndex,
        bool isIpv6)
    {
        // Where the frame sits (stream, local position) comes from arithmetic on the layout; an index outside the layout
        // is an expected result (null), not an error.
        TcpFrameLocation? location = layout.Locate(globalIndex);
        if (location is null)
        {
            return null;
        }

        // Everything the frame needs is derived from (masterSeed, stream, local position): no state is carried over,
        // which keeps any frame reproducible on its own (see the class remarks for the cost).
        TcpFrameLocation loc = location.Value;
        TcpStreamEndpoints ep = new(masterSeed, loc.StreamIndex, isIpv6);
        TcpFramePhase phase = layout.ClassifyPhase(loc.LocalFrameIndex);
        int step = layout.PhaseStep(loc.LocalFrameIndex);

        return phase switch
        {
            TcpFramePhase.Handshake => _BuildHandshakeFrame(ep, step, isIpv6),
            TcpFramePhase.Data => _BuildDataFrame(layout, options, masterSeed, ep, loc.StreamIndex, step, isIpv6),
            TcpFramePhase.Teardown => _BuildTeardownFrame(layout, options, masterSeed, ep, loc.StreamIndex, step, isIpv6),
            _ => null,
        };
    }

    #endregion

    #region Private Helpers

    // ─── Handshake ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a handshake frame: step 0=SYN, 1=SYN-ACK, 2=ACK.
    /// </summary>
    private static byte[] _BuildHandshakeFrame(
        in TcpStreamEndpoints ep, int step, bool isIpv6)
    {
        return step switch
        {
            // SYN: client → server, seq=ISN, ack=0
            0 => _AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                ep.ClientIsn, 0,
                _FlagSyn, ReadOnlySpan<byte>.Empty, isIpv6),

            // SYN-ACK: server → client, seq=server ISN, ack=client ISN+1
            1 => _AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                ep.ServerIsn, unchecked(ep.ClientIsn + 1),
                (byte)(_FlagSyn | _FlagAck), ReadOnlySpan<byte>.Empty, isIpv6),

            // ACK: client → server, seq=ISN+1, ack=server ISN+1
            _ => _AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                unchecked(ep.ClientIsn + 1), unchecked(ep.ServerIsn + 1),
                _FlagAck, ReadOnlySpan<byte>.Empty, isIpv6),
        };
    }

    // ─── Data ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a data segment frame at position <paramref name="dataStep"/>
    /// within the data phase. Computes accumulated sequence numbers by iterating
    /// all prior data segments' payload sizes.
    /// </summary>
    private static byte[] _BuildDataFrame(
        in TcpStreamLayout layout,
        TcpStreamOptions options,
        ulong masterSeed,
        in TcpStreamEndpoints ep,
        int streamIndex,
        int dataStep,
        bool isIpv6)
    {
        // Compute accumulated sequence numbers up to this data frame.
        // SYN consumes 1 seq byte for both client and server. The +1 applies even when the handshake is not generated:
        // the data then looks like a capture that started after the handshake. Data segments alternate direction
        // (even index client to server, odd server to client); a sender's sequence number advances by the payload
        // size of every earlier segment it sent, and the acknowledgement number of a segment is the other side's
        // current sequence number (everything it received so far).
        // Sequence numbers are uint sums that wrap modulo 2^32, which is TCP's own arithmetic. The wrap is spelled out with
        // unchecked instead of relying on the project's default, so a build with overflow checking cannot turn it into an
        // OverflowException.
        uint clientSeq = unchecked(ep.ClientIsn + 1);
        uint serverSeq = unchecked(ep.ServerIsn + 1);

        for (int i = 0; i < dataStep; i++)
        {
            int priorPayloadSize = _DerivePayloadSize(masterSeed, streamIndex, i, options);
            bool priorIsClientToServer = (i % 2) == 0;
            if (priorIsClientToServer)
            {
                clientSeq = unchecked(clientSeq + (uint)priorPayloadSize);
            }
            else
            {
                serverSeq = unchecked(serverSeq + (uint)priorPayloadSize);
            }
        }

        // Current frame direction and payload
        bool clientToServer = (dataStep % 2) == 0;
        int payloadSize = _DerivePayloadSize(masterSeed, streamIndex, dataStep, options);
        byte[] payload = _DerivePayload(masterSeed, streamIndex, dataStep, payloadSize);

        if (clientToServer)
        {
            return _AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                clientSeq, serverSeq,
                (byte)(_FlagAck | _FlagPsh), payload, isIpv6);
        }
        else
        {
            return _AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                serverSeq, clientSeq,
                (byte)(_FlagAck | _FlagPsh), payload, isIpv6);
        }
    }

    // ─── Teardown ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a teardown frame: 0=FIN-ACK(client→server), 1=ACK(server→client),
    /// 2=FIN-ACK(server→client), 3=final ACK(client→server).
    /// Sequence numbers include all prior data segment payloads.
    /// </summary>
    private static byte[] _BuildTeardownFrame(
        in TcpStreamLayout layout,
        TcpStreamOptions options,
        ulong masterSeed,
        in TcpStreamEndpoints ep,
        int streamIndex,
        int teardownStep,
        bool isIpv6)
    {
        // Accumulate all data segment payload sizes (every data frame precedes the teardown, so all of them count).
        // The loop re-derives each payload size from its seed, the same values the data frames used.
        // uint sums that wrap modulo 2^32, spelled out with unchecked (see _BuildDataFrame).
        uint clientSeq = unchecked(ep.ClientIsn + 1);
        uint serverSeq = unchecked(ep.ServerIsn + 1);

        for (int i = 0; i < layout.DataFrames; i++)
        {
            int size = _DerivePayloadSize(masterSeed, streamIndex, i, options);
            if ((i % 2) == 0)
            {
                clientSeq = unchecked(clientSeq + (uint)size);
            }
            else
            {
                serverSeq = unchecked(serverSeq + (uint)size);
            }
        }

        return teardownStep switch
        {
            // FIN-ACK: client → server
            0 => _AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                clientSeq, serverSeq,
                (byte)(_FlagFin | _FlagAck), ReadOnlySpan<byte>.Empty, isIpv6),

            // ACK of client's FIN: server → client
            1 => _AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                serverSeq, unchecked(clientSeq + 1),  // FIN consumes 1 seq
                _FlagAck, ReadOnlySpan<byte>.Empty, isIpv6),

            // FIN-ACK: server → client
            2 => _AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                serverSeq, unchecked(clientSeq + 1),
                (byte)(_FlagFin | _FlagAck), ReadOnlySpan<byte>.Empty, isIpv6),

            // Final ACK: client → server
            _ => _AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                unchecked(clientSeq + 1),  // After client's FIN consumed 1
                unchecked(serverSeq + 1),  // Server's FIN consumed 1
                _FlagAck, ReadOnlySpan<byte>.Empty, isIpv6),
        };
    }

    // ─── Payload derivation ───────────────────────────────────────────────────

    /// <summary>
    /// Deterministically computes the payload size for data segment <paramref name="dataIndex"/>
    /// in connection <paramref name="streamIndex"/>.
    /// </summary>
    private static int _DerivePayloadSize(
        ulong masterSeed, int streamIndex, int dataIndex, TcpStreamOptions options)
    {
        // The size and the bytes of one segment use separate seeds (see SegmentSeedId), so the payload size can be derived
        // without generating the bytes (the sequence sums need only sizes).
        ulong seed = Xoroshiro128PlusPlus.DeriveFrameSeed(
            masterSeed,
            SegmentSeedId(PayloadSizeNamespace, streamIndex, dataIndex));

        // NextRange takes an exclusive upper bound, hence + 1 (the options guarantee Min <= Max, and Max is at most 65 515, far
        // below int.MaxValue, so the bound cannot wrap).
        Xoroshiro128PlusPlus rng = new(seed);
        return rng.NextRange(options.MinPayloadSize, options.MaxPayloadSize + 1);
    }

    /// <summary>
    /// Seed id of one data segment in one of the two segment namespaces (size, bytes).
    /// <para>
    /// Seed namespaces in <c>DeriveFrameSeed(masterSeed, id)</c>: frame ids of the random frame modes are below 2^32;
    /// endpoint ids are <c>2^32 + stream</c> (below 2^32 + 2^31); the segment ids below keep their historical layout
    /// (<c>namespace + (stream &lt;&lt; 16) + segment</c>, in 2 x 2^32 and 3 x 2^32) as long as both indexes are below
    /// 65 536, so the output of every existing seed stays the same. Beyond that the historical layout lets the ids of
    /// different (stream, segment) pairs, and of the two namespaces, coincide (the stream bits overlap the segment
    /// bits), which repeated or correlated the generated values between those pairs. Such pairs get a second layout
    /// instead: bit 63 set, bit 62 selecting size or bytes, then the stream index in bits 31..61 and the segment index in
    /// bits 0..30. Both indexes are non-negative ints (31 bits each), so that layout is collision-free, and bit 63 keeps
    /// it out of every historical range (all of them are below 2^34).
    /// </para>
    /// </summary>
    internal static ulong SegmentSeedId(ulong historicalNamespace, int streamIndex, int dataIndex)
    {
        if (streamIndex < 0x1_0000 && dataIndex < 0x1_0000)
        {
            return historicalNamespace + ((ulong)streamIndex << 16) + (ulong)dataIndex;
        }

        ulong namespaceBit = historicalNamespace == PayloadBytesNamespace
            ? 1UL << 62
            : 0UL;
        return (1UL << 63) | namespaceBit | ((ulong)streamIndex << 31) | (uint)dataIndex;
    }

    /// <summary>
    /// Deterministically produces payload bytes for data segment <paramref name="dataIndex"/>
    /// in connection <paramref name="streamIndex"/>.
    /// </summary>
    private static byte[] _DerivePayload(
        ulong masterSeed, int streamIndex, int dataIndex, int size)
    {
        ulong seed = Xoroshiro128PlusPlus.DeriveFrameSeed(
            masterSeed,
            SegmentSeedId(PayloadBytesNamespace, streamIndex, dataIndex));
        Xoroshiro128PlusPlus rng = new(seed);
        byte[] payload = new byte[size];
        rng.FillBytes(payload);
        return payload;
    }

    // ─── Frame assembly ───────────────────────────────────────────────────────

    /// <summary>
    /// Assembles a complete Ethernet/IP/TCP frame with correct headers and IPv4 checksum.
    /// </summary>
    private static byte[] _AssembleFrame(
        byte[] srcMac, byte[] dstMac,
        byte[] srcIp, byte[] dstIp,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte tcpFlags, ReadOnlySpan<byte> payload,
        bool isIpv6)
    {
        int ipHeaderSize = isIpv6 ? _IPv6HeaderSize : _IPv4HeaderSize;
        int totalSize = _EthHeaderSize + ipHeaderSize + _TcpHeaderSize + payload.Length;
        byte[] frame = new byte[totalSize];

        // ── Ethernet header ──
        dstMac.CopyTo(frame.AsSpan(0));
        srcMac.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(12), isIpv6 ? _EtherTypeIPv6 : _EtherTypeIPv4);

        int ipOffset = _EthHeaderSize;
        int tcpOffset = ipOffset + ipHeaderSize;

        if (isIpv6)
        {
            _WriteIpv6Header(frame, ipOffset, srcIp, dstIp, payload.Length);
        }
        else
        {
            _WriteIpv4Header(frame, ipOffset, srcIp, dstIp, payload.Length);
        }

        _WriteTcpHeader(frame, tcpOffset, srcPort, dstPort, seqNum, ackNum, tcpFlags);

        // ── Payload ──
        if (payload.Length > 0)
        {
            payload.CopyTo(frame.AsSpan(tcpOffset + _TcpHeaderSize));
        }

        return frame;
    }

    /// <summary>
    /// Writes a 20-byte IPv4 header with computed checksum.
    /// </summary>
    private static void _WriteIpv4Header(
        byte[] frame, int offset, byte[] srcIp, byte[] dstIp, int payloadLength)
    {
        // The unchecked cast to 16 bits assumes payloadLength <= 65 495; larger payloads (not rejected by the options)
        // would wrap the length field.
        ushort totalLength = (ushort)(_IPv4HeaderSize + _TcpHeaderSize + payloadLength);

        frame[offset] = _IPv4VersionIhl;       // Version 4, IHL 5
        frame[offset + 1] = 0;                // DSCP + ECN
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 2), totalLength);
        // Identification (offset+4..5): 0
        frame[offset + 6] = 0x40;             // DF flag
        frame[offset + 7] = 0x00;             // Fragment offset
        frame[offset + 8] = 64;               // TTL
        frame[offset + 9] = _IpProtoTcp;
        // Checksum (offset+10..11): computed below
        srcIp.CopyTo(frame.AsSpan(offset + 12));
        dstIp.CopyTo(frame.AsSpan(offset + 16));

        // Compute the header checksum (RFC 791): one's-complement sum of the ten 16-bit big-endian words of the header,
        // the checksum field still zero at this point, carries folded back into the low 16 bits, then inverted.
        uint sum = 0;
        for (int i = 0; i < _IPv4HeaderSize; i += 2)
        {
            sum += (uint)(frame[offset + i] << 8 | frame[offset + i + 1]);
        }
        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 10), (ushort)~sum);
    }

    /// <summary>
    /// Writes a 40-byte IPv6 header.
    /// </summary>
    private static void _WriteIpv6Header(
        byte[] frame, int offset, byte[] srcIp, byte[] dstIp, int payloadLength)
    {
        // 16-bit payload length (TCP header plus data); assumes payloadLength <= 65 515, see the IPv4 header above.
        ushort ipPayloadLength = (ushort)(_TcpHeaderSize + payloadLength);

        frame[offset] = 0x60;                // Version 6
        // Traffic Class + Flow Label: 0
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 4), ipPayloadLength);
        frame[offset + 6] = _IpProtoTcp;      // Next header
        frame[offset + 7] = 64;              // Hop limit
        srcIp.CopyTo(frame.AsSpan(offset + 8));
        dstIp.CopyTo(frame.AsSpan(offset + 24));
    }

    /// <summary>
    /// Writes a 20-byte TCP header. Checksum is left at 0 (simplified).
    /// </summary>
    private static void _WriteTcpHeader(
        byte[] frame, int offset,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte flags)
    {
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 2), dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset + 4), seqNum);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset + 8), ackNum);
        frame[offset + 12] = 0x50;         // Data offset = 5 (20 bytes)
        frame[offset + 13] = flags;
        // Window size: 65535
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 14), 65535);
        // Checksum (offset+16..17): 0 (simplified)
        // Urgent pointer (offset+18..19): 0
    }

    #endregion
}
