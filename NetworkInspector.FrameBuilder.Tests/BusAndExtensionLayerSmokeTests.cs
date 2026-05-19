// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for bus and IPv6 extension layers built on top of the
/// <see cref="NetworkInspector.FrameBuilder.Frames.FrameStack"/> API:
/// <c>SocketCanLayer</c>, <c>SocketCanFdLayer</c>, <c>SocketCanXlLayer</c>,
/// <c>SomeIpLayer</c>, <c>IPv6HopByHopLayer</c>, <c>IPv6RoutingLayer</c>,
/// <c>IPv6DestinationOptionsLayer</c>.
/// </summary>
internal sealed class BusAndExtensionLayerSmokeTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

    private static readonly IPv6Address _SrcIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    private static readonly IPv6Address _DstIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);

    #region SocketCAN

    [Test]
    public async Task SocketCan_Classic_BuildsFixedSixteenByteFrame()
    {
        FB.SocketCanLayer can = new(canId: 0x123, data: [0x01, 0x02, 0x03, 0x04]);

        FB.CreatedStack<FB.StatelessStack<FB.SocketCanLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(can).CreateWithFixedValues();

        byte[] frame = new byte[16];
        int written = EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        await Assert.That(written).IsEqualTo(16);

        // CanId = 0x00000123 (no flags, standard 11-bit ID).
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4))).IsEqualTo(0x123u);
        // DLC = 4.
        await Assert.That(frame[4]).IsEqualTo((byte)4);
        // Padding/reserved = 0.
        await Assert.That(frame[5]).IsEqualTo((byte)0);
        await Assert.That(frame[6]).IsEqualTo((byte)0);
        await Assert.That(frame[7]).IsEqualTo((byte)0);
        // Data area: first 4 bytes set, last 4 zero-padded.
        await Assert.That(frame[8]).IsEqualTo((byte)0x01);
        await Assert.That(frame[9]).IsEqualTo((byte)0x02);
        await Assert.That(frame[10]).IsEqualTo((byte)0x03);
        await Assert.That(frame[11]).IsEqualTo((byte)0x04);
        await Assert.That(frame[12]).IsEqualTo((byte)0);
        await Assert.That(frame[15]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task SocketCan_Extended_SetsEffFlag()
    {
        FB.SocketCanLayer can = new(canId: 0x1ABCDEF, extended: true);

        FB.CreatedStack<FB.StatelessStack<FB.SocketCanLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(can).CreateWithFixedValues();

        byte[] frame = new byte[16];
        EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        uint canIdField = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4));
        await Assert.That((canIdField & 0x80000000u) != 0).IsTrue();        // EFF set
        await Assert.That(canIdField & 0x1FFFFFFFu).IsEqualTo(0x1ABCDEFu);   // 29-bit ID
    }

    [Test]
    public async Task SocketCanFd_BuildsSeventyTwoByteFrame_WithFdfFlag()
    {
        byte[] data = new byte[20];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        FB.SocketCanFdLayer canfd = new(canId: 0x456, data: data, brs: true);

        FB.CreatedStack<FB.StatelessStack<FB.SocketCanFdLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(canfd).CreateWithFixedValues();

        byte[] frame = new byte[FB.SocketCanFdLayer.FrameSize];
        int written = EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        await Assert.That(written).IsEqualTo(FB.SocketCanFdLayer.FrameSize);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4))).IsEqualTo(0x456u);
        await Assert.That(frame[4]).IsEqualTo((byte)20);                                  // length
        await Assert.That((frame[5] & FB.SocketCanFdLayer.FdfFlag) != 0).IsTrue();        // FDF
        await Assert.That((frame[5] & FB.SocketCanFdLayer.BrsFlag) != 0).IsTrue();        // BRS
        // Data round-trip: bytes 0..19 of the data area must equal our input.
        for (int i = 0; i < data.Length; i++)
        {
            await Assert.That(frame[8 + i]).IsEqualTo(data[i]);
        }
    }

    [Test]
    public async Task SocketCanXl_BuildsVariableLengthFrame()
    {
        byte[] data = new byte[100];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        FB.SocketCanXlLayer canxl = new(
            priority: 0x12345678,
            data: data,
            sdt: 0x03,
            af: 0xCAFEBABE);

        FB.CreatedStack<FB.StatelessStack<FB.SocketCanXlLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(canxl).CreateWithFixedValues();

        int totalSize = FB.SocketCanXlLayer.FrameSize; // fixed 2060 bytes
        byte[] frame = new byte[totalSize];
        int written = EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        await Assert.That(written).IsEqualTo(totalSize);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(0, 4))).IsEqualTo(0x12345678u);
        await Assert.That((frame[4] & FB.SocketCanXlLayer.XlfFlag) != 0).IsTrue();   // XLF
        await Assert.That(frame[5]).IsEqualTo((byte)0x03);                           // SDT
        // Len field stores actual data length in LE.
        await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2))).IsEqualTo((ushort)data.Length);
        await Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4))).IsEqualTo(0xCAFEBABEu);
        // First and last data byte round-trip.
        await Assert.That(frame[FB.SocketCanXlLayer.HeaderBytes]).IsEqualTo((byte)0);
        await Assert.That(frame[FB.SocketCanXlLayer.HeaderBytes + 99]).IsEqualTo((byte)99);
    }

    #endregion

    #region FlexRay

    [Test]
    public async Task FlexRay_ChannelA_BuildsCorrectFrame()
    {
        // frameId=42 (0x2A), cycleCount=3, 4-byte payload, channel A.
        // Total frame: 7 bytes header + 4 bytes payload = 11 bytes.
        FB.FlexRayLayer flexray = new(frameId: 42, cycleCount: 3, payload: new byte[] { 0x01, 0x02, 0x03, 0x04 }.AsMemory());

        FB.CreatedStack<FB.StatelessStack<FB.FlexRayLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(flexray).CreateWithFixedValues();

        byte[] frame = new byte[11];
        int written = EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        await Assert.That(written).IsEqualTo(11);

        // Byte 0: measurementHeader = channel A (bit 7 = 0) | typeIndex 1 (bits 6-0) = 0x01.
        await Assert.That(frame[0]).IsEqualTo((byte)0x01);
        // Byte 1: errorFlags = 0.
        await Assert.That(frame[1]).IsEqualTo((byte)0x00);
        // Byte 2: NFI=1 (not null) → 0x20, FID[10:8] = (42 >> 8) = 0 → 0x20.
        await Assert.That(frame[2]).IsEqualTo((byte)0x20);
        // Byte 3: FID[7:0] = 42 = 0x2A.
        await Assert.That(frame[3]).IsEqualTo((byte)0x2A);
        // Byte 4: payloadWords (7 bits, [7:1]) = 2 → 0x04; HCRC[10] = 0.
        await Assert.That(frame[4]).IsEqualTo((byte)0x04);
        // Byte 5: HCRC[9:2] = 0.
        await Assert.That(frame[5]).IsEqualTo((byte)0x00);
        // Byte 6: HCRC[1:0]=0, cycleCount=3 → 0x03.
        await Assert.That(frame[6]).IsEqualTo((byte)0x03);
        // Bytes 7-10: payload data.
        await Assert.That(frame[7]).IsEqualTo((byte)0x01);
        await Assert.That(frame[8]).IsEqualTo((byte)0x02);
        await Assert.That(frame[9]).IsEqualTo((byte)0x03);
        await Assert.That(frame[10]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task FlexRay_ChannelB_SetsMeasurementHeaderChannelBit()
    {
        FB.FlexRayLayer flexray = new(frameId: 10, cycleCount: 0, payload: new byte[] { 0xFF }.AsMemory(), channelB: true);

        FB.CreatedStack<FB.StatelessStack<FB.FlexRayLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(flexray).CreateWithFixedValues();

        // frameId=10, 1-byte payload rounded up to 2 (even) → total = 7 + 2 = 9 bytes.
        byte[] frame = new byte[9];
        EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        // Byte 0: channel B (bit 7 = 1) | typeIndex 1 = 0x81.
        await Assert.That(frame[0]).IsEqualTo((byte)0x81);
    }

    #endregion

    #region LIN

    [Test]
    public async Task Lin_BuildsCorrectFrameWithPayload()
    {
        // frameId=0x10, 2-byte payload, enhanced checksum (type 2).
        // Total frame: 8-byte header + 2 bytes payload = 10 bytes.
        FB.LinLayer lin = new(frameId: 0x10, data: new byte[] { 0xAA, 0xBB });

        FB.CreatedStack<FB.StatelessStack<FB.LinLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(lin).CreateWithFixedValues();

        byte[] frame = new byte[10];
        int written = EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);

        await Assert.That(written).IsEqualTo(10);

        // Byte 0: msg_format_rev = 1.
        await Assert.That(frame[0]).IsEqualTo((byte)1);
        // Bytes 1-3: reserved = 0.
        await Assert.That(frame[1]).IsEqualTo((byte)0);
        await Assert.That(frame[2]).IsEqualTo((byte)0);
        await Assert.That(frame[3]).IsEqualTo((byte)0);
        // Byte 4: (payloadLength=2 << 4) | (msgType=0 << 2) | checksumType=2 = 0x22.
        await Assert.That(frame[4]).IsEqualTo((byte)0x22);
        // Byte 5: PID — parity for 0x10: P0 = ID0^ID1^ID2^ID4 = 0^0^0^1 = 1; P1 = !(ID1^ID3^ID4^ID5) = !(0^0^1^0) = 0.
        // PID = 0x10 | (1 << 6) | (0 << 7) = 0x50.
        await Assert.That(frame[5]).IsEqualTo((byte)0x50);
        // Bytes 8-9: payload data.
        await Assert.That(frame[8]).IsEqualTo((byte)0xAA);
        await Assert.That(frame[9]).IsEqualTo((byte)0xBB);
    }

    #endregion

    #region SOME/IP

    [Test]
    public async Task SomeIp_OverUdpIPv4_PatchesLengthField()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        FB.UdpLayer udp = new(srcPort: 30491, dstPort: 30491);
        FB.SomeIpLayer someip = new(
            serviceId: 0x1234,
            methodId: 0x5678,
            clientId: 0x0001,
            sessionId: 0x0042,
            messageType: SomeIpMessageType.Request);

        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];

        FB.CreatedStack<
            FB.StatelessStack<FB.SomeIpLayer,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .Then(someip)
                .CreateWithFixedValues();

        const int FrameSize = 14 + 20 + 8 + 16 + 4;
        byte[] frame = new byte[FrameSize];
        int written = EmitOnce(in stack, payload, frame);

        await Assert.That(written).IsEqualTo(FrameSize);

        // SOME/IP header starts at offset 14+20+8 = 42.
        const int SomeIpOffset = 42;
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(SomeIpOffset, 2))).IsEqualTo((ushort)0x1234);   // ServiceId
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(SomeIpOffset + 2, 2))).IsEqualTo((ushort)0x5678); // MethodId
        // Length field: counts ClientId..end-of-payload = 8 + 4 = 12.
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(SomeIpOffset + 4, 4))).IsEqualTo(12u);
        await Assert.That(frame[SomeIpOffset + 12]).IsEqualTo((byte)1);                                   // ProtocolVersion
        await Assert.That(frame[SomeIpOffset + 14]).IsEqualTo(SomeIpMessageType.Request);                 // MessageType
    }

    #endregion

    #region IPv6 Extension Headers

    [Test]
    public async Task IPv6_HopByHop_PatchesIPv6NextHeader_AndForwardsTransportProtocol()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv6Layer ip = new(_SrcIp6, _DstIp6);
        FB.IPv6HopByHopLayer hbh = new();
        FB.UdpLayer udp = new(53, 53);

        byte[] payload = [0xDE, 0xAD];

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv6HopByHopLayer,
                    FB.StatelessStack<FB.IPv6Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(hbh)
                .Then(udp)
                .CreateWithFixedValues();

        const int FrameSize = 14 + 40 + 8 + 8 + 2;
        byte[] frame = new byte[FrameSize];
        int written = EmitOnce(in stack, payload, frame);

        await Assert.That(written).IsEqualTo(FrameSize);

        // IPv6 NextHeader (offset 14+6 = 20) must point to HopByHop (0).
        await Assert.That(frame[20]).IsEqualTo(IpProtocols.IPv6HopByHop);
        // HopByHop NextHeader (offset 14+40 = 54) must point to UDP (17).
        await Assert.That(frame[54]).IsEqualTo(IpProtocols.Udp);
        // IPv6 PayloadLength (offset 14+4 = 18) covers HopByHop + UDP + payload = 8 + 8 + 2.
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(18, 2))).IsEqualTo((ushort)18);
    }

    [Test]
    public async Task IPv6_ExtensionChain_HopByHop_Routing_DestOpts_ChainsCorrectly()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv6Layer ip = new(_SrcIp6, _DstIp6);
        FB.IPv6HopByHopLayer hbh = new();
        FB.IPv6RoutingLayer rt = new();
        FB.IPv6DestinationOptionsLayer dest = new();
        FB.UdpLayer udp = new(53, 53);

        byte[] payload = [0xAB];

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv6DestinationOptionsLayer,
                    FB.StatelessStack<FB.IPv6RoutingLayer,
                        FB.StatelessStack<FB.IPv6HopByHopLayer,
                            FB.StatelessStack<FB.IPv6Layer,
                                FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(hbh)
                .Then(rt)
                .Then(dest)
                .Then(udp)
                .CreateWithFixedValues();

        const int EthSize = 14;
        const int IPv6Size = 40;
        const int ExtSize = 8;
        const int UdpSize = 8;
        int total = EthSize + IPv6Size + ExtSize * 3 + UdpSize + payload.Length;
        byte[] frame = new byte[total];
        int written = EmitOnce(in stack, payload, frame);
        await Assert.That(written).IsEqualTo(total);

        // Walk the next-header chain: IPv6 → HopByHop → Routing → DestOpts → UDP.
        await Assert.That(frame[EthSize + 6]).IsEqualTo(IpProtocols.IPv6HopByHop);
        await Assert.That(frame[EthSize + IPv6Size]).IsEqualTo(IpProtocols.IPv6Routing);
        await Assert.That(frame[EthSize + IPv6Size + ExtSize]).IsEqualTo(IpProtocols.IPv6DestinationOptions);
        await Assert.That(frame[EthSize + IPv6Size + ExtSize * 2]).IsEqualTo(IpProtocols.Udp);
    }

    #endregion

    #region Helpers

    /// <summary>Sync helper that emits a single frame and returns its byte length.</summary>
    private static int EmitOnce<TStack, TTrailer, TInterceptor>(
        in FB.CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload,
        Span<byte> dst)
        where TStack : struct, FB.IStackNode, FB.IStatelessStack
        where TTrailer : struct, FB.ITrailerLayer
        where TInterceptor : struct, FB.IFrameInterceptor
    {
        FB.FrameSequence<TStack, TTrailer, TInterceptor> seq = created.Build(payload);
        seq.MoveNext(dst, out int written);
        return written;
    }

    #endregion
}
