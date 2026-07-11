// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for VLAN (IEEE 802.1Q) protocol parsing.
/// Covers standard 802.1Q tags, Q-in-Q (802.1ad), priority, DEI bit,
/// edge cases, malformed frames, and tshark cross-validation.
/// </summary>
internal sealed class VlanBasicTests
{
    #region Helper Methods

    /// <summary>Builds an Ethernet+VLAN+IPv4+UDP frame with the given VLAN parameters.</summary>
    private static byte[] _BuildVlanFrame(ushort vlanId, byte pcp = 0, byte dei = 0)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        VlanLayer vlan = new(vlanId, pcp, dei);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 80);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        return FrameStack.Start(eth).Then(vlan).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    #endregion

    #region Basic Field Tests

    [Test]
    public async Task Parse_VlanId_CorrectValue()
    {
        byte[] frame = _BuildVlanFrame(100);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 100).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanId_MaxValue()
    {
        // Maximum VLAN ID: 4095 (12-bit field)
        byte[] frame = _BuildVlanFrame(4095);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 4095).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanId_Zero()
    {
        // VLAN ID 0 = priority-tagged frame (null VLAN)
        byte[] frame = _BuildVlanFrame(0);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanId_One()
    {
        // VLAN ID 1 = default VLAN
        byte[] frame = _BuildVlanFrame(1);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 1).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanPriority_Zero()
    {
        byte[] frame = _BuildVlanFrame(100, 0);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.priority", 0).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanPriority_Seven()
    {
        // Maximum PCP value (3-bit field: 0-7)
        byte[] frame = _BuildVlanFrame(100, 7);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.priority", 7).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanDei_False()
    {
        byte[] frame = _BuildVlanFrame(100, 0, 0);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "vlan.dei", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanDei_True()
    {
        byte[] frame = _BuildVlanFrame(100, 0, 1);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertBoolField(stack, packet, "vlan.dei", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanEtherType_IPv4()
    {
        byte[] frame = _BuildVlanFrame(100);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // Inner EtherType should be IPv4 (0x0800)
        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.etype", 0x0800).ConfigureAwait(false);
    }

    #endregion

    #region EtherType Tests

    [Test]
    public async Task Parse_VlanFrame_EtherTypeIs8021Q()
    {
        byte[] frame = _BuildVlanFrame(100);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // Outer EtherType should be 802.1Q (0x8100)
        await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x8100).ConfigureAwait(false);
    }

    #endregion

    #region Protocol Presence Tests

    [Test]
    public async Task Parse_VlanFrame_VlanProtocolPresent()
    {
        byte[] frame = _BuildVlanFrame(100);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "vlan").ConfigureAwait(false);
    }

    [Test]
    public async Task Parse_VlanFrame_IPv4ProtocolPresent()
    {
        byte[] frame = _BuildVlanFrame(100);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        // IPv4 should be dispatched via inner EtherType
        await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "ip").ConfigureAwait(false);
    }

    #endregion

    #region Priority + DEI Combined

    [Test]
    public async Task Parse_AllVlanFieldsCombined()
    {
        // VLAN ID 42, priority 5 (Voice), DEI set
        byte[] frame = _BuildVlanFrame(42, 5, 1);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.id", 42).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.priority", 5).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, packet, "vlan.dei", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packet, "vlan.etype", 0x0800).ConfigureAwait(false);
    }

    #endregion
}
