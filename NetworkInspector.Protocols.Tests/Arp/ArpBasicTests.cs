// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for ARP protocol parsing (RFC 826): the standard Ethernet/IPv4 form, the gratuitous, announcement and probe
/// rules of RFC 5227, other address sizes and hardware types, the opcode and hardware type tables and the ATM layout
/// of RFC 2225.
/// </summary>
internal sealed class ArpBasicTests
{
    private static readonly MacAddress _SenderMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _ZeroMac = MacAddress.FromBytes([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
    private static readonly MacAddress _OtherMac = MacAddress.FromBytes([0x02, 0x11, 0x22, 0x33, 0x44, 0x66]);
    private static readonly byte[] _SenderMacBytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _ZeroMacBytes = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Creates an Ethernet + ARP Request frame.</summary>
    private static byte[] _BuildArpRequestFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // broadcast
        // ARP Request: opcode 1
        // sender: 00:11:22:33:44:55 / 192.168.1.100
        // target: 00:00:00:00:00:00 / 192.168.1.1
        EthernetLayer eth = new(dstMac, _SenderMac);
        ArpLayer arp = new(1, _SenderMac, new IPv4Address(0xC0A80164), _ZeroMac, new IPv4Address(0xC0A80101));
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Creates an Ethernet + ARP Reply frame.</summary>
    private static byte[] _BuildArpReplyFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        // ARP Reply: opcode 2
        EthernetLayer eth = new(dstMac, srcMac);
        ArpLayer arp = new(2, srcMac, new IPv4Address(0xC0A80101), dstMac, new IPv4Address(0xC0A80164));
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Builds an Ethernet + ARP frame of the standard form with the given opcode, addresses and target hardware address.</summary>
    private static byte[] _BuildFrame(ushort opcode, uint senderIp, uint targetIp, MacAddress targetMac)
    {
        EthernetLayer eth = new(MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]), _SenderMac);
        ArpLayer arp = new(opcode, _SenderMac, new IPv4Address(senderIp), targetMac, new IPv4Address(targetIp));
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds an Ethernet frame (EtherType ARP) around a hand-built ARP message, padded with zeros to
    /// <paramref name="padTo"/> bytes when the frame is shorter.
    /// </summary>
    private static byte[] _RawFrame(byte[] arp, int padTo = 0)
    {
        byte[] frame = new byte[Math.Max(14 + arp.Length, padTo)];
        frame.AsSpan(0, 6).Fill(0xFF);
        _SenderMacBytes.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0806);
        arp.CopyTo(frame.AsSpan(14));
        return frame;
    }

    /// <summary>Builds a plain ARP message with arbitrary hardware type, address sizes and address bytes.</summary>
    private static byte[] _ArpMessage(ushort hwType, ushort protoType, ushort opcode, byte[] senderHw, byte[] senderProto, byte[] targetHw, byte[] targetProto)
    {
        List<byte> message = [];
        message.AddRange([(byte)(hwType >> 8), (byte)hwType, (byte)(protoType >> 8), (byte)protoType]);
        message.Add((byte)senderHw.Length);
        message.Add((byte)senderProto.Length);
        message.AddRange([(byte)(opcode >> 8), (byte)opcode]);
        message.AddRange(senderHw);
        message.AddRange(senderProto);
        message.AddRange(targetHw);
        message.AddRange(targetProto);
        return [.. message];
    }

    #region Standard form

    [Test]
    public async Task ParseArpRequestOpcode()
    {
        byte[] frame = _BuildArpRequestFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.opcode", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParseArpReplyOpcode()
    {
        byte[] frame = _BuildArpReplyFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.opcode", 2).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParseArpRequestEtherType()
    {
        byte[] frame = _BuildArpRequestFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // EtherType 0x0806 = ARP
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0806).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParseArpRequestFixedHeaderAndAddresses()
    {
        byte[] frame = _BuildArpRequestFrame();

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.hw.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.proto.type", 0x0800).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.hw.size", 6).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.proto.size", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "arp.src.hw_mac", "00:11:22:33:44:55").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "arp.src.proto_ipv4", "192.168.1.100").ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "arp.dst.hw_mac", "00:00:00:00:00:00").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "arp.dst.proto_ipv4", "192.168.1.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "arp.hw.type", "Ethernet (1)").ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "arp.opcode", "request (1)").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EthernetPaddingIsNotPartOfTheArpContainer()
    {
        // 28 ARP bytes padded to the 60-byte Ethernet minimum: the container names the 28 bytes of the message.
        byte[] arp = _ArpMessage(1, 0x0800, 1, _SenderMacBytes, [192, 168, 1, 1], _ZeroMacBytes, [192, 168, 1, 2]);
        byte[] frame = _RawFrame(arp, padTo: 60);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? id = stack.GetFieldId("arp");
            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: false);
            await Assert.That(found).IsTrue();
            await Assert.That(value.CustomRepresentation.AsString).IsEqualTo("28 bytes");
        }
    }

    #endregion

    #region Gratuitous, announcement and probe

    [Test]
    [Arguments((ushort)1, 0xC0A80101u, 0xC0A80102u, false, false, false, false, "Who has 192.168.1.2? Tell 192.168.1.1")]
    [Arguments((ushort)1, 0x00000000u, 0xC0A80102u, false, false, false, true, "Who has 192.168.1.2? (ARP Probe)")]
    [Arguments((ushort)1, 0xC0A80101u, 0xC0A80101u, false, true, true, false, "ARP Announcement for 192.168.1.1")]
    [Arguments((ushort)1, 0xC0A80101u, 0xC0A80101u, true, true, false, false, "Gratuitous ARP for 192.168.1.1 (Request)")]
    [Arguments((ushort)2, 0xC0A80101u, 0xC0A80101u, true, true, false, false, "Gratuitous ARP for 192.168.1.1 (Reply)")]
    [Arguments((ushort)2, 0xC0A80101u, 0xC0A80102u, true, false, false, false, "192.168.1.1 is at 00:11:22:33:44:55")]
    [Arguments((ushort)3, 0xC0A80101u, 0xC0A80101u, true, false, false, false, "Who is 02:11:22:33:44:66? Tell 00:11:22:33:44:55")]
    [Arguments((ushort)2, 0x00000000u, 0x00000000u, true, true, false, false, "Gratuitous ARP for 0.0.0.0 (Reply)")]
    public async Task FlagsAndInfoFollowTheAddressConflictRules(
        ushort opcode, uint senderIp, uint targetIp, bool targetHwSet, bool gratuitous, bool announcement, bool probe, string info)
    {
        // The target hardware address is zero for the announcement and probe rows and a real address otherwise; the
        // RARP row (opcode 3) names it in the text, so it is a distinctive address there.
        MacAddress setMac = opcode == 3
            ? _OtherMac
            : _SenderMac;
        MacAddress targetMac = targetHwSet
            ? setMac
            : _ZeroMac;
        byte[] frame = _BuildFrame(opcode, senderIp, targetIp, targetMac);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isgratuitous", gratuitous).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isannouncement", announcement).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isprobe", probe).ConfigureAwait(false);
            await Assert.That(packet.Info).IsEqualTo(info);
        }
    }

    [Test]
    public async Task RarpRequestWithEqualProtocolAddressesIsNotGratuitous()
    {
        // Only requests and replies can be gratuitous: a RARP request with equal addresses must not set the flag.
        byte[] frame = _BuildFrame(3, 0xC0A80101, 0xC0A80101, _OtherMac);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isgratuitous", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isprobe", false).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task GratuitousRequestContainerTitleSaysSo()
    {
        byte[] frame = _BuildFrame(1, 0xC0A80101, 0xC0A80101, _OtherMac);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayTextContains(stack, packet, "arp", "(request/gratuitous ARP)").ConfigureAwait(false);
        }
    }

    #endregion

    #region Other address sizes and hardware types

    [Test]
    public async Task NonStandardSizesUseRawFields()
    {
        // Hardware address 8 bytes, protocol address 16 bytes (for example IPv6): no MAC and no IPv4 value may be invented.
        byte[] hw = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] proto = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
        byte[] frame = _RawFrame(_ArpMessage(1, 0x86DD, 1, hw, proto, new byte[8], proto));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "arp.src.hw_raw", hw).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "arp.src.proto_raw", proto).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "arp.src.hw_mac").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "arp.src.proto_ipv4").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isgratuitous", true).ConfigureAwait(false);
            await Assert.That(packet.Info).StartsWith("Who has 20010db8");
        }
    }

    [Test]
    public async Task ZeroAddressLengthsParseWithEmptyRawFieldsAndNoFlags()
    {
        byte[] frame = _RawFrame(_ArpMessage(1, 0x0800, 1, [], [], [], []));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "arp.src.hw_raw", []).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "arp.dst.proto_raw", []).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isgratuitous", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "arp.isprobe", false).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task SixByteAddressesOfAnotherHardwareTypeAreNotNamedAsMacInTheInfo()
    {
        // Hardware type 3 (AX.25) with a 6-byte address: the typed field is chosen by size, but the summary must not
        // call it a MAC, so it falls back to the hexadecimal form.
        byte[] frame = _RawFrame(_ArpMessage(3, 0x0800, 1, _SenderMacBytes, [192, 168, 1, 1], _ZeroMacBytes, [192, 168, 1, 2]));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Who has c0a80102? Tell c0a80101");
        }
    }

    [Test]
    public async Task MessageShorterThanDeclaredIsRejected()
    {
        // Declares 6 + 4 byte addresses (28 bytes) but carries only 20.
        byte[] arp = _ArpMessage(1, 0x0800, 1, _SenderMacBytes, [192, 168, 1, 1], _ZeroMacBytes, [192, 168, 1, 2])[..20];
        byte[] frame = _RawFrame(arp);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolNotPresent(stack, packet, "arp").ConfigureAwait(false);
        }
    }

    #endregion

    #region Tables

    [Test]
    [Arguments((ushort)0, "reserved (0)")]
    [Arguments((ushort)3, "RARP request (3)")]
    [Arguments((ushort)9, "InARP reply (9)")]
    [Arguments((ushort)10, "ARP NAK (10)")]
    [Arguments((ushort)22, "MARS redirect map (22)")]
    [Arguments((ushort)25, "experimental 2 (25)")]
    [Arguments((ushort)65535, "reserved (65535)")]
    [Arguments((ushort)26, "26")]
    public async Task OpcodeTableNamesTheRegisteredValues(ushort opcode, string expected) =>
        await Assert.That(DisplayTables.GetArpOpcodeDisplayText(opcode)).IsEqualTo(expected);

    [Test]
    [Arguments((ushort)1, "Ethernet (1)")]
    [Arguments((ushort)21, "ATM (21)")]
    [Arguments((ushort)24, "IEEE 1394.1995 (24)")]
    [Arguments((ushort)38, "Unified Bus (38)")]
    [Arguments((ushort)257, "AEthernet (257)")]
    [Arguments((ushort)39, "39")]
    [Arguments((ushort)768, "768")]
    public async Task HardwareTypeTableNamesTheRegisteredValues(ushort hwType, string expected) =>
        await Assert.That(DisplayTables.GetArpHwTypeDisplayText(hwType)).IsEqualTo(expected);

    [Test]
    [Arguments((ushort)7, "DRARP Error")]
    [Arguments((ushort)10, "ARP NAK")]
    [Arguments((ushort)0x1234, "Unknown ARP opcode 0x1234")]
    public async Task OpcodeInfoTextCoversTheSpecialAndTheUnknownOpcodes(ushort opcode, string expected) =>
        await Assert.That(DisplayTables.GetArpOpcodeInfoText(opcode)).IsEqualTo(expected);

    #endregion

    #region ATM

    [Test]
    public async Task AtmArpUsesTheRfc2225Layout()
    {
        // Hardware type 19, request. Sender: E.164 number "1234" (type/length 0x44), no subaddress, IPv4 10.0.0.1.
        // Target: 20-byte NSAP number (type/length 0x14), no subaddress, IPv4 10.0.0.2.
        List<byte> message = [0x00, 0x13, 0x08, 0x00, 0x44, 0x00, 0x00, 0x01, 0x04, 0x14, 0x00, 0x04];
        message.AddRange("1234"u8.ToArray());
        message.AddRange([10, 0, 0, 1]);
        message.AddRange(Enumerable.Range(1, 20).Select(static i => (byte)i));
        message.AddRange([10, 0, 0, 2]);
        byte[] frame = _RawFrame([.. message]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.hw.type", 19).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.src.htype", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.src.hlen", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.src.pln", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.dst.htype", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "arp.dst.hlen", 20).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "arp.src.atm_num_e164", "1234").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "arp.dst.atm_num_nsap", [.. Enumerable.Range(1, 20).Select(static i => (byte)i)]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "arp.src.proto_ipv4", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "arp.dst.proto_ipv4", "10.0.0.2").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "arp.hw.size").ConfigureAwait(false);
            await Assert.That(packet.Info).IsEqualTo("ATMARP request");
        }
    }

    [Test]
    public async Task AtmArpShorterThanItsDeclaredLengthsIsRejected()
    {
        // The header declares a 20-byte target number that is not there.
        List<byte> message = [0x00, 0x13, 0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x14, 0x00, 0x00, 1, 2, 3];
        byte[] frame = _RawFrame([.. message]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolNotPresent(stack, packet, "arp").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task AtmArpShorterThanItsFixedHeaderIsRejected()
    {
        byte[] frame = _RawFrame([0x00, 0x13, 0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolNotPresent(stack, packet, "arp").ConfigureAwait(false);
        }
    }

    #endregion

    #region Cross validation and concurrency

    [Test]
    public async Task TsharkCrossValidationArpOpcode()
    {
        byte[] frame = _BuildArpRequestFrame();
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "arp.opcode");
        if (tsharkValue is null)
        {
            return;
        }

        await Assert.That(tsharkValue).IsEqualTo("1");
    }

    [Test]
    public async Task TsharkCrossValidationSenderProtocolAddress()
    {
        // ARP request sender IP: 192.168.1.100
        byte[] frame = _BuildArpRequestFrame();
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "arp.src.proto_ipv4");
        if (tsharkValue is null)
        {
            return;
        }

        await Assert.That(tsharkValue).IsEqualTo("192.168.1.100");
    }

    [Test]
    public async Task TsharkCrossValidationTargetProtocolAddress()
    {
        // ARP request target IP: 192.168.1.1
        byte[] frame = _BuildArpRequestFrame();
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "arp.dst.proto_ipv4");
        if (tsharkValue is null)
        {
            return;
        }

        await Assert.That(tsharkValue).IsEqualTo("192.168.1.1");
    }

    [Test]
    public async Task ConcurrentParsingYieldsTheSameFieldsAsASingleThread() =>
        await ConcurrentMaterialization.AssertMatchesSingleThreadedResult(
            _BuildArpRequestFrame(),
            _BuildArpReplyFrame(),
            _BuildFrame(1, 0xC0A80101, 0xC0A80101, _ZeroMac),
            _RawFrame(_ArpMessage(1, 0x86DD, 1, [1, 2, 3, 4, 5, 6, 7, 8], new byte[16], new byte[8], new byte[16]))).ConfigureAwait(false);

    [Test]
    public async Task ArpFrameIsRecordedInTheProtocolAndFieldIndex()
    {
        byte[] frame = _BuildArpRequestFrame();

        using Stack stack = ProtocolTestHelper.BuildStack();
        NetworkInspector.Core.Index.PacketIndex index = new(stack);
        Frame parsedFrame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), frame, LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(index: index);
        bool parsed = Packet.TryParse(new PacketId(0), stack, parsedFrame, in options, out Packet? packet, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(packet).IsNotNull();
        ProtocolId? arpId = stack.GetProtocolId("arp");
        await Assert.That(index.GetProtocolBitmap(arpId!.Value).Contains(0)).IsTrue();
        FieldId? opcodeId = stack.GetFieldId("arp.opcode");
        await Assert.That(index.GetFieldBitmap(opcodeId!.Value).Contains(0)).IsTrue();
    }

    #endregion
}
