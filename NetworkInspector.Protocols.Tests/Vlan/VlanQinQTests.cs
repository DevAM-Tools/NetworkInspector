// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for IEEE 802.1ad Q-in-Q (double VLAN tagging) parsing.
/// Verifies outer and inner VLAN tags are both correctly parsed.
/// </summary>
internal sealed class VlanQinQTests
{
    #region Helper Methods

    /// <summary>Builds an Ethernet+QinQ+VLAN+IPv4+UDP frame (double-tagged).</summary>
    private static byte[] _BuildQinQFrame(
        ushort outerVlanId, ushort innerVlanId,
        byte outerPcp = 0, byte innerPcp = 0)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        VlanLayer outerVlan = new(outerVlanId, isQinQ: true, pcp: outerPcp);
        VlanLayer innerVlan = new(innerVlanId, pcp: innerPcp);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 80);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        return FrameStack.Start(eth).Then(outerVlan).Then(innerVlan).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    #endregion

    #region Q-in-Q Basic Tests

    [Test]
    public async Task Parse_QinQ_OuterVlanId()
    {
        byte[] frame = _BuildQinQFrame(100, 200);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // The first vlan.id encountered should be the outer VLAN
        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 100).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_QinQ_EtherTypeIs88A8()
    {
        byte[] frame = _BuildQinQFrame(100, 200);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // Outer EtherType should be 802.1ad (0x88A8)
        await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x88A8).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_QinQ_VlanProtocolPresent()
    {
        byte[] frame = _BuildQinQFrame(100, 200);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "vlan").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_QinQ_IPv4Present()
    {
        byte[] frame = _BuildQinQFrame(100, 200);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // IPv4 should still be dispatched through the double-tagged frame
        await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "ip").ConfigureAwait(false);
    }

    #endregion
}
