// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Edge case tests for Ethernet protocol parsing.
/// Covers I/G and L/G bits, IEEE 802.3 vs Ethernet II boundary,
/// padding detection, trailer handling, broadcast/multicast addresses,
/// and minimum-size frames.
/// </summary>
internal sealed class EthernetEdgeCaseTests
{
    #region Helper Methods

    /// <summary>Builds a standard Ethernet+IPv4+UDP frame with specific MAC addresses.</summary>
    private static byte[] BuildEthFrame(MacAddress dst, MacAddress src) =>
        BuildEthFrameWithPayload(dst, src, [0x01, 0x02, 0x03, 0x04]);

    /// <summary>Builds a standard Ethernet+IPv4+UDP frame with specific MACs and payload.</summary>
    private static byte[] BuildEthFrameWithPayload(MacAddress dst, MacAddress src, byte[] payload)
    {
        EthernetLayer eth = new(dst, src);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 80);

        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds a raw Ethernet header with a custom type/length field and payload.</summary>
    private static byte[] BuildRawEthFrame(MacAddress dst, MacAddress src, ushort typeOrLen, byte[] payload)
    {
        // 6 bytes dst + 6 bytes src + 2 bytes type/len + payload
        byte[] frame = new byte[14 + payload.Length];
        Span<byte> span = frame.AsSpan();

        // Write DST MAC (6 bytes)
        dst.ToBytes(span[..6]);

        // Write SRC MAC (6 bytes)
        src.ToBytes(span[6..12]);

        // Write type/length (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], typeOrLen);

        // Copy payload
        payload.AsSpan().CopyTo(span[14..]);
        return frame;
    }

    #endregion

    #region MAC Address Bits (I/G + L/G) CustomText Tests

    // The ig/lg flags are no longer materialised as separate bool fields (eth.dst.ig,
    // eth.dst.lg, eth.src.ig, eth.src.lg). They are emitted as combined CustomText on
    // the eth.dst / eth.src fields by EthernetProtocol.FormatMacAddressBits:
    //   ig = address.IsMulticast ? "Multicast" : "Unicast"
    //   lg = address.IsLocal     ? "Locally Administered" : "Globally Unique"
    //   format = "{ig}, {lg}"
    // Each test below asserts the full combined CustomText for the relevant MAC.

    [Test]
    public async Task Parse_UnicastDstMac_IgBitIsFalse()
    {
        MacAddress dst = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "00:11:22:33:44:55 (Unicast, Globally Unique)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_MulticastDstMac_IgBitIsTrue()
    {
        MacAddress dst = MacAddress.FromBytes([0x01, 0x00, 0x5E, 0x00, 0x00, 0x01]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "01:00:5E:00:00:01 (Multicast, Globally Unique)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_BroadcastDstMac_IgBitIsTrue()
    {
        MacAddress dst = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // Broadcast: ig=true, lg=true.
        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "FF:FF:FF:FF:FF:FF (Multicast, Locally Administered)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_UnicastSrcMac_IgBitIsFalse()
    {
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.src", "00:11:22:33:44:55 (Unicast, Globally Unique)").ConfigureAwait(false);
    }

    #endregion

    #region L/G Bit Tests

    [Test]
    public async Task Parse_GloballyAdministeredMac_LgBitIsFalse()
    {
        MacAddress dst = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "00:11:22:33:44:55 (Unicast, Globally Unique)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_LocallyAdministeredMac_LgBitIsTrue()
    {
        MacAddress dst = MacAddress.FromBytes([0x02, 0x00, 0x00, 0x00, 0x00, 0x01]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "02:00:00:00:00:01 (Unicast, Locally Administered)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_LocalMulticastMac_BothIgAndLgSet()
    {
        MacAddress dst = MacAddress.FromBytes([0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "03:00:00:00:00:01 (Multicast, Locally Administered)").ConfigureAwait(false);
    }

    #endregion

    #region EtherType Display Text Tests

    [Test]
    public async Task Parse_EtherTypeIPv4_DisplayText()
    {
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // The EtherType display text should show the protocol name and hex value
        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.type", "IPv4 (0x0800)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_EtherTypeARP_DisplayText()
    {
        // Build an ARP frame: EtherType = 0x0806
        MacAddress dst = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

        // Minimal ARP request payload (28 bytes)
        byte[] arpPayload =
        [
            0x00, 0x01,             // Hardware type: Ethernet
            0x08, 0x00,             // Protocol type: IPv4
            0x06,                   // Hardware size: 6
            0x04,                   // Protocol size: 4
            0x00, 0x01,             // Opcode: request
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66,  // Sender MAC
            0xC0, 0xA8, 0x01, 0x01,               // Sender IP: 192.168.1.1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // Target MAC
            0xC0, 0xA8, 0x01, 0x02,               // Target IP: 192.168.1.2
        ];

        byte[] frame = BuildRawEthFrame(dst, src, 0x0806, arpPayload);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.type", "ARP (0x0806)").ConfigureAwait(false);
    }

    #endregion

    #region Ethernet II vs IEEE 802.3 Boundary

    [Test]
    public async Task Parse_TypeOrLen_Boundary_0x05FF_IsLength()
    {
        // Value 0x05FF (1535) < 0x0600 → IEEE 802.3 (length field)
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

        // Use a small payload since we're testing the boundary condition, not LLC parsing
        byte[] payload = new byte[46]; // minimum payload size
        byte[] frame = BuildRawEthFrame(dst, src, 0x0005, payload);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // Value < 0x0600 → should populate eth.len, not eth.type
        await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.len", 5).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "eth.type").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_TypeOrLen_Boundary_0x0600_IsType()
    {
        // Value 0x0600 (1536) >= 0x0600 → Ethernet II (EtherType)
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

        byte[] payload = new byte[46];
        byte[] frame = BuildRawEthFrame(dst, src, 0x0600, payload);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // Value >= 0x0600 → should populate eth.type, not eth.len
        await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0600).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "eth.len").ConfigureAwait(false);
    }

    #endregion

    #region Broadcast and Special Addresses

    [Test]
    public async Task Parse_BroadcastFrame_AllFieldsPresent()
    {
        MacAddress broadcast = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(broadcast, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "FF:FF:FF:FF:FF:FF").ConfigureAwait(false);
        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "FF:FF:FF:FF:FF:FF (Multicast, Locally Administered)").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_ZeroMacAddresses_FieldsPresent()
    {
        // All zeros MAC (00:00:00:00:00:00) — valid but unusual
        MacAddress zero = MacAddress.FromBytes([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(zero, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "00:00:00:00:00:00").ConfigureAwait(false);
        await ProtocolTestHelper.AssertDisplayText(stack, packet, "eth.dst", "00:00:00:00:00:00 (Unicast, Globally Unique)").ConfigureAwait(false);
    }

    #endregion

    #region Padding Detection

    [Test]
    public async Task Parse_SmallPayload_PaddingDetected()
    {
        // Create a frame where the IP payload is smaller than 46 bytes minimum.
        // Build an ARP frame (28 bytes payload) padded to 60 bytes minimum.
        MacAddress dst = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

        byte[] arpPayload =
        [
            0x00, 0x01,             // Hardware type: Ethernet
            0x08, 0x00,             // Protocol type: IPv4
            0x06,                   // Hardware size: 6
            0x04,                   // Protocol size: 4
            0x00, 0x01,             // Opcode: request
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66,  // Sender MAC
            0xC0, 0xA8, 0x01, 0x01,               // Sender IP: 192.168.1.1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // Target MAC
            0xC0, 0xA8, 0x01, 0x02,               // Target IP: 192.168.1.2
        ];

        // Build 60-byte frame: 14 header + 28 ARP + 18 padding = 60
        byte[] frame = new byte[60];
        byte[] rawFrame = BuildRawEthFrame(dst, src, 0x0806, arpPayload);
        rawFrame.AsSpan().CopyTo(frame.AsSpan());
        // Remaining bytes stay 0x00 — padding

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // ARP payload is 28 bytes, minimum is 46, so 18 bytes of padding
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "eth.padding").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_FullSizePayload_NoPadding()
    {
        // Standard Ethernet+IPv4+UDP frame — payload >= 46 bytes, no padding needed
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

        // Use a payload large enough so header+payload > 60 bytes (no padding)
        byte[] payload = new byte[100];
        byte[] frame = BuildEthFrameWithPayload(dst, src, payload);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "eth.padding").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "eth.trailer").ConfigureAwait(false);
    }

    #endregion

    #region Addr Field Tests

    [Test]
    public async Task Parse_AddrField_ExistsForDstAndSrc()
    {
        // eth.addr is a metadata-only alias group ({ eth.dst, eth.src }); no eth.addr field
        // is appended to the parse tree. Both endpoint MACs are exposed via the alias members.
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await Assert.That(stack.GetFieldId("eth.addr")).IsNull()
            .Because("eth.addr is an alias name and must never resolve via GetFieldId");

        FieldAliasGroupId? aliasId = stack.GetFieldAliasGroupId("eth.addr");
        await Assert.That(aliasId).IsNotNull().Because("eth.addr alias group must be registered");

        FieldAliasGroupInfo? aliasInfo = stack.GetFieldAliasGroup(aliasId!.Value);
        await Assert.That(aliasInfo).IsNotNull();
        await Assert.That(aliasInfo!.MemberCount).IsEqualTo(2)
            .Because("eth.addr alias must expose exactly two members: eth.dst and eth.src");

        FieldId dstId = stack.GetFieldId("eth.dst")!.Value;
        FieldId srcId = stack.GetFieldId("eth.src")!.Value;
        FieldId[] members = aliasInfo.Members.ToArray();
        await Assert.That(members.Contains(dstId)).IsTrue().Because("alias must include eth.dst");
        await Assert.That(members.Contains(srcId)).IsTrue().Because("alias must include eth.src");

        List<string> found = [];
        foreach (FieldId memberId in members)
        {
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextFieldValue(memberId, ref cookie, out FieldValue value))
            {
                bool ok = value.Data.TryGetAsMacAddress(out MacAddress addr);
                await Assert.That(ok).IsTrue().Because("alias member values must be MAC addresses");
                found.Add(addr.ToString());
            }
        }

        await Assert.That(found.Count).IsEqualTo(2)
            .Because("alias must surface destination and source across its two members");
        await Assert.That(found.Contains("AA:BB:CC:DD:EE:FF")).IsTrue();
        await Assert.That(found.Contains("11:22:33:44:55:66")).IsTrue();
    }

    #endregion

    #region Protocol Container Tests

    [Test]
    public async Task Parse_EthernetFrame_ProtocolContainerPresent()
    {
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // The Ethernet protocol container field should be present
        await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "eth").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_EthernetIPv4Frame_IPv4ProtocolPresent()
    {
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // An Ethernet+IPv4+UDP frame should have IPv4 present
        await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "ip").ConfigureAwait(false);
    }

    #endregion

    #region No FCS by Default

    [Test]
    public async Task Parse_DefaultSettings_NoFcsFields()
    {
        // By default, eth.assume_fcs = false, so FCS fields should not be present
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "eth.fcs").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "eth.fcs.status").ConfigureAwait(false);
    }

    #endregion
}
