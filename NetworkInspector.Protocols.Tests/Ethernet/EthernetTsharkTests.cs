// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the Ethernet dissector
/// (Network-Inspector ↔ tshark, Plan §3.1.1).
/// </summary>
/// <remarks>
/// <para>
/// All frames are emitted exclusively through the <see cref="FrameStack"/> API
/// (<c>FrameStack.Start(eth).Then(...).CreateWithFixedValues().EmitFrame(...)</c>);
/// no static byte-blob fixtures are loaded from disk. Field comparison goes
/// through <see cref="TsharkAssert.AssertEquivalentMany(Stack, Packet, byte[], (string, string)[])"/>
/// so a drift on either side triggers an immediate test failure with the diff.
/// </para>
/// <para>
/// Coverage per the plan: <c>eth.dst</c>, <c>eth.src</c>, <c>eth.type</c>,
/// <c>eth.padding</c> (short-frame zero-pad path), <c>eth.fcs</c> via
/// <see cref="EthernetFcs"/> trailer.
/// </para>
/// <para>Thread safety: tests are stateless; the shared parser stack is read-only.</para>
/// </remarks>
internal sealed class EthernetTsharkTests
{
    #region Frame builders

    /// <summary>Standard Ethernet+IPv4+UDP frame; large enough to avoid auto-padding.</summary>
    private static byte[] _BuildStandardFrame()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 80);
        byte[] payload = new byte[64]; // > 46 byte minimum payload, no padding required
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>ARP request frame to exercise <c>eth.type</c> = 0x0806.</summary>
    private static byte[] _BuildArpFrame()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        ArpLayer arp = new(
            opcode: 1,
            senderMac: MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]),
            senderIp: new IPv4Address(0xC0A80101),
            targetMac: MacAddress.FromBytes([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            targetIp: new IPv4Address(0xC0A80102));
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Short Ethernet+IPv4+UDP frame manually padded to 60 bytes (the standard
    /// 802.3 minimum minus the 4-byte FCS). tshark exposes the trailing
    /// zero-bytes via the <c>eth.padding</c> field, which is what we verify.
    /// </summary>
    private static byte[] _BuildShortFrameWithPadding()
    {
        // Eth(14) + IPv4(20) + UDP(8) + payload(4) = 46 bytes raw → pad to 60.
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] core = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
        // Pad with zeros up to the 60-byte minimum; tshark renders this as eth.padding.
        const int MinFrameWithoutFcs = 60;
        if (core.Length >= MinFrameWithoutFcs)
        {
            return core;
        }
        byte[] padded = new byte[MinFrameWithoutFcs];
        Buffer.BlockCopy(core, 0, padded, 0, core.Length);
        return padded;
    }

    /// <summary>
    /// Standard frame plus a 4-byte CRC-32 FCS trailer (<see cref="EthernetFcs"/>).
    /// The DLT remains 1 (Ethernet); tshark only exposes <c>eth.fcs</c> when the
    /// pcap-ng interface advertises an FCS length, which the in-memory writer
    /// does not currently do — so this test verifies the round-trip through our
    /// parser only and pins the FCS bytes against an independent CRC.
    /// </summary>
    private static byte[] _BuildFrameWithFcs(out uint expectedCrc)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(53, 53, Auto.Explicit((ushort)0));
        byte[] payload = new byte[64];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0xA0 + i);
        }

        // Full type-plumbing required to add the FCS trailer; mirrors the pattern
        // used in TrailerAndInterceptorSmokeTests.EthernetFcs_AppendsValidCrc32.
        CreatedStack<
            StatelessStack<UdpLayer,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            EthernetFcs,
            NoInterceptor> stack = FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .WithTrailer(EthernetFcs.Crc32)
                .CreateWithFixedValues();

        int total = stack.HeaderSize + payload.Length + EthernetFcs.Size;
        byte[] frame = new byte[total];
        FrameSequence<
            StatelessStack<UdpLayer,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            EthernetFcs,
            NoInterceptor> seq = stack.Build(payload);
        seq.MoveNext(frame, out int written);
        byte[] sized = frame.Length == written ? frame : frame[..written];
        expectedCrc = _ComputeCrc32(sized.AsSpan(0, written - EthernetFcs.Size));
        return sized;
    }

    /// <summary>Reference IEEE 802.3 CRC-32 — independent of the trailer's implementation.</summary>
    private static uint _ComputeCrc32(ReadOnlySpan<byte> data)
    {
        const uint Polynomial = 0xEDB88320u;
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? Polynomial ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }

    #endregion

    #region Symmetric cross-validation

    /// <summary>Verifies dst/src/type for a standard IPv4-carrying frame.</summary>
    [Test]
    public async Task Ethernet_StandardFrame_AllCoreFieldsMatchTshark()
    {
        byte[] frame = _BuildStandardFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("eth.dst", "eth.dst"),
                ("eth.src", "eth.src"),
                ("eth.type", "eth.type")).ConfigureAwait(false);
        }
    }

    /// <summary>Same coverage as above but with <c>eth.type</c> = 0x0806 (ARP).</summary>
    [Test]
    public async Task Ethernet_ArpFrame_EtherTypeMatchesTshark()
    {
        byte[] frame = _BuildArpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("eth.dst", "eth.dst"),
                ("eth.src", "eth.src"),
                ("eth.type", "eth.type")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Frame padded to the 60-byte 802.3 minimum: tshark must surface the
    /// trailing zero-bytes via <c>eth.padding</c> and our parser must produce
    /// the equivalent <c>eth.padding</c> value.
    /// </summary>
    [Test]
    public async Task Ethernet_PaddedShortFrame_PaddingMatchesTshark()
    {
        byte[] frame = _BuildShortFrameWithPadding();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("eth.dst", "eth.dst"),
                ("eth.src", "eth.src"),
                ("eth.type", "eth.type"),
                ("eth.padding", "eth.padding")).ConfigureAwait(false);
        }
    }

    #endregion

    #region FCS trailer round-trip

    /// <summary>
    /// Verifies the <see cref="EthernetFcs"/> trailer round-trip:
    /// (1) the four bytes following the IPv4/UDP payload match an independent
    /// IEEE 802.3 CRC-32 reference, and (2) the core Ethernet fields the
    /// dissector emits remain symmetric with tshark even when the frame
    /// carries an extra four-byte trailer (treated by the dissector as
    /// <c>eth.trailer</c> when no FCS-length hint is present).
    /// </summary>
    [Test]
    public async Task Ethernet_FrameWithFcsTrailer_BytesAndFieldsMatch()
    {
        byte[] frame = _BuildFrameWithFcs(out uint expectedCrc);

        // (1) Pin the trailer bytes against an independent CRC-32 implementation.
        uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            frame.AsSpan(frame.Length - EthernetFcs.Size, EthernetFcs.Size));
        await Assert.That(actualCrc).IsEqualTo(expectedCrc);

        // (2) Standard Ethernet fields remain symmetric with tshark.
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("eth.dst", "eth.dst"),
                ("eth.src", "eth.src"),
                ("eth.type", "eth.type")).ConfigureAwait(false);
        }
    }

    #endregion
}
