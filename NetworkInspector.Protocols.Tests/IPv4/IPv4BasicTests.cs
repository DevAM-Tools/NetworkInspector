// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for IPv4 protocol parsing (RFC 791).
/// Verifies header fields, flags, TTL, addresses, and tshark cross-validation.
/// </summary>
internal sealed class IPv4BasicTests
{
    /// <summary>Creates an Ethernet + IPv4 + UDP frame with known values.</summary>
    private static byte[] _BuildIPv4Frame(
        uint srcAddr = 0xC0A80101,  // 192.168.1.1
        uint dstAddr = 0xC0A80102,  // 192.168.1.2
        byte ttl = 64,
        bool dontFragment = true)
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(srcAddr), new IPv4Address(dstAddr), ttl: ttl, dontFragment: dontFragment);
        UdpLayer udp = new(1234, 5678);
        byte[] payload = [0xDE, 0xAD];

        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    [Test]
    public async Task Parse_IPv4_SourceAddress()
    {
        byte[] frame = _BuildIPv4Frame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "ip.src", "192.168.1.1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_DestinationAddress()
    {
        byte[] frame = _BuildIPv4Frame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "ip.dst", "192.168.1.2").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_Version()
    {
        byte[] frame = _BuildIPv4Frame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ip.version", 4).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_Ttl()
    {
        byte[] frame = _BuildIPv4Frame(ttl: 128);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ip.ttl", 128).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_Protocol_Udp()
    {
        byte[] frame = _BuildIPv4Frame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // IP protocol 17 = UDP
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ip.proto", 17).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_DontFragment_Set()
    {
        byte[] frame = _BuildIPv4Frame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "ip.flags.df", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_DontFragment_Clear()
    {
        byte[] frame = _BuildIPv4Frame(dontFragment: false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "ip.flags.df", false).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_HeaderLength()
    {
        byte[] frame = _BuildIPv4Frame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Standard IPv4 header without options: 20 bytes
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ip.hdr_len", 20).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_TruncatedIPv4_DoesNotCrash()
    {
        // Valid Ethernet header + truncated IPv4 (only 5 bytes instead of minimum 20)
        byte[] dstMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] srcMac = [0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB];
        byte[] truncated = [
            .. dstMac, .. srcMac,
            0x08, 0x00, // EtherType: IPv4
            0x45, 0x00, 0x00, 0x1C, 0x00, // truncated IPv4 header (only 5 bytes)
        ];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(truncated);
        using (stack)
        {
            // Should not crash; frame protocol field always present
            await Assert.That(packet.FieldCount()).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task Parse_IPv4_AddrField_ContainsBothEndpoints()
    {
        // ip.addr is a metadata-only alias group ({ ip.src, ip.dst }); no ip.addr field is
        // appended. Verifies that the canonical field namespace exposes only ip.src/ip.dst,
        // and the alias group correctly enumerates both members.
        byte[] frame = _BuildIPv4Frame(srcAddr: 0xC0A80101, dstAddr: 0xC0A80102);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(stack.GetFieldId("ip.addr")).IsNull()
                .Because("ip.addr is an alias name and must never resolve via GetFieldId");

            FieldAliasGroupId? aliasId = stack.GetFieldAliasGroupId("ip.addr");
            await Assert.That(aliasId).IsNotNull().Because("ip.addr alias group must be registered");

            FieldAliasGroupInfo? aliasInfo = stack.GetFieldAliasGroup(aliasId!.Value);
            await Assert.That(aliasInfo).IsNotNull();
            await Assert.That(aliasInfo!.MemberCount).IsEqualTo(2)
                .Because("ip.addr alias must expose exactly two members: ip.src and ip.dst");

            FieldId srcId = stack.GetFieldId("ip.src")!.Value;
            FieldId dstId = stack.GetFieldId("ip.dst")!.Value;
            FieldId[] members = aliasInfo.Members.ToArray();
            await Assert.That(members.Contains(srcId)).IsTrue().Because("alias must include ip.src");
            await Assert.That(members.Contains(dstId)).IsTrue().Because("alias must include ip.dst");

            // Enumerate via alias members directly — confirm both endpoint values present.
            List<string> found = [];
            foreach (FieldId memberId in members)
            {
                FieldLookupCookie cookie = FieldLookupCookie.Start;
                while (packet.TryGetNextFieldValue(memberId, ref cookie, out FieldValue value))
                {
                    bool ok = value.Data.TryGetAsIPv4(out IPv4Address addr);
                    await Assert.That(ok).IsTrue().Because("alias member values must be IPv4 addresses");
                    found.Add(addr.ToString());
                }
            }

            await Assert.That(found.Count).IsEqualTo(2)
                .Because("alias must surface source and destination across its two members");
            await Assert.That(found.Contains("192.168.1.1")).IsTrue();
            await Assert.That(found.Contains("192.168.1.2")).IsTrue();
        }
    }

    #region Flags container display text

    [Test]
    public async Task Parse_IPv4_FlagsContainer_DontFragment_DisplayText()
    {
        byte[] frame = _BuildIPv4Frame(dontFragment: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "ip.flags", "[DF]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_FlagsContainer_NoFlags_DisplayText()
    {
        byte[] frame = _BuildIPv4Frame(dontFragment: false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "ip.flags", "[None]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_IPv4_FlagsSubFields_StillAccessible()
    {
        // Verify that ip.flags.df, ip.flags.rb, ip.flags.mf are still resolvable
        // after being re-parented under the new ip.flags NoneField container.
        byte[] frame = _BuildIPv4Frame(dontFragment: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "ip.flags.df", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "ip.flags.rb", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "ip.flags.mf", false).ConfigureAwait(false);
        }
    }

    #endregion

    // tshark cross-validation lives in IPv4TsharkTests.cs (Plan §3.1.4).
}
