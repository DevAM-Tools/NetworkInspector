// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Verifies that re-parsing an already parsed packet reproduces the first parse field-by-field, and
/// that the watermark inside the stateful protocols separates first parses from re-parses correctly.
/// <para>
/// There is no parse-mode parameter: <see cref="Packet.ParseFrame"/> is used throughout. The first
/// parse of a packet id drives the stateful trackers, every later parse of that id replays what the
/// first one recorded.
/// </para>
/// </summary>
internal sealed class RedissectIdentityTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    [Test]
    public async Task Udp_ReparseAfterFirstParse_ProducesIdenticalPacket()
    {
        byte[] frameData = _BuildUdpFrame(srcPort: 12345, dstPort: 53);
        using Stack stack = _BuildStack();
        Frame frame = _CreateFrame(stack, frameData, 0);

        Packet first = Packet.ParseFrame(new PacketId(0), stack, frame);
        Packet reparse = Packet.ParseFrame(new PacketId(0), stack, frame);

        await PacketFieldComparer.AssertFieldIdentical(stack, first, reparse);
    }

    [Test]
    public async Task GetEffectLayerKey_UdpHeaderSlice_PacksEthernetIpv4Offset()
    {
        using Stack stack = _BuildStack();
        byte[] frameData = _BuildUdpFrame();
        Frame frame = _CreateFrame(stack, frameData, 0);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        int udpKey = packet.GetEffectLayerKey(packet.Frame.Data.Slice(34, 8));
        int ipv4Key = packet.GetEffectLayerKey(packet.Frame.Data.Slice(14, 20));

        await Assert.That(udpKey).IsEqualTo(34);
        await Assert.That(ipv4Key).IsEqualTo(14);
        await Assert.That(udpKey).IsNotEqualTo(ipv4Key);
    }

    [Test]
    public async Task Udp_Sequence_ReparseAfterFirstParse_AllIdentical()
    {
        using Stack stack = _BuildStack();

        for (int i = 0; i < 32; i++)
        {
            byte[] frameData = _BuildUdpFrame(srcPort: (ushort)(1000 + i), dstPort: 53);
            Frame frame = _CreateFrame(stack, frameData, i);
            Packet first = Packet.ParseFrame(new PacketId(i), stack, frame);
            Packet reparse = Packet.ParseFrame(new PacketId(i), stack, frame);
            await PacketFieldComparer.AssertFieldIdentical(stack, first, reparse);
        }
    }

    [Test]
    public async Task Udp_ReparseIdAboveWatermark_TakesFirstParsePath()
    {
        using Stack stack = _BuildStack();
        Frame frame0 = _CreateFrame(stack, _BuildUdpFrame(srcPort: 1000), 0);
        Frame frame1 = _CreateFrame(stack, _BuildUdpFrame(srcPort: 1001), 1);

        Packet first = Packet.ParseFrame(new PacketId(0), stack, frame0);
        Packet second = Packet.ParseFrame(new PacketId(1), stack, frame1);

        // Id 1 is above the watermark left by id 0, so it must take the first-parse path and draw a
        // fresh stream index from the tracker instead of replaying anything.
        await Assert.That(_ReadStreamIndex(stack, second)).IsEqualTo(_ReadStreamIndex(stack, first) + 1);
    }

    [Test]
    public async Task Udp_JumpInPacketId_Throws()
    {
        using Stack stack = _BuildStack();
        Packet.ParseFrame(new PacketId(0), stack, _CreateFrame(stack, _BuildUdpFrame(srcPort: 1000), 0));

        await Assert.That(() => Packet.ParseFrame(new PacketId(2), stack, _CreateFrame(stack, _BuildUdpFrame(srcPort: 2000), 2)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Udp_RepeatedFirstParseOfSameId_ReparseStillMatches()
    {
        using Stack stack = _BuildStack();
        byte[] frameData = _BuildUdpFrame();
        Frame frame = _CreateFrame(stack, frameData, 0);

        Packet.ParseFrame(new PacketId(0), stack, frame);
        Packet second = Packet.ParseFrame(new PacketId(0), stack, frame);
        Packet third = Packet.ParseFrame(new PacketId(0), stack, frame);

        await PacketFieldComparer.AssertFieldIdentical(stack, second, third);
    }

    [Test]
    public async Task Udp_RecycledFirstParse_ThenReparse_Matches()
    {
        using Stack stack = _BuildStack();
        Frame frame0 = _CreateFrame(stack, _BuildUdpFrame(srcPort: 1000), 0);
        Frame frame1 = _CreateFrame(stack, _BuildUdpFrame(srcPort: 1001), 1);

        Packet recycled = Packet.ParseFrame(new PacketId(0), stack, frame0);
        RecycleError? error = Packet.TryParseFrame(recycled, new PacketId(1), stack, frame1);
        await Assert.That(error).IsNull();

        Packet reparse = Packet.ParseFrame(new PacketId(1), stack, frame1);
        await PacketFieldComparer.AssertFieldIdentical(stack, recycled, reparse);
    }

    [Test]
    public async Task Udp_ConcurrentReparse_MatchesFirstParse()
    {
        const int count = 64;
        using Stack stack = _BuildStack();
        Frame[] frames = new Frame[count];
        Packet[] references = new Packet[count];

        for (int i = 0; i < count; i++)
        {
            byte[] frameData = _BuildUdpFrame(srcPort: (ushort)(2000 + i), dstPort: 53);
            frames[i] = _CreateFrame(stack, frameData, i);
            references[i] = Packet.ParseFrame(new PacketId(i), stack, frames[i]);
        }

        await Parallel.ForAsync(0, count, async (i, _) =>
        {
            Packet reparse = Packet.ParseFrame(new PacketId(i), stack, frames[i]);
            await PacketFieldComparer.AssertFieldIdentical(stack, references[i], reparse);
        });
    }

    /// <summary>
    /// The interleaving proof: while one thread keeps parsing new packet ids for the first time,
    /// several reader threads re-parse every id that has already been announced. Re-parses must stay
    /// field-identical even though first parses of higher ids are running concurrently.
    /// <para>
    /// The producer captures a field snapshot of each reference packet before announcing it. Readers
    /// compare against that snapshot rather than against the shared packet, because materializing a
    /// packet mutates it in place and several readers must not do that to the same instance.
    /// </para>
    /// </summary>
    [Test]
    public async Task Udp_ConcurrentReparse_DuringOngoingFirstParse_Identical()
    {
        const int count = 256;
        const int readers = 4;
        using Stack stack = _BuildStack();
        Frame[] frames = new Frame[count];
        PacketFieldComparer.PacketFieldSnapshot[] references = new PacketFieldComparer.PacketFieldSnapshot[count];

        for (int i = 0; i < count; i++)
        {
            frames[i] = _CreateFrame(stack, _BuildUdpFrame(srcPort: (ushort)(3000 + i), dstPort: 53), i);
        }

        // -1 = nothing announced yet. Written only by the producer, read by every reader.
        int announced = -1;

        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                Packet first = Packet.ParseFrame(new PacketId(i), stack, frames[i]);
                references[i] = PacketFieldComparer.CaptureFields(first);
                Volatile.Write(ref announced, i);
            }
        });

        Task[] readerTasks = new Task[readers];
        for (int r = 0; r < readers; r++)
        {
            readerTasks[r] = Task.Run(async () =>
            {
                for (int i = 0; i < count; i++)
                {
                    SpinWait spin = default;
                    while (Volatile.Read(ref announced) < i)
                    {
                        spin.SpinOnce();
                    }

                    Packet reparse = Packet.ParseFrame(new PacketId(i), stack, frames[i]);
                    await PacketFieldComparer.AssertMatchesSnapshot(stack, references[i], reparse);
                }
            });
        }

        await producer;
        await Task.WhenAll(readerTasks);
    }

    [Test]
    public async Task Tcp_HandshakeThenData_ReparseIdentical()
    {
        using Stack stack = _BuildStack();

        IPv4Address client = new(0x0A000001);
        IPv4Address server = new(0x0A000002);
        const ushort clientPort = 52000;
        const ushort serverPort = 80;
        uint cIsn = 1000;
        uint sIsn = 2000;

        byte[][] frames =
        [
            _BuildTcpFrame(client, server, clientPort, serverPort, cIsn, 0, TcpFlags.Syn),
            _BuildTcpFrame(server, client, serverPort, clientPort, sIsn, cIsn + 1, TcpFlags.SynAck),
            _BuildTcpFrame(client, server, clientPort, serverPort, cIsn + 1, sIsn + 1, TcpFlags.Ack),
            _BuildTcpFrame(
                client, server, clientPort, serverPort, cIsn + 1, sIsn + 1,
                TcpFlags.Psh | TcpFlags.Ack,
                [0x47, 0x45, 0x54, 0x20, 0x2F, 0x20, 0x48, 0x54, 0x54, 0x50, 0x2F, 0x31, 0x31, 0x0D, 0x0A, 0x0D, 0x0A]),
        ];

        Packet[] firstParsed = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            Frame frame = _CreateFrame(stack, frames[i], i);
            firstParsed[i] = Packet.ParseFrame(new PacketId(i), stack, frame);
        }

        for (int i = 0; i < frames.Length; i++)
        {
            Frame frame = _CreateFrame(stack, frames[i], i);
            Packet reparse = Packet.ParseFrame(new PacketId(i), stack, frame);
            await PacketFieldComparer.AssertFieldIdentical(stack, firstParsed[i], reparse);
        }
    }

    [Test]
    public async Task Tcp_ReassembledDns_ReparseIdentical()
    {
        using Stack stack = _BuildStack();

        IPv4Address client = new(0x0A000001);
        IPv4Address server = new(0x0A000002);
        const ushort clientPort = 52000;
        const ushort serverPort = 53;
        uint cIsn = 7000;
        uint sIsn = 8000;

        byte[] pdu = _BuildDnsTcpPdu(0xDEAD);
        byte[][] frames =
        [
            _BuildTcpFrame(client, server, clientPort, serverPort, cIsn, 0, TcpFlags.Syn),
            _BuildTcpFrame(server, client, serverPort, clientPort, sIsn, cIsn + 1, TcpFlags.SynAck),
            _BuildTcpFrame(client, server, clientPort, serverPort, cIsn + 1, sIsn + 1, TcpFlags.Ack),
            _BuildTcpFrame(
                client, server, clientPort, serverPort, cIsn + 1, sIsn + 1,
                TcpFlags.Psh | TcpFlags.Ack,
                pdu.AsSpan(0, 8).ToArray()),
            _BuildTcpFrame(
                client, server, clientPort, serverPort, cIsn + 1 + 8, sIsn + 1,
                TcpFlags.Psh | TcpFlags.Ack,
                pdu.AsSpan(8).ToArray()),
        ];

        Packet[] firstParsed = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            Frame frame = _CreateFrame(stack, frames[i], i);
            firstParsed[i] = Packet.ParseFrame(new PacketId(i), stack, frame);
        }

        Packet completing = firstParsed[^1];
        await ProtocolTestHelper.AssertU64Field(stack, completing, "dns.id", 0xDEAD);

        for (int i = 0; i < frames.Length; i++)
        {
            Frame frame = _CreateFrame(stack, frames[i], i);
            Packet reparse = Packet.ParseFrame(new PacketId(i), stack, frame);
            await PacketFieldComparer.AssertFieldIdentical(stack, firstParsed[i], reparse);
        }
    }

    /// <summary>Reads the <c>udp.stream</c> index of a parsed packet.</summary>
    private static ulong _ReadStreamIndex(Stack stack, Packet packet)
    {
        FieldId streamFieldId = stack.GetFieldId("udp.stream")!.Value;
        if (!packet.TryGetFieldValue(streamFieldId, out FieldValue value, materialize: true)
            || !value.Data.TryGetAsU64(out ulong streamIndex))
        {
            return ulong.MaxValue;
        }

        return streamIndex;
    }

    private static byte[] _BuildDnsTcpPdu(ushort txId)
    {
        byte[] pdu = new byte[2 + 21];
        Span<byte> s = pdu;
        BinaryPrimitives.WriteUInt16BigEndian(s, 21);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], txId);
        s[4] = 0x01;
        s[5] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 1);
        s[14] = 1;
        s[15] = (byte)'a';
        s[16] = 1;
        s[17] = (byte)'b';
        s[18] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(s[19..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(s[21..], 1);
        return pdu;
    }

    private static byte[] _BuildTcpFrame(
        IPv4Address srcIp, IPv4Address dstIp,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte flags,
        byte[]? payload = null)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(srcIp, dstIp);
        TcpLayer tcp = new(srcPort, dstPort, seqNum: seqNum, ackNum: ackNum, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues()
            .EmitFrame(payload ?? []);
    }

    private static byte[] _BuildUdpFrame(ushort srcPort = 12345, ushort dstPort = 53)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xAC100164), new IPv4Address(0xAC100101));
        UdpLayer udp = new(srcPort, dstPort);
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static Stack _BuildStack()
    {
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _CreateFrame(Stack stack, byte[] frameData, int index) =>
        Frame.Create(
            new FrameId(index),
            Timestamp.FromSecs(index),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
}
