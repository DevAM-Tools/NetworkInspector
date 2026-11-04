// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that a reassembled datagram or message, a decoded HTTP body, or an unmasked or inflated WebSocket payload which
/// cannot be handed to the next protocol, because the packet already holds the maximum of 255 bound buffers, is reported by
/// name instead of reaching the next protocol as empty data.
/// A probe protocol on a UDP port fills the packet's buffer table and then calls the protocol under test with the datagram
/// carried in the UDP payload.
/// </summary>
internal sealed class BoundBufferLimitTests
{
    private const ushort _ProbePort = 15600;
    private const int _AdditionalBufferLimit = 255;

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    /// <summary>
    /// Binds <paramref name="fillCount"/> one-byte buffers to the packet (all 255 by default) and then calls the protocol
    /// with the given name.
    /// </summary>
    private sealed class BufferFillerProtocol(string targetProtocolName, int fillCount = _AdditionalBufferLimit, int bufferSize = 1) : IProtocol
    {
        public string Name => "buffer.filler";

        public string UiName => "Buffer Filler";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            for (int index = 0; index < fillCount; index++)
            {
                if (!parentField.TryBindBuffer(new byte[bufferSize], out _, out _))
                {
                    return NetworkInspector.Core.Errors.ParseError.Custom(Name, "The probe could not fill the buffer table.");
                }
            }

            ProtocolId? target = context.Stack?.GetProtocolId(targetProtocolName);
            if (target is null)
            {
                return NetworkInspector.Core.Errors.ParseError.Custom(Name, "The target protocol is not registered.");
            }

            return parentField.CallProtocol(target.Value, data, in context);
        }
    }

    private static Stack _BuildStack(string targetProtocolName, params (string Name, SettingValue Value)[] settingOverrides)
    {
        using SettingsManager settings = new();
        foreach ((string name, SettingValue value) in settingOverrides)
        {
            settings.PreloadValue(name, value);
        }

        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterProtocol(new BufferFillerProtocol(targetProtocolName), static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(UdpProtocol.PortTableName, _ProbePort, id);
        });
        return builder.Build();
    }

    /// <summary>
    /// Builds a stack in which the buffer filler (see <see cref="BufferFillerProtocol"/>) runs on the probe port, fills
    /// <paramref name="fillCount"/> buffers, and calls <paramref name="targetProtocolName"/>; the probe protocol is
    /// registered in the HTTP content-type table and the WebSocket payload table, so a nested dispatch is observable.
    /// </summary>
    private static Stack _BuildStackWithProbe(string targetProtocolName, int fillCount, BoundSliceProbeProtocol probe) =>
        BoundSliceProbeProtocol.BuildStack(probe, builder =>
        {
            _ = builder.RegisterProtocol(new BufferFillerProtocol(targetProtocolName, fillCount), static (b, id, _) =>
            {
                b.RegisterParserInU64TableByName(UdpProtocol.PortTableName, _ProbePort, id);
            });
        });

    private static byte[] _UdpFrame(ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        UdpLayer udp = new(40000, _ProbePort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Parses the two fragment datagrams as packets 0 and 1 and returns the second packet.</summary>
    private static Packet _ParseTwoFragments(Stack stack, byte[] firstFragment, byte[] secondFragment)
    {
        _ = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(firstFragment), 0, Timestamp.FromMillis(0));
        return ProtocolTestHelper.ParseFrame(stack, _UdpFrame(secondFragment), 1, Timestamp.FromMillis(1));
    }

    private static byte[] _Ipv4Fragment(ushort flagsAndOffset)
    {
        // 20-byte header, no options, protocol 253 (experimental, no parser), 8 bytes of payload.
        byte[] datagram = new byte[28];
        datagram[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)datagram.Length);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(6), flagsAndOffset);
        datagram[8] = 64;
        datagram[9] = 253;
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(12), 0xC0A80101);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(16), 0xC0A80102);
        return datagram;
    }

    private static byte[] _Ipv6Fragment(ushort offsetAndMoreFlag)
    {
        // 40-byte header (next header 44 = fragment), 8-byte fragment header (next header 253), 8 bytes of payload.
        byte[] datagram = new byte[56];
        datagram[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), 16);
        datagram[6] = 44;
        datagram[7] = 64;
        datagram[8] = 0x20;
        datagram[9] = 0x01;
        datagram[23] = 0x01;
        datagram[24] = 0x20;
        datagram[25] = 0x01;
        datagram[39] = 0x02;
        datagram[40] = 253;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(42), offsetAndMoreFlag);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(44), 0xCAFEBABE);
        return datagram;
    }

    private static byte[] _EthernetFrame(byte[] ipv4Datagram)
    {
        byte[] frame = new byte[14 + ipv4Datagram.Length];
        new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }.CopyTo(frame, 0);
        new byte[] { 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB }.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        ipv4Datagram.CopyTo(frame, 14);
        return frame;
    }

    /// <summary>
    /// Like <see cref="_BuildStack"/>, but the probe binds one buffer of <paramref name="bufferSize"/> bytes before it calls the target,
    /// and the three per-packet totals (HTTP, WebSocket, SOME/IP-TP) are at their 2 KiB minimum, below that buffer.
    /// </summary>
    private static Stack _BuildStackWithBigBoundBuffer(string targetProtocolName, int bufferSize)
    {
        using SettingsManager settings = new();
        foreach (string key in (string[])["http.max_derived_bytes_per_packet", "ws.max_derived_bytes_per_packet", "someip.tp_max_derived_bytes_per_packet"])
        {
            settings.PreloadValue(key, SettingValue.U64(2048));
        }

        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterProtocol(new BufferFillerProtocol(targetProtocolName, fillCount: 1, bufferSize: bufferSize), static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(UdpProtocol.PortTableName, _ProbePort, id);
        });
        return builder.Build();
    }

    /// <summary>
    /// IPv4 reassembly has no total of its own: with 4 KiB already bound to the packet, above every per-protocol total, the
    /// completed datagram is still bound and dispatched, and no error is reported.
    /// </summary>
    [Test]
    public async Task Ipv4ReassemblyIsNotLimitedByTheBytesAlreadyBound()
    {
        using Stack stack = _BuildStackWithBigBoundBuffer("ip", 4096);

        Packet packet = _ParseTwoFragments(stack, _Ipv4Fragment(0x2000), _Ipv4Fragment(0x0001));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        await Assert.That(packet.BufferCount).IsGreaterThanOrEqualTo(3);
    }

    /// <summary>IPv6 reassembly has no total of its own either (see <see cref="Ipv4ReassemblyIsNotLimitedByTheBytesAlreadyBound"/>).</summary>
    [Test]
    public async Task Ipv6ReassemblyIsNotLimitedByTheBytesAlreadyBound()
    {
        using Stack stack = _BuildStackWithBigBoundBuffer("ipv6", 4096);

        Packet packet = _ParseTwoFragments(stack, _Ipv6Fragment(0x0001), _Ipv6Fragment(0x0008));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        await Assert.That(packet.BufferCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Ipv4DatagramReassembledWithAFullBufferTableIsReported()
    {
        using Stack stack = _BuildStack("ip");

        Packet packet = _ParseTwoFragments(stack, _Ipv4Fragment(0x2000), _Ipv4Fragment(0x0001));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }

    [Test]
    public async Task Ipv6DatagramReassembledWithAFullBufferTableIsReported()
    {
        using Stack stack = _BuildStack("ipv6");

        // Fragment offsets count 8-byte units in bits 15..3; bit 0 is the More Fragments flag.
        Packet packet = _ParseTwoFragments(stack, _Ipv6Fragment(0x0001), _Ipv6Fragment(0x0008));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }

    [Test]
    public async Task SomeIpTpMessageReassembledWithAFullBufferTableIsReported()
    {
        // The probe calls SOME/IP directly on its own port, so the datagram is accepted through content-based detection.
        using Stack stack = _BuildStack("someip", ("someip.heuristic", SettingValue.Bool(true)));

        // A single last segment at offset 0 completes the message at once.
        byte[] message = SomeIpFrames.TpMessage(0, new byte[16], more: false, messageType: 0x20);
        byte[] frame = _UdpFrame(message);

        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
    }

    #region HTTP decoded body and WebSocket derived payload

    /// <summary>The decoded body of <see cref="_ChunkedHttpResponse"/>.</summary>
    private static readonly byte[] _HttpDecodedBody = Encoding.ASCII.GetBytes("hello");

    /// <summary>A chunked response whose content type selects the probe; its decoded body is <see cref="_HttpDecodedBody"/>.</summary>
    private static byte[] _ChunkedHttpResponse() => Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\nContent-Type: " + BoundSliceProbeProtocol.ContentType + "\r\nTransfer-Encoding: chunked\r\n\r\n"
        + "5\r\nhello\r\n0\r\n\r\n");

    /// <summary>Encodes one masked, unfragmented text frame with <see cref="WebSocketLayer"/>.</summary>
    private static byte[] _MaskedTextFrame(string text, uint maskingKey)
    {
        WebSocketLayer layer = new(Encoding.UTF8.GetBytes(text), WebSocketOpcode.Text, new WebSocketFrameOptions(Fin: true, MaskingKey: maskingKey));
        ArrayBufferWriter<byte> writer = new(initialCapacity: 64);
        layer.WriteStream(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// With 255 filled buffers a chunked body cannot be bound: the packet shows the HTTP limit message that names 255, the
    /// table stays at its limit, and the nested protocol is not called.
    /// </summary>
    [Test]
    public async Task HttpBodyDecodedWithAFullBufferTableIsReportedAndNotDispatched()
    {
        BoundSliceProbeProtocol probe = new();
        using Stack stack = _BuildStackWithProbe("http", _AdditionalBufferLimit, probe);

        Packet packet = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(_ChunkedHttpResponse()), 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "Decoded HTTP body").ConfigureAwait(false);
        await Assert.That(packet.BufferCount).IsEqualTo(1 + _AdditionalBufferLimit).Because("nothing is added to a full table");
        await Assert.That(probe.Observations.Count).IsEqualTo(0).Because("the nested protocol is not called");
    }

    /// <summary>
    /// Boundary: with 254 filled buffers the decoded body becomes the 255th additional buffer and the nested protocol gets it.
    /// </summary>
    [Test]
    public async Task HttpBodyDecodedIntoTheLastFreeBufferSlotIsParsed()
    {
        // Control and boundary: with one slot left the decoded body becomes the 255th additional buffer.
        BoundSliceProbeProtocol probe = new();
        using Stack stack = _BuildStackWithProbe("http", _AdditionalBufferLimit - 1, probe);

        Packet packet = ProtocolTestHelper.ParseFrame(stack, _UdpFrame(_ChunkedHttpResponse()), 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        await Assert.That(packet.BufferCount).IsEqualTo(1 + _AdditionalBufferLimit);
        await Assert.That(packet.Buffer(_AdditionalBufferLimit)!.Value.Span.SequenceEqual(_HttpDecodedBody)).IsTrue();

        BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
        await Assert.That(seen.IsPacketSlice).IsTrue();
        await Assert.That(seen.BufferIndex).IsEqualTo(_AdditionalBufferLimit);
    }

    /// <summary>
    /// With 255 filled buffers a masked payload cannot be bound: the packet shows the WebSocket limit message that names 255,
    /// the table stays at its limit, and the nested protocol is not called.
    /// </summary>
    [Test]
    public async Task WebSocketPayloadUnmaskedWithAFullBufferTableIsReportedAndNotDispatched()
    {
        BoundSliceProbeProtocol probe = new();
        using Stack stack = _BuildStackWithProbe("websocket", _AdditionalBufferLimit, probe);

        Packet packet = ProtocolTestHelper.ParseFrame(
            stack, _UdpFrame(_MaskedTextFrame("masked payload", 0xDEADBEEF)), 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "WebSocket payload").ConfigureAwait(false);
        await Assert.That(packet.BufferCount).IsEqualTo(1 + _AdditionalBufferLimit);
        await Assert.That(probe.Observations.Count).IsEqualTo(0).Because("the nested protocol is not called");
    }

    /// <summary>
    /// Boundary: with 254 filled buffers the unmasked payload becomes the 255th additional buffer and the nested protocol gets
    /// it.
    /// </summary>
    [Test]
    public async Task WebSocketPayloadUnmaskedIntoTheLastFreeBufferSlotIsParsed()
    {
        BoundSliceProbeProtocol probe = new();
        using Stack stack = _BuildStackWithProbe("websocket", _AdditionalBufferLimit - 1, probe);

        Packet packet = ProtocolTestHelper.ParseFrame(
            stack, _UdpFrame(_MaskedTextFrame("masked payload", 0xDEADBEEF)), 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        await Assert.That(packet.BufferCount).IsEqualTo(1 + _AdditionalBufferLimit);
        await Assert.That(packet.Buffer(_AdditionalBufferLimit)!.Value.Span.SequenceEqual("masked payload"u8)).IsTrue();
        await Assert.That(probe.Observations.Single().BufferIndex).IsEqualTo(_AdditionalBufferLimit);
    }

    /// <summary>
    /// The limit error of the first frame ends the dispatch, not the frame walk: the index groups of the frames behind it are
    /// still recorded, because the presence index must name every field the lazy populator will emit. The second frame is a
    /// ping, whose group <c>websocket.ping</c> only that frame can record.
    /// </summary>
    [Test]
    public async Task WebSocketFrameWalkContinuesAfterTheLimitErrorAndRecordsLaterGroups()
    {
        BoundSliceProbeProtocol probe = new();
        using Stack stack = _BuildStackWithProbe("websocket", _AdditionalBufferLimit, probe);
        byte[] ping = [0x89, 0x02, (byte)'o', (byte)'k'];
        byte[] payload = [.. _MaskedTextFrame("masked payload", 0xDEADBEEF), .. ping];
        NetworkInspector.Core.Index.PacketIndex index = new(stack);

        Frame frame = Frame.Create(
            0, Timestamp.FromMillis(0), _UdpFrame(payload), LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(index: index);
        bool parsed = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure);

        await Assert.That(parsed).IsTrue().Because(failure.ToString());
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet!, "packet.error", "255").ConfigureAwait(false);
        IndexGroupId pingGroup = stack.GetFieldIndexGroup(stack.GetFieldId("websocket.payload.ping")!.Value);
        await Assert.That(pingGroup.IsValid).IsTrue();
        await Assert.That(index.GetGroupBitmap(pingGroup).Contains(0)).IsTrue()
            .Because("the frame behind the failed frame is still walked and its group recorded");
    }

    #endregion

    [Test]
    public async Task Ipv4DatagramReassembledWithRoomInTheBufferTableIsNotAnError()
    {
        // Control: the same fragments without the filler are reassembled normally.
        using Stack stack = ProtocolTestHelper.BuildStack();

        _ = ProtocolTestHelper.ParseFrame(stack, _EthernetFrame(_Ipv4Fragment(0x2000)), 0, Timestamp.FromMillis(0));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, _EthernetFrame(_Ipv4Fragment(0x0001)), 1, Timestamp.FromMillis(1));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }
}
