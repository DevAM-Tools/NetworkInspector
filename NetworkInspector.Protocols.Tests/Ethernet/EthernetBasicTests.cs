// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for Ethernet protocol parsing (IEEE 802.3 / Ethernet II).
/// Verifies MAC addresses, EtherType, and field display text.
/// </summary>
internal sealed class EthernetBasicTests
{
    /// <summary>Creates a minimal Ethernet+IPv4+UDP frame for testing.</summary>
    private static byte[] _BuildEthFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        MacAddress srcMac = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102)); // 192.168.1.1 → 192.168.1.2
        UdpLayer udp = new(12345, 80);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    [Test]
    public async Task Parse_EthernetFrame_DstMac()
    {
        byte[] frame = _BuildEthFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? id = stack.GetFieldId("eth.dst");
            await Assert.That(id).IsNotNull();

            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(found).IsTrue();

            bool ok = value.Data.TryGetAsMacAddress(out MacAddress mac);
            await Assert.That(ok).IsTrue();
            await Assert.That(mac.ToString()).IsEqualTo("AA:BB:CC:DD:EE:FF");
        }
    }

    [Test]
    public async Task Parse_EthernetFrame_SrcMac()
    {
        byte[] frame = _BuildEthFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? id = stack.GetFieldId("eth.src");
            await Assert.That(id).IsNotNull();

            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(found).IsTrue();

            bool ok = value.Data.TryGetAsMacAddress(out MacAddress mac);
            await Assert.That(ok).IsTrue();
            await Assert.That(mac.ToString()).IsEqualTo("11:22:33:44:55:66");
        }
    }

    [Test]
    public async Task Parse_EthernetFrame_EtherTypeIPv4()
    {
        byte[] frame = _BuildEthFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // EtherType 0x0800 = IPv4
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0800).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_TruncatedFrame_DoesNotCrash()
    {
        // An Ethernet frame shorter than the 14-byte header should not crash
        byte[] truncated = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(truncated);
        using (stack)
        {
            // Packet should exist (frame protocol always runs), but Ethernet fields may not
            await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThanOrEqualTo(1); // materialize: false — current materialized count only
        }
    }

    // tshark cross-validation lives in EthernetTsharkTests.cs (Plan §3.1.1).
}
