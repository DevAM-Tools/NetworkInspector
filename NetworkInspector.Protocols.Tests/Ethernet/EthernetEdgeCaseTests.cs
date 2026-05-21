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

    #region I/G Bit Tests

    [Test]
    public async Task Parse_UnicastDstMac_IgBitIsFalse()
    {
        // Unicast MAC: first octet bit 0 = 0
        MacAddress dst = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.ig", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_MulticastDstMac_IgBitIsTrue()
    {
        // Multicast MAC: first octet bit 0 = 1 (e.g., 0x01)
        MacAddress dst = MacAddress.FromBytes([0x01, 0x00, 0x5E, 0x00, 0x00, 0x01]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.ig", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_BroadcastDstMac_IgBitIsTrue()
    {
        // Broadcast: FF:FF:FF:FF:FF:FF — I/G bit is set
        MacAddress dst = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.ig", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_UnicastSrcMac_IgBitIsFalse()
    {
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.src.ig", false).ConfigureAwait(false);
    }

    #endregion

    #region L/G Bit Tests

    [Test]
    public async Task Parse_GloballyAdministeredMac_LgBitIsFalse()
    {
        // Globally administered (OUI): bit 1 of first octet = 0
        MacAddress dst = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.lg", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_LocallyAdministeredMac_LgBitIsTrue()
    {
        // Locally administered: bit 1 of first octet = 1 (e.g., 0x02)
        MacAddress dst = MacAddress.FromBytes([0x02, 0x00, 0x00, 0x00, 0x00, 0x01]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.lg", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_LocalMulticastMac_BothIgAndLgSet()
    {
        // 0x03 = bits 0 and 1 set → both multicast (I/G) and locally administered (L/G)
        MacAddress dst = MacAddress.FromBytes([0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        MacAddress src = MacAddress.FromBytes([0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.ig", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.lg", true).ConfigureAwait(false);
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
        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.ig", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.lg", true).ConfigureAwait(false);
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
        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.ig", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, packet, "eth.dst.lg", false).ConfigureAwait(false);
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
        // eth.addr is appended twice — once as a child of eth.dst, once as a child of eth.src.
        // TryGetNextFieldValue traverses the flat field array and finds both occurrences
        // regardless of their nesting depth.
        MacAddress dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress src = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        byte[] frame = BuildEthFrame(dst, src);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? addrId = stack.GetFieldId("eth.addr");
        await Assert.That(addrId).IsNotNull().Because("eth.addr must be registered");

        // Act: collect all eth.addr occurrences
        FieldLookupCookie cookie = FieldLookupCookie.Start;
        List<string> found = [];
        while (packet.TryGetNextFieldValue(addrId!.Value, ref cookie, out FieldValue value))
        {
            bool ok = value.Data.TryGetAsMacAddress(out MacAddress addr);
            await Assert.That(ok).IsTrue().Because("eth.addr values must be MAC addresses");
            found.Add(addr.ToString());
        }

        // Assert: exactly two occurrences matching destination and source MAC addresses
        await Assert.That(found.Count).IsEqualTo(2)
            .Because("eth.addr must appear exactly twice — once for destination, once for source");
        await Assert.That(found.Contains("AA:BB:CC:DD:EE:FF")).IsTrue()
            .Because("eth.addr must contain destination address AA:BB:CC:DD:EE:FF");
        await Assert.That(found.Contains("11:22:33:44:55:66")).IsTrue()
            .Because("eth.addr must contain source address 11:22:33:44:55:66");
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
