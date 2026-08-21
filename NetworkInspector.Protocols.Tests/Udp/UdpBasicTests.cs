// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for UDP protocol parsing (RFC 768).
/// Verifies ports, length, and tshark cross-validation.
/// </summary>
internal sealed class UdpBasicTests
{
    /// <summary>Creates an Ethernet + IPv4 + UDP frame with known values.</summary>
    private static byte[] _BuildUdpFrame(ushort srcPort = 12345, ushort dstPort = 53)
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
        byte[] frame = _BuildUdpFrame(srcPort: 12345);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.srcport", 12345).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_DestinationPort()
    {
        byte[] frame = _BuildUdpFrame(dstPort: 53);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.dstport", 53).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Udp_Length()
    {
        byte[] frame = _BuildUdpFrame();
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
        byte[] frame = _BuildUdpFrame();
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
        // udp.port is a metadata-only alias group ({ udp.srcport, udp.dstport }); no udp.port
        // field is appended to the parse tree. The protocol table name udp.port (UDP demux)
        // lives in an independent namespace from this alias.
        byte[] frame = _BuildUdpFrame(srcPort: 12345, dstPort: 53);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(stack.GetFieldId("udp.port")).IsNull()
                .Because("udp.port is an alias name and must never resolve via GetFieldId");

            FieldAliasGroupId? aliasId = stack.GetFieldAliasGroupId("udp.port");
            await Assert.That(aliasId).IsNotNull().Because("udp.port alias group must be registered");

            FieldAliasGroupInfo? aliasInfo = stack.GetFieldAliasGroup(aliasId!.Value);
            await Assert.That(aliasInfo).IsNotNull();
            await Assert.That(aliasInfo!.MemberCount).IsEqualTo(2);

            FieldId srcId = stack.GetFieldId("udp.srcport")!.Value;
            FieldId dstId = stack.GetFieldId("udp.dstport")!.Value;
            FieldId[] members = aliasInfo.Members.ToArray();
            await Assert.That(members.Contains(srcId)).IsTrue();
            await Assert.That(members.Contains(dstId)).IsTrue();

            List<ulong> found = [];
            foreach (FieldId memberId in members)
            {
                FieldLookupCookie cookie = FieldLookupCookie.Start;
                while (packet.TryGetNextFieldValue(memberId, ref cookie, out FieldValue value, materialize: true)) // materialize: true — need complete field tree for assertion
                {
                    bool ok = value.Data.TryGetAsU64(out ulong port);
                    await Assert.That(ok).IsTrue().Because("alias member values must be U64");
                    found.Add(port);
                }
            }

            await Assert.That(found.Count).IsEqualTo(2);
            await Assert.That(found.Contains(12345UL)).IsTrue();
            await Assert.That(found.Contains(53UL)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_Udp_UnregisteredPorts_SucceedsWithoutPacketError()
    {
        byte[] frame = _BuildUdpFrame(srcPort: 1000, dstPort: 2000);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "udp").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.srcport", 1000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.dstport", 2000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    // tshark cross-validation lives in UdpTsharkTests.cs (Plan §3.1.5).
}
