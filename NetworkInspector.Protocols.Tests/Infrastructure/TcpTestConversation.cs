// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Builds the frames of one TCP connection for tests that need to control the segmentation: sequence and acknowledgement
/// numbers are tracked, the payload is cut into segments of a chosen size, and single segments can be built explicitly to
/// express a retransmission, a reordering or a gap.
/// </summary>
/// <remarks>
/// Why a helper of its own: the frame builder offers a ready-made connection, but the reassembly and analysis tests need to
/// say exactly which segment comes when (a late segment, a repeated segment, a FIN with data). This type only produces the
/// frames; the test parses them in the order it wants. Thread safety: not thread-safe, one instance per test.
/// </remarks>
internal sealed class TcpTestConversation
{
    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private readonly ushort _ClientPort;
    private readonly ushort _ServerPort;
    private readonly ushort _Window;

    /// <summary>Creates a conversation; sequence numbers start at the given initial numbers.</summary>
    /// <param name="serverPort">Server port of the connection.</param>
    /// <param name="clientPort">Client port of the connection.</param>
    /// <param name="clientIsn">Initial sequence number of the client.</param>
    /// <param name="serverIsn">Initial sequence number of the server.</param>
    /// <param name="window">Window advertised in every segment.</param>
    internal TcpTestConversation(ushort serverPort, ushort clientPort = 50000, uint clientIsn = 1000, uint serverIsn = 9000, ushort window = 65535)
    {
        _ServerPort = serverPort;
        _ClientPort = clientPort;
        _Window = window;
        ClientSeq = clientIsn;
        ServerSeq = serverIsn;
    }

    /// <summary>All frames built so far, in the order they were built.</summary>
    internal List<byte[]> Frames { get; } = [];

    /// <summary>Next sequence number the client sends.</summary>
    internal uint ClientSeq { get; private set; }

    /// <summary>Next sequence number the server sends.</summary>
    internal uint ServerSeq { get; private set; }

    /// <summary>Appends SYN, SYN-ACK and the final ACK.</summary>
    internal void Handshake()
    {
        Frames.Add(_Build(fromClient: true, ClientSeq, 0, TcpFlags.Syn, default));
        ClientSeq++;
        Frames.Add(_Build(fromClient: false, ServerSeq, ClientSeq, TcpFlags.SynAck, default));
        ServerSeq++;
        Frames.Add(_Build(fromClient: true, ClientSeq, ServerSeq, TcpFlags.Ack, default));
    }

    /// <summary>Sends <paramref name="payload"/> from the client in segments of at most <paramref name="mss"/> bytes.</summary>
    /// <param name="payload">Bytes to send.</param>
    /// <param name="mss">Largest payload of one segment.</param>
    internal void ClientSend(ReadOnlySpan<byte> payload, int mss = 1460) => _Send(fromClient: true, payload, mss);

    /// <summary>Sends <paramref name="payload"/> from the server in segments of at most <paramref name="mss"/> bytes.</summary>
    /// <param name="payload">Bytes to send.</param>
    /// <param name="mss">Largest payload of one segment.</param>
    internal void ServerSend(ReadOnlySpan<byte> payload, int mss = 1460) => _Send(fromClient: false, payload, mss);

    /// <summary>
    /// Builds a client segment with explicit numbers and does not add it to <see cref="Frames"/> or advance the sequence
    /// numbers: the test places it itself (late, twice, out of order).
    /// </summary>
    /// <param name="seq">Wire sequence number.</param>
    /// <param name="payload">Payload bytes.</param>
    /// <param name="flags">TCP flags.</param>
    internal byte[] ClientSegment(uint seq, ReadOnlySpan<byte> payload, byte flags = TcpFlags.PshAck) =>
        _Build(fromClient: true, seq, ServerSeq, flags, payload);

    /// <summary>Builds a server segment with explicit numbers, see <see cref="ClientSegment"/>.</summary>
    /// <param name="seq">Wire sequence number.</param>
    /// <param name="payload">Payload bytes.</param>
    /// <param name="flags">TCP flags.</param>
    internal byte[] ServerSegment(uint seq, ReadOnlySpan<byte> payload, byte flags = TcpFlags.PshAck) =>
        _Build(fromClient: false, seq, ClientSeq, flags, payload);

    /// <summary>Appends a FIN-ACK from the client (one sequence number).</summary>
    /// <param name="payload">Optional data carried by the FIN segment.</param>
    internal void ClientFin(ReadOnlySpan<byte> payload = default)
    {
        Frames.Add(_Build(fromClient: true, ClientSeq, ServerSeq, TcpFlags.FinAck, payload));
        ClientSeq += (uint)payload.Length + 1;
    }

    /// <summary>Appends a FIN-ACK from the server (one sequence number).</summary>
    /// <param name="payload">Optional data carried by the FIN segment.</param>
    internal void ServerFin(ReadOnlySpan<byte> payload = default)
    {
        Frames.Add(_Build(fromClient: false, ServerSeq, ClientSeq, TcpFlags.FinAck, payload));
        ServerSeq += (uint)payload.Length + 1;
    }

    /// <summary>Appends an RST-ACK from the client.</summary>
    internal void ClientRst() => Frames.Add(_Build(fromClient: true, ClientSeq, ServerSeq, (byte)(TcpFlags.Rst | TcpFlags.Ack), default));

    /// <summary>Appends an RST-ACK from the server.</summary>
    internal void ServerRst() => Frames.Add(_Build(fromClient: false, ServerSeq, ClientSeq, (byte)(TcpFlags.Rst | TcpFlags.Ack), default));

    /// <summary>Appends a pure ACK from the client for everything the server sent.</summary>
    internal void ClientAck() => Frames.Add(_Build(fromClient: true, ClientSeq, ServerSeq, TcpFlags.Ack, default));

    /// <summary>Appends a pure ACK from the server for everything the client sent.</summary>
    internal void ServerAck() => Frames.Add(_Build(fromClient: false, ServerSeq, ClientSeq, TcpFlags.Ack, default));

    /// <summary>
    /// Parses all frames in order on <paramref name="stack"/>, frame i at i milliseconds, and returns the packets.
    /// </summary>
    /// <param name="stack">The protocol stack.</param>
    /// <param name="firstPacketId">Packet id of the first frame.</param>
    internal Packet[] ParseAll(Stack stack, int firstPacketId = 0)
    {
        Packet[] packets = new Packet[Frames.Count];
        for (int i = 0; i < Frames.Count; i++)
        {
            packets[i] = ProtocolTestHelper.ParseFrame(stack, Frames[i], firstPacketId + i, Timestamp.FromMillis(i));
        }

        return packets;
    }

    private void _Send(bool fromClient, ReadOnlySpan<byte> payload, int mss)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            int length = Math.Min(mss, payload.Length - offset);
            ReadOnlySpan<byte> slice = payload.Slice(offset, length);
            if (fromClient)
            {
                Frames.Add(_Build(fromClient: true, ClientSeq, ServerSeq, TcpFlags.PshAck, slice));
                ClientSeq += (uint)length;
            }
            else
            {
                Frames.Add(_Build(fromClient: false, ServerSeq, ClientSeq, TcpFlags.PshAck, slice));
                ServerSeq += (uint)length;
            }

            offset += length;
        }
    }

    private byte[] _Build(bool fromClient, uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = fromClient
            ? new(_ServerMac, _ClientMac)
            : new(_ClientMac, _ServerMac);
        IPv4Layer ip = fromClient
            ? new(_ClientIp, _ServerIp)
            : new(_ServerIp, _ClientIp);
        ushort src = fromClient
            ? _ClientPort
            : _ServerPort;
        ushort dst = fromClient
            ? _ServerPort
            : _ClientPort;
        TcpLayer tcp = new(src, dst, seqNum: seq, ackNum: ack, flags: flags, windowSize: _Window);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }
}
