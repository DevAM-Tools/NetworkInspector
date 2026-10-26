// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the size limit of DNS-over-TCP reassembly: a DNS message can be 65535 bytes long, and together with its 2-byte
/// length prefix the stream carries 65537 bytes for it.
/// </summary>
internal sealed class DnsOverTcpLimitTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);
    private const ushort _ClientPort = 52000;
    private const ushort _ServerPort = 53;

    private static byte[] _ClientFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(_ClientPort, _ServerPort, seqNum: seq, ackNum: ack, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static byte[] _ServerFrame(uint seq, uint ack, byte flags)
    {
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(_ServerPort, _ClientPort, seqNum: seq, ackNum: ack, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>A DNS message of <paramref name="messageLength"/> bytes (header only, rest zero) behind its length prefix.</summary>
    private static byte[] _PrefixedMessage(ushort transactionId, int messageLength)
    {
        byte[] pdu = new byte[2 + messageLength];
        BinaryPrimitives.WriteUInt16BigEndian(pdu, (ushort)messageLength);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), transactionId);
        return pdu;
    }

    [Test]
    [Arguments(65535)]
    [Arguments(65000)]
    public async Task DnsMessageUpToTheMaximumLengthIsReassembledFromManySegments(int messageLength)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        const uint clientIsn = 1000;
        const uint serverIsn = 2000;
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(clientIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

        // The frame builder emits one Ethernet frame per call, so the stream is sent in segments that fit one frame.
        const int segmentLength = 1400;
        byte[] pdu = _PrefixedMessage(0x4242, messageLength);
        Packet? completing = null;
        int packetIndex = 3;
        for (int sent = 0; sent < pdu.Length; sent += segmentLength)
        {
            int length = Math.Min(segmentLength, pdu.Length - sent);
            completing = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(clientIsn + 1 + (uint)sent, serverIsn + 1, TcpFlags.Psh | TcpFlags.Ack, pdu.AsSpan(sent, length)),
                packetIndex,
                Timestamp.FromMillis(packetIndex));
            packetIndex++;
        }

        await Assert.That(completing).IsNotNull();
        await ProtocolTestHelper.AssertU64Field(stack, completing!, "dns.id", 0x4242).ConfigureAwait(false);
    }
}
