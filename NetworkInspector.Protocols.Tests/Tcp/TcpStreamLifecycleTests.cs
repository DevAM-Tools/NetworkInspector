// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using Http1MessageDetector = NetworkInspector.Protocols.Http.Http1MessageDetector;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the life of a TCP stream: a reused address/port pair starts a new stream, FIN and RST reach the reassembly
/// engine, protocols found by content are reassembled, segments that only fill a buffer are visible, and a reassembly limit
/// loses one message instead of the connection.
/// </summary>
/// <remarks>
/// Every test builds its own stack, because the tracker and the reassembly engine accumulate state across the packets of one
/// test. Frames come from <see cref="TcpTestConversation"/>, which tracks sequence numbers and cuts payloads into segments.
/// </remarks>
internal sealed class TcpStreamLifecycleTests
{
    #region Helpers

    /// <summary>A DNS-over-TCP query for "a.b.": 2-byte length prefix, header, question (23 bytes in total).</summary>
    private static byte[] _DnsPdu(ushort transactionId)
    {
        byte[] pdu = new byte[2 + 21];
        BinaryPrimitives.WriteUInt16BigEndian(pdu, 21);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), transactionId);
        pdu[4] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6), 1);
        pdu[14] = 1;
        pdu[15] = (byte)'a';
        pdu[16] = 1;
        pdu[17] = (byte)'b';
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(19), 1);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(21), 1);
        return pdu;
    }

    #endregion

    #region Port reuse

    [Test]
    public async Task ReusedPortsAfterFinStartNewStream()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation first = new(serverPort: 40002);
        first.Handshake();
        first.ClientFin();
        first.ServerFin();
        first.ClientAck();
        _ = first.ParseAll(stack);

        TcpTestConversation second = new(serverPort: 40002, clientIsn: 5000, serverIsn: 6000);
        second.Handshake();
        Packet[] packets = second.ParseAll(stack, firstPacketId: first.Frames.Count);

        // The new SYN is flagged, belongs to stream 1, and the relative numbers restart at the new initial sequence number.
        await ProtocolTestHelper.AssertU64Field(stack, packets[0], "tcp.stream", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packets[0], "tcp.analysis.reused_ports").ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[0], "tcp.seq", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[2], "tcp.seq", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, packets[2], "tcp.analysis.connection_state", "ESTABLISHED").ConfigureAwait(false);
    }

    [Test]
    public async Task SynRetransmissionBeforeAnyCloseKeepsTheStream()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 40002);
        conversation.Handshake();
        byte[] repeatedSyn = conversation.ClientSegment(1000, default, TcpFlags.Syn);
        _ = conversation.ParseAll(stack);

        Packet repeat = ProtocolTestHelper.ParseFrame(stack, repeatedSyn, 3, Timestamp.FromMillis(3000));

        await ProtocolTestHelper.AssertU64Field(stack, repeat, "tcp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, repeat, "tcp.analysis.reused_ports").ConfigureAwait(false);
    }

    [Test]
    public async Task SynAckWithNewSequenceNumberAfterResetStartsNewStream()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation first = new(serverPort: 40002);
        first.Handshake();
        first.ServerRst();
        _ = first.ParseAll(stack);

        // A new connection attempt after the reset: the SYN-ACK arrives with another initial sequence number.
        TcpTestConversation second = new(serverPort: 40002, clientIsn: 7000, serverIsn: 8000);
        second.Handshake();
        Packet[] packets = second.ParseAll(stack, firstPacketId: first.Frames.Count);

        await ProtocolTestHelper.AssertU64Field(stack, packets[0], "tcp.stream", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[1], "tcp.stream", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task ReusedPortsDropTheReassemblyStreamOfTheOldConnection()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] pdu = _DnsPdu(0x1111);

        // The old connection leaves half a DNS message in the reassembly buffer and is then replaced without a close.
        TcpTestConversation first = new(serverPort: 53);
        first.Handshake();
        first.ClientSend(pdu.AsSpan(0, 10));
        _ = first.ParseAll(stack);

        TcpTestConversation second = new(serverPort: 53, clientIsn: 5000, serverIsn: 6000);
        second.Handshake();
        second.ClientSend(pdu);
        Packet[] packets = second.ParseAll(stack, firstPacketId: first.Frames.Count);

        // Without the drop, the new connection's bytes would be glued to the stale half message of the old cursor.
        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "dns.id", 0x1111).ConfigureAwait(false);
    }

    #endregion

    #region FIN and RST

    [Test]
    public async Task HttpBodyUntilFinIsDeliveredOnFin()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 80);
        conversation.Handshake();
        conversation.ServerSend("HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\n\r\n"u8);
        conversation.ServerSend("Hello, "u8);
        conversation.ServerSend("world"u8);
        conversation.ServerFin();

        Packet[] packets = conversation.ParseAll(stack);

        // Nothing is shown while the body may still grow; the FIN completes the message.
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[3], "http").ConfigureAwait(false);
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[5], "http").ConfigureAwait(false);
        await ProtocolTestHelper.AssertBytesField(stack, packets[^1], "http.payload", "Hello, world"u8.ToArray()).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "http.response.code", 200).ConfigureAwait(false);
    }

    [Test]
    public async Task FinWithPayloadFeedsThePayloadThenFlushes()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 80);
        conversation.Handshake();
        conversation.ServerSend("HTTP/1.0 200 OK\r\n\r\nHello, "u8);
        conversation.ServerFin("world"u8);

        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertBytesField(stack, packets[^1], "http.payload", "Hello, world"u8.ToArray()).ConfigureAwait(false);
    }

    [Test]
    public async Task RstDropsTheBufferedBytesOfAnUnfinishedMessage()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 80);
        conversation.Handshake();
        conversation.ServerSend("HTTP/1.0 200 OK\r\n\r\npartial"u8);
        conversation.ServerRst();

        Packet[] packets = conversation.ParseAll(stack);

        // An aborted connection never delivered a complete message: nothing is dispatched on the RST.
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[^1], "http").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "packet.error").ConfigureAwait(false);
    }

    [Test]
    public async Task SegmentAfterFinOfTheSameDirectionIsNotReassembled()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 53);
        conversation.Handshake();
        conversation.ClientFin();
        _ = conversation.ParseAll(stack);

        // The closed direction refuses further bytes, so a late segment takes the plain path instead of vanishing.
        byte[] late = conversation.ClientSegment(conversation.ClientSeq, _DnsPdu(0x2222));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, late, 4, Timestamp.FromMillis(40));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }

    [Test]
    public async Task ConnectionWithoutDataDoesNotCreateAReassemblyStreamOnFin()
    {
        // A port scan: SYN, SYN-ACK, RST on a reassembled port. The RST must not allocate a stream; it is observable as a
        // clean packet and a following connection on the same tuple that still reassembles.
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation scan = new(serverPort: 53);
        scan.Handshake();
        scan.ClientRst();
        Packet[] scanPackets = scan.ParseAll(stack);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, scanPackets[^1], "packet.error").ConfigureAwait(false);

        TcpTestConversation real = new(serverPort: 53, clientIsn: 4000, serverIsn: 5000);
        real.Handshake();
        real.ClientSend(_DnsPdu(0x3333));
        Packet[] packets = real.ParseAll(stack, firstPacketId: scan.Frames.Count);

        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "dns.id", 0x3333).ConfigureAwait(false);
    }

    #endregion

    #region Heuristic owner

    [Test]
    public async Task HttpOnPort12345_SplitRequest_OneMessage()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 12345);
        conversation.Handshake();
        conversation.ClientSend("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8, mss: 20);

        Packet[] packets = conversation.ParseAll(stack);

        // The heuristic recognizes the request on its first segment; the rest is joined by the stream, so the message
        // appears once, on the segment that completes it - exactly as on port 80.
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[3], "http").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packets[^1], "http.request.method").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "packet.error").ConfigureAwait(false);
    }

    [Test]
    public async Task TlsClientHelloSplitOnPort12346_YieldsServerNameOnTheCompletingSegment()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] extensions = TlsPayloadBuilder.BuildExtension(
            TlsPayloadBuilder.ExtensionType.ServerName, TlsPayloadBuilder.BuildSniExtensionBody("split.example.com"));
        byte[] hello = TlsPayloadBuilder.BuildClientHelloBody(
            TlsPayloadBuilder.Tls12, new byte[32], new byte[32], [0x1301, 0x1302], [0], extensions);
        byte[] record = TlsPayloadBuilder.BuildRecord(
            TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12,
            TlsPayloadBuilder.BuildHandshakeMessage(TlsPayloadBuilder.HandshakeType.ClientHello, hello));

        TcpTestConversation conversation = new(serverPort: 12346);
        conversation.Handshake();
        conversation.ClientSend(record, mss: 40);

        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[3], "tls").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, packets[^1], "tls.handshake.extensions.server_name", "split.example.com")
            .ConfigureAwait(false);
    }

    #endregion

    #region Stream limit

    [Test]
    public async Task StreamLimitStillDispatchesPayload()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("tcp.max_tracked_streams", SettingValue.U64(1)));
        TcpTestConversation tracked = new(serverPort: 40002);
        tracked.Handshake();
        _ = tracked.ParseAll(stack);

        // The second connection is refused by the table (no stream index), but its HTTP request line needs no connection
        // state and must stay visible: the limit protects memory, it must not hide the application layer.
        TcpTestConversation refused = new(serverPort: 80, clientPort: 50001);
        refused.Handshake();
        refused.ClientSend("GET /x HTTP/1.1\r\nHost: h\r\n\r\n"u8);
        Packet[] packets = refused.ParseAll(stack, firstPacketId: tracked.Frames.Count);

        await ProtocolTestHelper.AssertFieldExists(stack, packets[^1], "tcp.error.stream_limit").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "tcp.stream").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packets[^1], "http.request.method").ConfigureAwait(false);
    }

    #endregion

    #region Segment fields

    [Test]
    public async Task BufferedSegmentShowsSegmentData()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] pdu = _DnsPdu(0x4444);
        TcpTestConversation conversation = new(serverPort: 53);
        conversation.Handshake();
        conversation.ClientSend(pdu, mss: 10);

        Packet[] packets = conversation.ParseAll(stack);

        // The first segment only filled the buffer: its payload is shown as segment data, with the reason in the info text.
        await ProtocolTestHelper.AssertFieldExists(stack, packets[3], "tcp.segment_data").ConfigureAwait(false);
        await Assert.That(packets[3].Info).Contains("[TCP segment of a reassembled PDU]");
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[3], "dns.id").ConfigureAwait(false);
    }

    [Test]
    public async Task CompletingPacketListsContributingSegments()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] pdu = _DnsPdu(0x5555);
        TcpTestConversation conversation = new(serverPort: 53);
        conversation.Handshake();
        conversation.ClientSend(pdu, mss: 10);

        Packet[] packets = conversation.ParseAll(stack);

        // 23 bytes in segments of 10 are three segments (frames 4 to 6, numbered from 1).
        Packet completing = packets[^1];
        await ProtocolTestHelper.AssertU64Field(stack, completing, "tcp.segment.count", 3).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, completing, "tcp.reassembled.length", (ulong)pdu.Length).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, completing, "tcp.segment", 4).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, completing, "dns.id", 0x5555).ConfigureAwait(false);
    }

    [Test]
    public async Task RetransmittedRawSegmentIsNotDispatchedAgain()
    {
        using Stack stack = CountingProbeProtocol.BuildStack(40100, out CountingProbeProtocol probe);
        TcpTestConversation conversation = new(serverPort: 40100);
        conversation.Handshake();
        byte[] payload = [1, 2, 3, 4];
        byte[] original = conversation.ClientSegment(conversation.ClientSeq, payload);
        _ = conversation.ParseAll(stack);

        _ = ProtocolTestHelper.ParseFrame(stack, original, 3, Timestamp.FromMillis(23));
        await Assert.That(probe.ParseCount).IsEqualTo(1);

        // 300 ms later the same bytes arrive again: a retransmission. The application protocol saw them already.
        Packet repeat = ProtocolTestHelper.ParseFrame(stack, original, 4, Timestamp.FromMillis(323));

        await ProtocolTestHelper.AssertBoolField(stack, repeat, "tcp.analysis.retransmission", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, repeat, "tcp.segment_data").ConfigureAwait(false);
        await Assert.That(probe.ParseCount).IsEqualTo(1);
    }

    #endregion

    #region Reassembly limit

    [Test]
    public async Task BufferOverflowDropsTheMessageAndRecovers()
    {
        // A tiny buffer limit makes the overflow reachable with small segments: the first response announces more than the
        // buffer can hold and is cut by the limit, the next response fits. The connection must survive the first one: the
        // loss is reported once, the framing resumes at the next start line, and the later response is delivered whole.
        StreamReassemblyConfig config = new()
        {
            BoundaryDetector = new Http1MessageDetector(),
            MaxPduSize = 1000,
            MaxBufferSize = 400,
        };
        using Stack stack = CountingProbeProtocol.BuildStack(40200, out CountingProbeProtocol probe, config);
        TcpTestConversation conversation = new(serverPort: 40200);
        conversation.Handshake();
        conversation.ServerSend("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\n"u8);
        conversation.ServerSend(new byte[600], mss: 150);
        byte[] next = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();
        conversation.ServerSend(next);

        Packet[] packets = conversation.ParseAll(stack);

        FieldId limitField = stack.GetFieldId("tcp.error.reassembly_limit")!.Value;
        int reports = 0;
        foreach (Packet packet in packets)
        {
            if (packet.TryGetFieldValue(limitField, out _, materialize: true))
            {
                reports++;
            }
        }

        await Assert.That(reports).IsEqualTo(1).Because("the dropped message is reported once, on the segment that hit the limit");
        await Assert.That(probe.Lengths[^1]).IsEqualTo(next.Length).Because("the next response is delivered whole");
    }

    #endregion


    #region Replay

    [Test]
    public async Task ReparseOfAReassembledPduIsIdenticalOnManyThreads()
    {
        const int readers = 4;
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] pdu = _DnsPdu(0x6666);
        TcpTestConversation conversation = new(serverPort: 53);
        conversation.Handshake();
        conversation.ClientSend(pdu, mss: 10);
        Packet[] packets = conversation.ParseAll(stack);
        int lastId = packets.Length - 1;
        PacketFieldComparer.PacketFieldSnapshot reference = PacketFieldComparer.CaptureFields(packets[lastId]);

        await Parallel.ForAsync(0, readers, async (_, _) =>
        {
            Packet reparse = ProtocolTestHelper.ParseFrame(stack, conversation.Frames[lastId], lastId, Timestamp.FromMillis(lastId));
            await PacketFieldComparer.AssertMatchesSnapshot(stack, reference, reparse);
        }).ConfigureAwait(false);
    }

    #endregion

    #region Sequence wrap

    [Test]
    public async Task ReassemblyAcrossTheSequenceWrapSucceeds()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] pdu = _DnsPdu(0x7777);
        TcpTestConversation conversation = new(serverPort: 53, clientIsn: 0xFFFFFF00u);

        // The handshake takes one number; the data starts at 0xFFFFFF01 and the 23 bytes in segments of 10 cross the wrap.
        conversation.Handshake();
        conversation.ClientSend(pdu.AsSpan(0, 10));
        conversation.ClientSend(pdu.AsSpan(10, 13));
        conversation.ClientSend(pdu.AsSpan(0, 0));
        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "dns.id", 0x7777).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "tcp.analysis.retransmission").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "tcp.analysis.lost_segment").ConfigureAwait(false);
    }

    #endregion

    #region Large messages

    [Test]
    public async Task LargeChunkedBodyReassemblesInBoundedTime()
    {
        // 4 MiB in 1460-byte segments is about 2900 segments. A detector that re-walked the whole message per segment would
        // scan gigabytes; with the incremental cursor the work per segment is proportional to the new bytes.
        const int bodyBytes = 4 * 1024 * 1024;
        byte[] chunk = new byte[64 * 1024];
        using MemoryStream body = new();
        for (int written = 0; written < bodyBytes; written += chunk.Length)
        {
            byte[] sizeLine = Encoding.ASCII.GetBytes(chunk.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            body.Write(sizeLine);
            body.Write(chunk);
            body.Write("\r\n"u8);
        }

        body.Write("0\r\n\r\n"u8);

        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = new(serverPort: 80);
        conversation.Handshake();
        conversation.ServerSend("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"u8);
        conversation.ServerSend(body.ToArray());

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        Packet[] packets = conversation.ParseAll(stack);
        watch.Stop();

        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "http.response.code", 200).ConfigureAwait(false);
        await Assert.That(watch.Elapsed.TotalSeconds).IsLessThan(20).Because("linear work per segment");
    }

    #endregion
}
