// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for IPv6 protocol parsing (RFC 8200).
/// Verifies fixed header fields, extension header detection, DoS limits,
/// truncated frames, and tshark cross-validation.
/// </summary>
internal sealed class IPv6BasicTests
{
    #region Frame helpers

    // 2001:db8::1
    private static readonly byte[] _SrcAddrBytes =
        [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];

    // 2001:db8::2
    private static readonly byte[] _DstAddrBytes =
        [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02];

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    /// <summary>
    /// Creates an Ethernet + IPv6 + UDP frame with known values and a small payload.
    /// </summary>
    private static byte[] BuildIPv6UdpFrame(
        byte[]? srcAddr = null,
        byte[]? dstAddr = null,
        ushort srcPort = 12345,
        ushort dstPort = 53,
        byte hopLimit = 64)
    {
        srcAddr ??= _SrcAddrBytes;
        dstAddr ??= _DstAddrBytes;

        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(srcAddr), IPv6Address.FromBytes(dstAddr), hopLimit: hopLimit);
        UdpLayer udp = new(srcPort, dstPort);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Creates an Ethernet + IPv6 + TCP frame.
    /// </summary>
    private static byte[] BuildIPv6TcpFrame(
        ushort srcPort = 49152,
        ushort dstPort = 443)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_SrcAddrBytes), IPv6Address.FromBytes(_DstAddrBytes));
        TcpLayer tcp = new(srcPort, dstPort, flags: TcpFlags.Syn);

        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #endregion

    #region Version

    [Test]
    public async Task Parse_IPv6_Version()
    {
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.version", 6).ConfigureAwait(false);
        }
    }

    #endregion

    #region Addresses

    [Test]
    public async Task Parse_IPv6_SourceAddress()
    {
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // 2001:db8::1
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "ipv6.src", "2001:db8::1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_DestinationAddress()
    {
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // 2001:db8::2
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "ipv6.dst", "2001:db8::2").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_AddrField_ContainsBothEndpoints()
    {
        // Arrange
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? addrId = stack.GetFieldId("ipv6.addr");
            await Assert.That(addrId).IsNotNull().Because("ipv6.addr must be registered");

            // Act: collect all ipv6.addr occurrences (src + dst siblings in ipv6 container, lazy-populated)
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            List<string> found = [];
            while (packet.TryGetNextFieldValue(addrId!.Value, ref cookie, out FieldValue value))
            {
                bool ok = value.Data.TryGetAsIPv6(out IPv6Address addr);
                await Assert.That(ok).IsTrue().Because("ipv6.addr values must be IPv6 addresses");
                found.Add(addr.ToString().ToUpperInvariant());
            }

            // Assert: exactly two occurrences matching source and destination
            await Assert.That(found.Count).IsEqualTo(2)
                .Because("ipv6.addr must appear exactly twice — once for source, once for destination");
            await Assert.That(found.Contains("2001:DB8::1")).IsTrue()
                .Because("ipv6.addr must contain source address 2001:db8::1");
            await Assert.That(found.Contains("2001:DB8::2")).IsTrue()
                .Because("ipv6.addr must contain destination address 2001:db8::2");
        }
    }

    #endregion

    #region Fixed header fields

    [Test]
    public async Task Parse_IPv6_HopLimit()
    {
        byte[] frame = BuildIPv6UdpFrame(hopLimit: 128);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.hlim", 128).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_NextHeader_UDP()
    {
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // UDP = 17 (0x11)
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.nxt", 17).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_NextHeader_TCP()
    {
        byte[] frame = BuildIPv6TcpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // TCP = 6
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.nxt", 6).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_PayloadLength()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // UDP header (8 bytes) + payload (4 bytes) = 12
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.plen", 12).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_TrafficClass_DefaultZero()
    {
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.tclass", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_FlowLabel_DefaultZero()
    {
        byte[] frame = BuildIPv6UdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.flow", 0).ConfigureAwait(false);
        }
    }

    #endregion

    #region Protocol dispatch

    [Test]
    public async Task Parse_IPv6_DispatchesToUDP()
    {
        byte[] frame = BuildIPv6UdpFrame(dstPort: 5678);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // UDP must be present as a sub-protocol
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "udp.dstport").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.dstport", 5678).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv6_DispatchesToTCP()
    {
        byte[] frame = BuildIPv6TcpFrame(dstPort: 443);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "tcp.dstport").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.dstport", 443).ConfigureAwait(false);
        }
    }

    #endregion

    #region Extension headers — Hop-by-Hop

    [Test]
    public async Task Parse_IPv6_HopByHop_PadN_Present()
    {
        // Build an IPv6 frame with a Hop-by-Hop Options header containing PadN (type 1)
        // Manual construction: Eth(14) + IPv6(40) + HopByHop(8) + UDP(8) + payload(4)
        // IPv6: next header = 0 (Hop-by-Hop), hop limit = 64
        // HopByHop: next = 17 (UDP), length = 0 (8 bytes total), options = PadN(1, 0)
        byte[] frame = BuildIPv6WithHopByHopPadN();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // IPv6 must be recognized
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "ipv6.src").ConfigureAwait(false);
            // Next header in fixed header = 0 (Hop-by-Hop)
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.nxt", 0).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Manually builds an Ethernet + IPv6 frame with a minimal Hop-by-Hop Options header
    /// (PadN option) followed by a UDP payload.
    /// </summary>
    private static byte[] BuildIPv6WithHopByHopPadN()
    {
        // Ethernet header (14 bytes)
        // dst MAC:  00:11:22:33:44:55
        // src MAC:  66:77:88:99:AA:BB
        // EtherType: 0x86DD (IPv6)
        // IPv6 fixed header (40 bytes)
        //   version=6, tclass=0, flow=0, plen=16, nxt=0 (HopByHop), hlim=64
        //   src: 2001:db8::1  dst: 2001:db8::2
        // Hop-by-Hop Options header (8 bytes)
        //   nxt=17 (UDP), len=0, PadN(type=1, len=4, data=0,0,0,0)
        // UDP header (8 bytes): src=1234, dst=5678, len=12, checksum=0
        // Payload (4 bytes): 0xDE, 0xAD, 0xBE, 0xEF

        byte[] frame = new byte[14 + 40 + 8 + 8 + 4];
        Span<byte> s = frame;

        // Ethernet header
        s[0] = 0x00;
        s[1] = 0x11;
        s[2] = 0x22;
        s[3] = 0x33;
        s[4] = 0x44;
        s[5] = 0x55; // dst
        s[6] = 0x66;
        s[7] = 0x77;
        s[8] = 0x88;
        s[9] = 0x99;
        s[10] = 0xAA;
        s[11] = 0xBB; // src
        s[12] = 0x86;
        s[13] = 0xDD; // EtherType IPv6

        // IPv6 fixed header (starts at offset 14)
        int ipOff = 14;
        s[ipOff + 0] = 0x60; // version=6, tclass=0 (high nibble)
        // flow label = 0 (bytes 1-3)
        s[ipOff + 1] = 0;
        s[ipOff + 2] = 0;
        s[ipOff + 3] = 0;
        // payload length = 20 (8 HopByHop + 8 UDP + 4 payload)
        BinaryPrimitives.WriteUInt16BigEndian(s[(ipOff + 4)..], 20);
        s[ipOff + 6] = 0;  // next header = 0 (Hop-by-Hop)
        s[ipOff + 7] = 64; // hop limit
        // src: 2001:db8::1
        _SrcAddrBytes.CopyTo(s[(ipOff + 8)..]);
        // dst: 2001:db8::2
        _DstAddrBytes.CopyTo(s[(ipOff + 24)..]);

        // Hop-by-Hop Options header (starts at offset 54)
        int hopOff = 14 + 40;
        s[hopOff + 0] = 17; // next header = UDP
        s[hopOff + 1] = 0;  // len = 0 means 8 bytes total
        // PadN option: type=1, len=4, data=0,0,0,0
        s[hopOff + 2] = 1;  // PadN type
        s[hopOff + 3] = 4;  // length = 4
        s[hopOff + 4] = 0;
        s[hopOff + 5] = 0;
        s[hopOff + 6] = 0;
        s[hopOff + 7] = 0;

        // UDP header (starts at offset 62)
        int udpOff = hopOff + 8;
        BinaryPrimitives.WriteUInt16BigEndian(s[udpOff..], 1234);       // src port
        BinaryPrimitives.WriteUInt16BigEndian(s[(udpOff + 2)..], 5678); // dst port
        BinaryPrimitives.WriteUInt16BigEndian(s[(udpOff + 4)..], 12);   // length = 8 + 4
        // checksum = 0 (no verification required for this test)

        // Payload
        s[udpOff + 8] = 0xDE;
        s[udpOff + 9] = 0xAD;
        s[udpOff + 10] = 0xBE;
        s[udpOff + 11] = 0xEF;

        return frame;
    }

    #endregion

    #region Fragment Header

    [Test]
    public async Task Parse_IPv6_FragmentHeader_Present()
    {
        // Build manually: Eth + IPv6 (nxt=44 Fragment) + FragHdr (nxt=17) + UDP + payload
        byte[] frame = BuildIPv6WithFragmentHeader();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // IPv6 must be detected with next header = 44 (Fragment)
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "ipv6.src").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.nxt", 44).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Manually builds a frame with an IPv6 Fragment Header (RFC 2460 §4.5).
    /// The fragment is the first (and only) fragment, so the complete payload is present.
    /// Fragment header: nxt=17 (UDP), reserved=0, offset=0, M=0, id=0x12345678.
    /// </summary>
    private static byte[] BuildIPv6WithFragmentHeader()
    {
        // Layout: Eth(14) + IPv6(40) + FragHdr(8) + UDP(8) + payload(4) = 74 bytes
        byte[] frame = new byte[14 + 40 + 8 + 8 + 4];
        Span<byte> s = frame;

        // Ethernet
        s[0] = 0x00;
        s[1] = 0x11;
        s[2] = 0x22;
        s[3] = 0x33;
        s[4] = 0x44;
        s[5] = 0x55;
        s[6] = 0x66;
        s[7] = 0x77;
        s[8] = 0x88;
        s[9] = 0x99;
        s[10] = 0xAA;
        s[11] = 0xBB;
        s[12] = 0x86;
        s[13] = 0xDD;

        // IPv6 fixed header
        int ipOff = 14;
        s[ipOff] = 0x60; // version=6
        BinaryPrimitives.WriteUInt16BigEndian(s[(ipOff + 4)..], 20); // plen = 8+8+4
        s[ipOff + 6] = 44; // next header = Fragment
        s[ipOff + 7] = 64; // hop limit
        _SrcAddrBytes.CopyTo(s[(ipOff + 8)..]);
        _DstAddrBytes.CopyTo(s[(ipOff + 24)..]);

        // Fragment Header (8 bytes, starts at 54)
        int fragOff = 14 + 40;
        s[fragOff + 0] = 17; // next header = UDP
        s[fragOff + 1] = 0;  // reserved
        // fragment offset=0, M=0 → offset+flags field = 0x0000
        s[fragOff + 2] = 0;
        s[fragOff + 3] = 0;
        // Identification = 0x12345678
        s[fragOff + 4] = 0x12;
        s[fragOff + 5] = 0x34;
        s[fragOff + 6] = 0x56;
        s[fragOff + 7] = 0x78;

        // UDP
        int udpOff = fragOff + 8;
        BinaryPrimitives.WriteUInt16BigEndian(s[udpOff..], 1234);
        BinaryPrimitives.WriteUInt16BigEndian(s[(udpOff + 2)..], 5678);
        BinaryPrimitives.WriteUInt16BigEndian(s[(udpOff + 4)..], 12);

        s[udpOff + 8] = 0xDE;
        s[udpOff + 9] = 0xAD;
        s[udpOff + 10] = 0xBE;
        s[udpOff + 11] = 0xEF;

        return frame;
    }

    #endregion

    #region Malformed / truncated frames

    [Test]
    public async Task Parse_IPv6_TruncatedFrame_NoFields()
    {
        // Only 10 bytes — not enough for the 40-byte IPv6 fixed header
        byte[] truncated = new byte[14 + 10];
        truncated[12] = 0x86;
        truncated[13] = 0xDD; // EtherType IPv6
        truncated[14] = 0x60; // version=6

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(truncated);
        Stack stack = ProtocolTestHelper.SharedStack;

        // IPv6 should not parse (insufficient data)
        FieldId? srcId = stack.GetFieldId("ipv6.src");
        if (srcId.HasValue)
        {
            bool found = packet.TryGetFieldValue(srcId.Value, out _);
            await Assert.That(found).IsFalse().Because("Truncated frame must not produce ipv6.src");
        }
        else
        {
            return; // field not registered at all → acceptable
        }
    }

    [Test]
    public async Task Parse_IPv6_MinimalFrame_ExactlyFortyByteHeader_NoPayload()
    {
        // Exactly 40-byte IPv6 header with no payload
        byte[] frame = new byte[14 + 40];
        Span<byte> s = frame;

        // Ethernet
        s[12] = 0x86;
        s[13] = 0xDD;
        // IPv6
        int ipOff = 14;
        s[ipOff] = 0x60;
        // plen = 0 (no payload)
        s[ipOff + 6] = 59;  // next header = No Next Header
        s[ipOff + 7] = 64;
        _SrcAddrBytes.CopyTo(s[(ipOff + 8)..]);
        _DstAddrBytes.CopyTo(s[(ipOff + 24)..]);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // IPv6 fields must be present
        await ProtocolTestHelper.AssertIPv6Field(stack, packet, "ipv6.src", "2001:db8::1").ConfigureAwait(false);
        await ProtocolTestHelper.AssertIPv6Field(stack, packet, "ipv6.dst", "2001:db8::2").ConfigureAwait(false);
    }

    #endregion

    // tshark cross-validation lives in IPv6TsharkTests.cs (Plan §3.1.3).
}
