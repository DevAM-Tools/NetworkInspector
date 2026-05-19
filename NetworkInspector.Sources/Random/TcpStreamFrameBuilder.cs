// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;

using NetworkInspector.Core;

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
/// </summary>
internal static class TcpStreamFrameBuilder
{
    #region Constants

    // ─── Protocol constants ───────────────────────────────────────────────────
    private const int EthHeaderSize = 14;   // bytes
    private const int IPv4HeaderSize = 20;  // bytes
    private const int IPv6HeaderSize = 40;  // bytes
    private const int TcpHeaderSize = 20;   // bytes
    private const ushort EtherTypeIPv4 = 0x0800;
    private const ushort EtherTypeIPv6 = 0x86DD;
    private const byte IpProtoTcp = 6;
    private const byte IPv4VersionIhl = 0x45;

    // ─── TCP flags ────────────────────────────────────────────────────────────
    private const byte FlagSyn = 0x02;
    private const byte FlagAck = 0x10;
    private const byte FlagPsh = 0x08;
    private const byte FlagFin = 0x01;

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
        TcpFrameLocation? location = layout.Locate(globalIndex);
        if (location is null)
        {
            return null;
        }

        TcpFrameLocation loc = location.Value;
        TcpStreamEndpoints ep = new(masterSeed, loc.StreamIndex, isIpv6);
        TcpFramePhase phase = layout.ClassifyPhase(loc.LocalFrameIndex);
        int step = layout.PhaseStep(loc.LocalFrameIndex);

        return phase switch
        {
            TcpFramePhase.Handshake => BuildHandshakeFrame(ep, step, isIpv6),
            TcpFramePhase.Data => BuildDataFrame(layout, options, masterSeed, ep, loc.StreamIndex, step, isIpv6),
            TcpFramePhase.Teardown => BuildTeardownFrame(layout, options, masterSeed, ep, loc.StreamIndex, step, isIpv6),
            _ => null,
        };
    }

    #endregion

    #region Private Helpers

    // ─── Handshake ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a handshake frame: step 0=SYN, 1=SYN-ACK, 2=ACK.
    /// </summary>
    private static byte[] BuildHandshakeFrame(
        in TcpStreamEndpoints ep, int step, bool isIpv6)
    {
        return step switch
        {
            // SYN: client → server, seq=ISN, ack=0
            0 => AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                ep.ClientIsn, 0,
                FlagSyn, ReadOnlySpan<byte>.Empty, isIpv6),

            // SYN-ACK: server → client, seq=server ISN, ack=client ISN+1
            1 => AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                ep.ServerIsn, ep.ClientIsn + 1,
                (byte)(FlagSyn | FlagAck), ReadOnlySpan<byte>.Empty, isIpv6),

            // ACK: client → server, seq=ISN+1, ack=server ISN+1
            _ => AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                ep.ClientIsn + 1, ep.ServerIsn + 1,
                FlagAck, ReadOnlySpan<byte>.Empty, isIpv6),
        };
    }

    // ─── Data ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a data segment frame at position <paramref name="dataStep"/>
    /// within the data phase. Computes accumulated sequence numbers by iterating
    /// all prior data segments' payload sizes.
    /// </summary>
    private static byte[] BuildDataFrame(
        in TcpStreamLayout layout,
        TcpStreamOptions options,
        ulong masterSeed,
        in TcpStreamEndpoints ep,
        int streamIndex,
        int dataStep,
        bool isIpv6)
    {
        // Compute accumulated sequence numbers up to this data frame.
        // SYN consumes 1 seq byte for both client and server.
        uint clientSeq = ep.ClientIsn + 1;
        uint serverSeq = ep.ServerIsn + 1;

        for (int i = 0; i < dataStep; i++)
        {
            int priorPayloadSize = DerivePayloadSize(masterSeed, streamIndex, i, options);
            bool priorIsClientToServer = (i % 2) == 0;
            if (priorIsClientToServer)
            {
                clientSeq += (uint)priorPayloadSize;
            }
            else
            {
                serverSeq += (uint)priorPayloadSize;
            }
        }

        // Current frame direction and payload
        bool clientToServer = (dataStep % 2) == 0;
        int payloadSize = DerivePayloadSize(masterSeed, streamIndex, dataStep, options);
        byte[] payload = DerivePayload(masterSeed, streamIndex, dataStep, payloadSize);

        if (clientToServer)
        {
            return AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                clientSeq, serverSeq,
                (byte)(FlagAck | FlagPsh), payload, isIpv6);
        }
        else
        {
            return AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                serverSeq, clientSeq,
                (byte)(FlagAck | FlagPsh), payload, isIpv6);
        }
    }

    // ─── Teardown ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a teardown frame: 0=FIN-ACK(client→server), 1=ACK(server→client),
    /// 2=FIN-ACK(server→client), 3=final ACK(client→server).
    /// Sequence numbers include all prior data segment payloads.
    /// </summary>
    private static byte[] BuildTeardownFrame(
        in TcpStreamLayout layout,
        TcpStreamOptions options,
        ulong masterSeed,
        in TcpStreamEndpoints ep,
        int streamIndex,
        int teardownStep,
        bool isIpv6)
    {
        // Accumulate all data segment payload sizes
        uint clientSeq = ep.ClientIsn + 1;
        uint serverSeq = ep.ServerIsn + 1;

        for (int i = 0; i < layout.DataFrames; i++)
        {
            int size = DerivePayloadSize(masterSeed, streamIndex, i, options);
            if ((i % 2) == 0)
            {
                clientSeq += (uint)size;
            }
            else
            {
                serverSeq += (uint)size;
            }
        }

        return teardownStep switch
        {
            // FIN-ACK: client → server
            0 => AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                clientSeq, serverSeq,
                (byte)(FlagFin | FlagAck), ReadOnlySpan<byte>.Empty, isIpv6),

            // ACK of client's FIN: server → client
            1 => AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                serverSeq, clientSeq + 1,  // FIN consumes 1 seq
                FlagAck, ReadOnlySpan<byte>.Empty, isIpv6),

            // FIN-ACK: server → client
            2 => AssembleFrame(
                ep.ServerMac, ep.ClientMac,
                ep.ServerIp, ep.ClientIp,
                ep.ServerPort, ep.ClientPort,
                serverSeq, clientSeq + 1,
                (byte)(FlagFin | FlagAck), ReadOnlySpan<byte>.Empty, isIpv6),

            // Final ACK: client → server
            _ => AssembleFrame(
                ep.ClientMac, ep.ServerMac,
                ep.ClientIp, ep.ServerIp,
                ep.ClientPort, ep.ServerPort,
                clientSeq + 1,  // After client's FIN consumed 1
                serverSeq + 1,  // Server's FIN consumed 1
                FlagAck, ReadOnlySpan<byte>.Empty, isIpv6),
        };
    }

    // ─── Payload derivation ───────────────────────────────────────────────────

    /// <summary>
    /// Deterministically computes the payload size for data segment <paramref name="dataIndex"/>
    /// in connection <paramref name="streamIndex"/>.
    /// </summary>
    private static int DerivePayloadSize(
        ulong masterSeed, int streamIndex, int dataIndex, TcpStreamOptions options)
    {
        // Seed namespace: high bits for stream, low bits for data index,
        // plus a distinct offset to avoid collisions with endpoint seeds.
        ulong seed = Xoroshiro128PlusPlus.DeriveFrameSeed(
            masterSeed,
            0x2_0000_0000UL + ((ulong)streamIndex << 16) + (ulong)dataIndex);
        Xoroshiro128PlusPlus rng = new(seed);
        return rng.NextRange(options.MinPayloadSize, options.MaxPayloadSize + 1);
    }

    /// <summary>
    /// Deterministically produces payload bytes for data segment <paramref name="dataIndex"/>
    /// in connection <paramref name="streamIndex"/>.
    /// </summary>
    private static byte[] DerivePayload(
        ulong masterSeed, int streamIndex, int dataIndex, int size)
    {
        ulong seed = Xoroshiro128PlusPlus.DeriveFrameSeed(
            masterSeed,
            0x3_0000_0000UL + ((ulong)streamIndex << 16) + (ulong)dataIndex);
        Xoroshiro128PlusPlus rng = new(seed);
        byte[] payload = new byte[size];
        rng.FillBytes(payload);
        return payload;
    }

    // ─── Frame assembly ───────────────────────────────────────────────────────

    /// <summary>
    /// Assembles a complete Ethernet/IP/TCP frame with correct headers and IPv4 checksum.
    /// </summary>
    private static byte[] AssembleFrame(
        byte[] srcMac, byte[] dstMac,
        byte[] srcIp, byte[] dstIp,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte tcpFlags, ReadOnlySpan<byte> payload,
        bool isIpv6)
    {
        int ipHeaderSize = isIpv6 ? IPv6HeaderSize : IPv4HeaderSize;
        int totalSize = EthHeaderSize + ipHeaderSize + TcpHeaderSize + payload.Length;
        byte[] frame = new byte[totalSize];

        // ── Ethernet header ──
        dstMac.CopyTo(frame.AsSpan(0));
        srcMac.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(12), isIpv6 ? EtherTypeIPv6 : EtherTypeIPv4);

        int ipOffset = EthHeaderSize;
        int tcpOffset = ipOffset + ipHeaderSize;

        if (isIpv6)
        {
            WriteIpv6Header(frame, ipOffset, srcIp, dstIp, payload.Length);
        }
        else
        {
            WriteIpv4Header(frame, ipOffset, srcIp, dstIp, payload.Length);
        }

        WriteTcpHeader(frame, tcpOffset, srcPort, dstPort, seqNum, ackNum, tcpFlags);

        // ── Payload ──
        if (payload.Length > 0)
        {
            payload.CopyTo(frame.AsSpan(tcpOffset + TcpHeaderSize));
        }

        return frame;
    }

    /// <summary>
    /// Writes a 20-byte IPv4 header with computed checksum.
    /// </summary>
    private static void WriteIpv4Header(
        byte[] frame, int offset, byte[] srcIp, byte[] dstIp, int payloadLength)
    {
        ushort totalLength = (ushort)(IPv4HeaderSize + TcpHeaderSize + payloadLength);

        frame[offset] = IPv4VersionIhl;       // Version 4, IHL 5
        frame[offset + 1] = 0;                // DSCP + ECN
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 2), totalLength);
        // Identification (offset+4..5): 0
        frame[offset + 6] = 0x40;             // DF flag
        frame[offset + 7] = 0x00;             // Fragment offset
        frame[offset + 8] = 64;               // TTL
        frame[offset + 9] = IpProtoTcp;
        // Checksum (offset+10..11): computed below
        srcIp.CopyTo(frame.AsSpan(offset + 12));
        dstIp.CopyTo(frame.AsSpan(offset + 16));

        // Compute one's-complement checksum
        uint sum = 0;
        for (int i = 0; i < IPv4HeaderSize; i += 2)
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
    private static void WriteIpv6Header(
        byte[] frame, int offset, byte[] srcIp, byte[] dstIp, int payloadLength)
    {
        ushort ipPayloadLength = (ushort)(TcpHeaderSize + payloadLength);

        frame[offset] = 0x60;                // Version 6
        // Traffic Class + Flow Label: 0
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 4), ipPayloadLength);
        frame[offset + 6] = IpProtoTcp;      // Next header
        frame[offset + 7] = 64;              // Hop limit
        srcIp.CopyTo(frame.AsSpan(offset + 8));
        dstIp.CopyTo(frame.AsSpan(offset + 24));
    }

    /// <summary>
    /// Writes a 20-byte TCP header. Checksum is left at 0 (simplified).
    /// </summary>
    private static void WriteTcpHeader(
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
