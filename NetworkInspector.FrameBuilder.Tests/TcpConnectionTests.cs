// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Tests for the C2 stage of the TCP-stream API: the bidirectional
/// <see cref="TcpConnection{TOld,TTail}"/> façade composed of two
/// <see cref="TcpStreamLayer"/>-driven sessions, the
/// <see cref="FrameSink"/>-based on-the-fly emission, and the optional
/// <see cref="TcpSegmentMutator"/>.
/// </summary>
/// <remarks>
/// All tests build their carriers (Eth + IPv4) freely with the existing
/// <see cref="FrameStack"/> API and pass them into
/// <see cref="TcpConnection.Open{TOld,TTail}"/>.  No PCAPs, no fixtures.
/// </remarks>
[NotInParallel(nameof(TcpConnectionTests))]
internal sealed class TcpConnectionTests
{
    #region Constants & helpers

    private const int _EthHeaderSize = 14;
    private const int _IPv4HeaderSize = 20;
    private const int _TcpHeaderSize = 20;
    private const int _TcpHeaderOffset = _EthHeaderSize + _IPv4HeaderSize;

    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x02, 0, 0, 0, 0, 0x01]);
    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x02, 0, 0, 0, 0, 0x02]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    /// <summary>Build the client→server carrier (client MAC/IP as source).</summary>
    private static StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> _BuildClientCarrier()
    {
        EthernetLayer eth = new(_ServerMac, _ClientMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        return FrameStack.Start(eth).Then(ip);
    }

    /// <summary>Build the server→client carrier (server MAC/IP as source).</summary>
    private static StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> _BuildServerCarrier()
    {
        EthernetLayer eth = new(_ClientMac, _ServerMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        return FrameStack.Start(eth).Then(ip);
    }

    /// <summary>Reads the TCP source port from a frame whose TCP header is at fixed offset 34.</summary>
    private static ushort _ReadTcpSrcPort(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(_TcpHeaderOffset, 2));

    private static uint _ReadTcpSeq(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(_TcpHeaderOffset + 4, 4));

    private static uint _ReadTcpAck(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(_TcpHeaderOffset + 8, 4));

    private static byte _ReadTcpFlags(ReadOnlySpan<byte> frame)
        => frame[_TcpHeaderOffset + 13];

    private static ushort _ReadTcpWindow(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(_TcpHeaderOffset + 14, 2));

    private static ushort _ReadTcpChecksum(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(_TcpHeaderOffset + 16, 2));

    private static ReadOnlySpan<byte> _TcpPayload(ReadOnlySpan<byte> frame)
        => frame[(_TcpHeaderOffset + _TcpHeaderSize)..];

    /// <summary>Snapshots one wire frame into a heap-allocated byte[] inside a sink.</summary>
    private static (FrameSink Sink, List<byte[]> Frames) _NewFrameCollector()
    {
        List<byte[]> frames = [];
        FrameSink sink = frame => frames.Add(frame.ToArray());
        return (sink, frames);
    }

    private static TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> _OpenDefault(
        TcpConnectionOptions? options = null)
        => TcpConnection.Open(
            _BuildClientCarrier(),
            _BuildServerCarrier(),
            clientPort: 49152,
            serverPort: 80,
            options: options ?? new TcpConnectionOptions(ClientIsn: 1000, ServerIsn: 9000, Mss: 1460, WindowSize: 65535));

    #endregion

    #region Handshake

    [Test]
    public async Task EmitHandshake_Produces_3_Frames_With_Correct_Flags()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();

        conn.EmitHandshake(sink);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.Syn);
        await Assert.That(_ReadTcpFlags(frames[1])).IsEqualTo<byte>(TcpFlags.SynAck);
        await Assert.That(_ReadTcpFlags(frames[2])).IsEqualTo<byte>(TcpFlags.Ack);
    }

    [Test]
    public async Task EmitHandshake_SeqAndAck_Match_RFC793()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault(
            new TcpConnectionOptions(ClientIsn: 1000, ServerIsn: 9000));
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();

        conn.EmitHandshake(sink);

        // SYN: SEQ=1000, ACK=0
        await Assert.That(_ReadTcpSeq(frames[0])).IsEqualTo(1000u);
        await Assert.That(_ReadTcpAck(frames[0])).IsEqualTo(0u);
        // SYN+ACK: SEQ=9000, ACK=1001
        await Assert.That(_ReadTcpSeq(frames[1])).IsEqualTo(9000u);
        await Assert.That(_ReadTcpAck(frames[1])).IsEqualTo(1001u);
        // ACK: SEQ=1001, ACK=9001
        await Assert.That(_ReadTcpSeq(frames[2])).IsEqualTo(1001u);
        await Assert.That(_ReadTcpAck(frames[2])).IsEqualTo(9001u);
    }

    [Test]
    public async Task EmitHandshake_Twice_Throws()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        FrameSink sink = _ => { };
        conn.EmitHandshake(sink);
        await Assert.That(() => conn.EmitHandshake(sink)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Source_Ports_Reflect_Direction()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);

        await Assert.That(_ReadTcpSrcPort(frames[0])).IsEqualTo<ushort>(49152);   // client
        await Assert.That(_ReadTcpSrcPort(frames[1])).IsEqualTo<ushort>(80);      // server
        await Assert.That(_ReadTcpSrcPort(frames[2])).IsEqualTo<ushort>(49152);   // client
    }

    #endregion

    #region Stream writes

    [Test]
    public async Task WriteFromClient_Small_Payload_Produces_Single_PshAck_Segment()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        byte[] payload = "hello"u8.ToArray();
        conn.WriteFromClient(payload, sink);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.PshAck);
        await Assert.That(_TcpPayload(frames[0]).SequenceEqual(payload)).IsTrue();
        await Assert.That(_ReadTcpSeq(frames[0])).IsEqualTo(1001u);
        await Assert.That(_ReadTcpAck(frames[0])).IsEqualTo(9001u);
    }

    [Test]
    public async Task WriteFromClient_Larger_Than_Mss_Splits_Into_Multiple_Segments()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault(
            new TcpConnectionOptions(ClientIsn: 1000, ServerIsn: 9000, Mss: 100));
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        byte[] payload = new byte[250];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        conn.WriteFromClient(payload, sink);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(_TcpPayload(frames[0]).Length).IsEqualTo(100);
        await Assert.That(_TcpPayload(frames[1]).Length).IsEqualTo(100);
        await Assert.That(_TcpPayload(frames[2]).Length).IsEqualTo(50);

        // SEQ progression: 1001, 1101, 1201
        await Assert.That(_ReadTcpSeq(frames[0])).IsEqualTo(1001u);
        await Assert.That(_ReadTcpSeq(frames[1])).IsEqualTo(1101u);
        await Assert.That(_ReadTcpSeq(frames[2])).IsEqualTo(1201u);

        // Only the last segment has PSH set.
        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.Ack);
        await Assert.That(_ReadTcpFlags(frames[1])).IsEqualTo<byte>(TcpFlags.Ack);
        await Assert.That(_ReadTcpFlags(frames[2])).IsEqualTo<byte>(TcpFlags.PshAck);

        // Reassembled bytes match the original payload exactly.
        byte[] reassembled = [.. _TcpPayload(frames[0]).ToArray(), .. _TcpPayload(frames[1]).ToArray(), .. _TcpPayload(frames[2]).ToArray()];
        await Assert.That(reassembled.AsSpan().SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task WriteFromClient_Empty_With_Push_Emits_Single_PshAck()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.WriteFromClient(ReadOnlySpan<byte>.Empty, sink, push: true);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.PshAck);
        await Assert.That(_TcpPayload(frames[0]).Length).IsEqualTo(0);
    }

    [Test]
    public async Task WriteFromClient_Empty_Without_Push_Emits_Nothing()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.WriteFromClient(ReadOnlySpan<byte>.Empty, sink, push: false);

        await Assert.That(frames.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bidirectional_Writes_Update_Each_Other_Ack()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.WriteFromClient("hello"u8, sink);   // 5 bytes
        conn.WriteFromServer("HELLO!"u8, sink);  // 6 bytes

        await Assert.That(frames.Count).IsEqualTo(2);
        // Client segment SEQ=1001, ACK=9001
        await Assert.That(_ReadTcpSeq(frames[0])).IsEqualTo(1001u);
        await Assert.That(_ReadTcpAck(frames[0])).IsEqualTo(9001u);
        // Server segment SEQ=9001, ACK should now be 1006 (1001 + 5 client bytes)
        await Assert.That(_ReadTcpSeq(frames[1])).IsEqualTo(9001u);
        await Assert.That(_ReadTcpAck(frames[1])).IsEqualTo(1006u);
    }

    [Test]
    public async Task Per_Call_Mss_Override_Forces_Smaller_Segments()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.WriteFromClient(new byte[60], sink, mss: 25);

        // 60 / 25 = 3 segments (25 + 25 + 10)
        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(_TcpPayload(frames[0]).Length).IsEqualTo(25);
        await Assert.That(_TcpPayload(frames[1]).Length).IsEqualTo(25);
        await Assert.That(_TcpPayload(frames[2]).Length).IsEqualTo(10);
    }

    #endregion

    #region IStreamProducer overload

    private readonly struct LengthPrefixedProducer(byte[] payload) : IStreamProducer
    {
        public void WriteStream(IBufferWriter<byte> writer)
        {
            Span<byte> hdr = writer.GetSpan(2);
            BinaryPrimitives.WriteUInt16BigEndian(hdr, (ushort)payload.Length);
            writer.Advance(2);
            Span<byte> body = writer.GetSpan(payload.Length);
            payload.CopyTo(body);
            writer.Advance(payload.Length);
        }
    }

    [Test]
    public async Task WriteFromClient_StreamProducer_Wraps_Bytes_In_Tcp_Segments()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault(
            new TcpConnectionOptions(ClientIsn: 1000, ServerIsn: 9000, Mss: 100));
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        byte[] body = new byte[150];
        for (int i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i + 1);
        }
        LengthPrefixedProducer producer = new(body);
        conn.WriteFromClient(producer, sink);

        // Producer wire-form is 2-byte length + 150 body bytes = 152 bytes total.
        // MSS=100 → two segments (100 + 52).
        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(_TcpPayload(frames[0]).Length).IsEqualTo(100);
        await Assert.That(_TcpPayload(frames[1]).Length).IsEqualTo(52);

        byte[] reassembled = [.. _TcpPayload(frames[0]).ToArray(), .. _TcpPayload(frames[1]).ToArray()];
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reassembled)).IsEqualTo<ushort>(150);
        await Assert.That(reassembled.AsSpan(2).SequenceEqual(body)).IsTrue();
    }

    #endregion

    #region Mutator

    [Test]
    public async Task Mutator_Null_Default_Is_NoOp_And_Preserves_Default_Flags()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        await Assert.That(conn.OnSegment).IsNull();

        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        conn.WriteFromClient("ping"u8, sink);

        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.Syn);
        await Assert.That(_ReadTcpFlags(frames[3])).IsEqualTo<byte>(TcpFlags.PshAck);
    }

    [Test]
    public async Task Per_Call_Mutator_Sets_Ecn_Cwr_Flag()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        TcpSegmentMutator mutator = (ref TcpSegmentDescriptor seg, in TcpSegmentContext _)
            => seg.Flags |= TcpFlags.Cwr;
        conn.WriteFromClient("x"u8, sink, mutator: mutator);

        await Assert.That((_ReadTcpFlags(frames[0]) & TcpFlags.Cwr) != 0).IsTrue();
    }

    [Test]
    public async Task Global_Mutator_Sees_Every_Segment_With_Correct_Direction_And_Phase()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();

        List<(TcpDirection Dir, TcpLifecycle Phase, int Idx, int Count)> log = [];
        conn.OnSegment = (ref TcpSegmentDescriptor _, in TcpSegmentContext ctx)
            => log.Add((ctx.Direction, ctx.Phase, ctx.SegmentIndex, ctx.SegmentCount));

        conn.EmitHandshake(sink);
        conn.WriteFromClient(new byte[10], sink);
        conn.EmitFinClose(sink);

        // Handshake (3) + Data (1) + FinClose (4) = 8 mutator invocations.
        await Assert.That(log.Count).IsEqualTo(8);

        // Spot-check phases.
        await Assert.That(log[0].Dir).IsEqualTo(TcpDirection.ClientToServer);
        await Assert.That(log[0].Phase).IsEqualTo(TcpLifecycle.Handshake);
        await Assert.That(log[3].Phase).IsEqualTo(TcpLifecycle.Data);
        await Assert.That(log[4].Phase).IsEqualTo(TcpLifecycle.Fin);
        await Assert.That(log[4].Dir).IsEqualTo(TcpDirection.ClientToServer);
    }

    [Test]
    public async Task Mutator_Override_Sequence_Reflects_In_Wire_And_Ack_Bookkeeping()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.WriteFromClient("a"u8, sink, mutator:
            (ref TcpSegmentDescriptor seg, in TcpSegmentContext _)
                => seg.Sequence = 5000);

        await Assert.That(_ReadTcpSeq(frames[0])).IsEqualTo(5000u);
    }

    /// <summary>
    /// Verifies that when a mutator adds the FIN flag to a data-carrying segment
    /// the connection auto-advances the peer's expected ACK by
    /// <c>payload.Length + 1</c> (payload bytes + the FIN sequence-number credit).
    /// Without the F6 fix this would only advance by payload.Length, leaving the
    /// peer ACK one behind.
    /// </summary>
    [Test]
    public async Task Mutator_FinOnDataSegment_AdvancesPeerAck_ByPayloadPlusOne()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault(
            new TcpConnectionOptions(ClientIsn: 0, ServerIsn: 0, Mss: 1460, WindowSize: 65535));
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();

        conn.EmitHandshake(sink);
        frames.Clear();

        // Mutator adds FIN to every data segment (unusual but legal via the API).
        TcpSegmentMutator addFin = (ref TcpSegmentDescriptor seg, in TcpSegmentContext ctx) =>
        {
            if (ctx.Phase == TcpLifecycle.Data)
            {
                seg.Flags |= TcpFlags.Fin;
            }
        };

        // After handshake: ClientNextSeq = 1 (SYN consumed seq 0 → NextSeq advanced to 1).
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05]; // 5 bytes
        conn.WriteFromClient(payload, sink, mutator: addFin);

        await Assert.That(frames.Count).IsEqualTo(1);

        // Emitted frame must have PSH+ACK+FIN.
        byte emittedFlags = _ReadTcpFlags(frames[0]);
        await Assert.That((emittedFlags & TcpFlags.Psh) != 0).IsTrue();
        await Assert.That((emittedFlags & TcpFlags.Ack) != 0).IsTrue();
        await Assert.That((emittedFlags & TcpFlags.Fin) != 0).IsTrue();
        await Assert.That(_TcpPayload(frames[0]).Length).IsEqualTo(5);

        // ClientNextSeq must have advanced by payload (5) + FIN (1) = 6.
        // Before write: client NextSeq = 1 (post-handshake).  After: 1 + 6 = 7.
        await Assert.That(conn.ClientNextSeq).IsEqualTo(7u);

        // The server's ACK for the next server→client frame must reflect the full
        // sequence space consumed: 5 payload bytes + 1 FIN = 6 → server ACK = 1 + 6 = 7.
        frames.Clear();
        conn.WriteFromServer([0xAA], sink); // 1-byte server→client segment
        await Assert.That(_ReadTcpAck(frames[0])).IsEqualTo(7u);
    }

    #endregion

    #region Teardown / Reset / Window-update

    [Test]
    public async Task EmitFinClose_Produces_4_Correct_Frames()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.EmitFinClose(sink);

        await Assert.That(frames.Count).IsEqualTo(4);
        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.FinAck);   // client FIN
        await Assert.That(_ReadTcpFlags(frames[1])).IsEqualTo<byte>(TcpFlags.Ack);       // server ACK
        await Assert.That(_ReadTcpFlags(frames[2])).IsEqualTo<byte>(TcpFlags.FinAck);   // server FIN
        await Assert.That(_ReadTcpFlags(frames[3])).IsEqualTo<byte>(TcpFlags.Ack);       // client ACK

        // After client FIN (SEQ=1001, consumes 1) → server ACK should be 1002.
        await Assert.That(_ReadTcpAck(frames[1])).IsEqualTo(1002u);
        // Server FIN SEQ=9001 → client final ACK = 9002.
        await Assert.That(_ReadTcpAck(frames[3])).IsEqualTo(9002u);
    }

    [Test]
    public async Task EmitRstFromClient_Emits_Single_RstAck_Frame()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.EmitRstFromClient(sink);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That((_ReadTcpFlags(frames[0]) & TcpFlags.Rst) != 0).IsTrue();
    }

    [Test]
    public async Task EmitWindowUpdateFromServer_Emits_Bare_Ack_With_New_Window()
    {
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        conn.EmitWindowUpdateFromServer(newWindow: 4096, sink);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(_ReadTcpFlags(frames[0])).IsEqualTo<byte>(TcpFlags.Ack);
        await Assert.That(_ReadTcpWindow(frames[0])).IsEqualTo<ushort>(4096);
        await Assert.That(_TcpPayload(frames[0]).Length).IsEqualTo(0);

        // Subsequent server emission reverts to default window.
        frames.Clear();
        conn.WriteFromServer("y"u8, sink);
        await Assert.That(_ReadTcpWindow(frames[0])).IsEqualTo<ushort>(65535);
    }

    #endregion

    #region Multi-frame (fragmented carrier) drain

    /// <summary>
    /// Verifies that <see cref="TcpConnection{TOld,TTail}.WriteFromClient"/> drains ALL
    /// frames produced by a single-call multi-frame sequence, not just the first one.
    /// A low-MTU Ethernet carrier with <c>dontFragment:false</c> IPv4 forces IPv4 to
    /// fragment one TCP segment into multiple wire frames; every fragment must reach the sink.
    /// </summary>
    [Test]
    public async Task WriteFromClient_FragmentingCarrier_EmitsAllFragments()
    {
        // maxFrameSize=68: max IP payload = 68 - 14(Eth) - 20(IP) = 34 bytes.
        // With 8-byte alignment: 32 bytes per non-last IP fragment.
        // TCP segment: 20(TCP hdr) + 40(MSS) = 60 bytes IP payload → 2 fragments.
        const ushort SmallMtu = 68;
        EthernetLayer clientEth = new(_ServerMac, _ClientMac, maxFrameSize: SmallMtu);
        IPv4Layer clientIp = new(_ClientIp, _ServerIp, dontFragment: false);
        StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> clientCarrier =
            FrameStack.Start(clientEth).Then(clientIp);

        EthernetLayer serverEth = new(_ClientMac, _ServerMac, maxFrameSize: SmallMtu);
        IPv4Layer serverIp = new(_ServerIp, _ClientIp, dontFragment: false);
        StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> serverCarrier =
            FrameStack.Start(serverEth).Then(serverIp);

        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn =
            TcpConnection.Open(clientCarrier, serverCarrier, 1234, 80,
                new TcpConnectionOptions(ClientIsn: 0, ServerIsn: 0, Mss: 40, WindowSize: 65535));

        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        // Single TCP segment of exactly MSS bytes; the carrier will fragment it
        // into multiple IPv4 fragments, all of which must reach the sink.
        byte[] payload = new byte[40];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        conn.WriteFromClient(payload, sink);

        // IPv4 fragmentation must produce at least 2 wire frames.
        await Assert.That(frames.Count).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Verifies that <see cref="TcpConnection{TOld,TTail}.WriteFromClient"/> emits
    /// all TCP segments when the payload exceeds MSS (multi-segment write).
    /// </summary>
    [Test]
    public async Task WriteFromClient_MultiSegmentPayload_EmitsAllSegments()
    {
        // MSS of 60 bytes: a 150-byte payload requires exactly 3 TCP segments.
        using TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn =
            TcpConnection.Open(
                _BuildClientCarrier(),
                _BuildServerCarrier(),
                1234,
                80,
                new TcpConnectionOptions(ClientIsn: 0, ServerIsn: 0, Mss: 60, WindowSize: 65535));

        (FrameSink sink, List<byte[]> frames) = _NewFrameCollector();
        conn.EmitHandshake(sink);
        frames.Clear();

        byte[] payload = new byte[150];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        conn.WriteFromClient(payload, sink);

        await Assert.That(frames.Count).IsEqualTo(3);

        // Reassemble TCP payload bytes and verify completeness.
        List<byte> allData = [];
        foreach (byte[] frame in frames)
        {
            int tcpDataOffset = (frame[_TcpHeaderOffset + 12] >> 4) * 4;
            int tcpPayloadStart = _TcpHeaderOffset + tcpDataOffset;
            allData.AddRange(frame.AsSpan(tcpPayloadStart).ToArray());
        }

        await Assert.That(allData.Count).IsEqualTo(payload.Length);
        for (int i = 0; i < payload.Length; i++)
        {
            await Assert.That(allData[i]).IsEqualTo(payload[i]);
        }
    }

    #endregion

    #region Dispose

    [Test]
    public async Task Dispose_Is_Idempotent()
    {
        TcpConnection<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>> conn = _OpenDefault();
        conn.Dispose();
        conn.Dispose();   // must not throw

        FrameSink sink = _ => { };
        await Assert.That(() => conn.EmitHandshake(sink)).Throws<ObjectDisposedException>();
    }

    #endregion
}
