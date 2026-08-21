// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Malformed and edge case tests for VLAN protocol parsing.
/// Verifies graceful handling of truncated or invalid VLAN frames.
/// </summary>
internal sealed class VlanMalformedTests
{
    [Test]
    public async Task Parse_TruncatedVlanTag_DoesNotCrash()
    {
        // Ethernet header (14 bytes) + incomplete VLAN tag (only 2 of 4 bytes)
        byte[] frame =
        [
            0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,  // dst MAC
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66,  // src MAC
            0x81, 0x00,                            // EtherType = 802.1Q
            0x00, 0x64                             // only 2 bytes of VLAN tag (need 4)
        ];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Ethernet should parse, but VLAN may not fully parse
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "eth").ConfigureAwait(false);
            await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThanOrEqualTo(1); // materialize: false — current materialized count only
        }
    }

    [Test]
    public async Task Parse_VlanHeaderOnly_NoPayload()
    {
        // Ethernet header + complete VLAN tag (4 bytes) but no inner payload
        byte[] frame =
        [
            0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,  // dst MAC
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66,  // src MAC
            0x81, 0x00,                            // EtherType = 802.1Q
            0x00, 0x64,                            // PCP=0, DEI=0, VLAN ID=100
            0x08, 0x00                             // Inner EtherType: IPv4
        ];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // VLAN should parse, IPv4 should be dispatched but may fail due to no data
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x8100).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 100).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ExactEthernetPlusVlan_NoInnerPayload()
    {
        // 14 (eth) + 4 (vlan) = 18 bytes total — valid VLAN tag but nothing inside
        byte[] frame =
        [
            0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66,
            0x81, 0x00,                            // 802.1Q
            0xE0, 0x01,                            // PCP=7, DEI=0, VLAN ID=1
            0x08, 0x00                             // Inner: IPv4
        ];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.priority", 7).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 1).ConfigureAwait(false);
        }
    }
}
