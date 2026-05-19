// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for UDP protocol parsing (RFC 768).
/// Verifies ports, length, and tshark cross-validation.
/// </summary>
internal sealed class UdpBasicTests
{
    /// <summary>Creates an Ethernet + IPv4 + UDP frame with known values.</summary>
    private static byte[] BuildUdpFrame(ushort srcPort = 12345, ushort dstPort = 53)
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xAC100164), new IPv4Address(0xAC100101)); // 172.16.1.100 → 172.16.1.1
        UdpLayer udp = new(srcPort, dstPort);
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    [Test]
    public async Task Parse_Udp_SourcePort()
    {
        byte[] frame = BuildUdpFrame(srcPort: 12345);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.srcport", 12345).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_DestinationPort()
    {
        byte[] frame = BuildUdpFrame(dstPort: 53);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.dstport", 53).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_Length()
    {
        byte[] frame = BuildUdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // UDP header (8 bytes) + payload (8 bytes) = 16
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.length", 16).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_ChecksumFieldExists()
    {
        byte[] frame = BuildUdpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "udp.checksum").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_EmptyPayload()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(1000, 2000);

        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // UDP header only, length = 8 bytes (no payload)
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.length", 8).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.srcport", 1000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.dstport", 2000).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_PortField_ContainsBothEndpoints()
    {
        // Arrange
        byte[] frame = BuildUdpFrame(srcPort: 12345, dstPort: 53);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? portId = stack.GetFieldId("udp.port");
            await Assert.That(portId).IsNotNull().Because("udp.port must be registered");

            // Act: collect all udp.port occurrences (src + dst siblings in udp container)
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            List<ulong> found = [];
            while (packet.TryGetNextFieldValue(portId!.Value, ref cookie, out FieldValue value))
            {
                bool ok = value.Data.TryGetAsU64(out ulong port);
                await Assert.That(ok).IsTrue().Because("udp.port values must be U64");
                found.Add(port);
            }

            // Assert: exactly two occurrences matching source and destination ports
            await Assert.That(found.Count).IsEqualTo(2)
                .Because("udp.port must appear exactly twice — once for source, once for destination");
            await Assert.That(found.Contains(12345UL)).IsTrue()
                .Because("udp.port must contain source port 12345");
            await Assert.That(found.Contains(53UL)).IsTrue()
                .Because("udp.port must contain destination port 53");
        }
    }

    // tshark cross-validation lives in UdpTsharkTests.cs (Plan §3.1.5).
}
