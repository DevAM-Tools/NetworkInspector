// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for DHCPv4 parsing: the summary texts and the use of the hardware address length (hlen) for the client hardware
/// address.
/// </summary>
internal sealed class DhcpHardeningTests
{
    private const int _HardwareLengthOffset = 2;

    private static byte[] _DiscoverPayload(uint xid, byte hardwareLength)
    {
        byte[] payload = DhcpPayloadBuilder.BuildPayload(
            op: 1, xid, [new DhcpPayloadBuilder.Option(53, [1])]);
        payload[_HardwareLengthOffset] = hardwareLength;
        return payload;
    }

    [Test]
    public async Task SummaryTextsShowTheTransactionIdAsEightUppercaseHexDigits()
    {
        byte[] frame = DhcpPayloadBuilder.WrapUdp(_DiscoverPayload(0x0000ABCDu, 6));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("DHCP Discover (1) - Transaction ID 0x0000ABCD");
            await ProtocolTestHelper.AssertDisplayText(
                stack, packet, "dhcp", "DHCP Discover (1) - Transaction ID 0x0000ABCD").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task HardwareAddressOfSixBytesIsShownAsAMacAddress()
    {
        byte[] frame = DhcpPayloadBuilder.WrapUdp(_DiscoverPayload(1, 6));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertMacField(stack, packet, "dhcp.hw.mac_addr", "AA:BB:CC:DD:EE:FF").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((byte)0)]
    [Arguments((byte)4)]
    [Arguments((byte)5)]
    [Arguments((byte)7)]
    [Arguments((byte)16)]
    public async Task HardwareAddressOfAnotherLengthIsNotShownAsAMacAddress(byte hardwareLength)
    {
        byte[] frame = DhcpPayloadBuilder.WrapUdp(_DiscoverPayload(1, hardwareLength));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dhcp.hw.len", hardwareLength).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dhcp.hw.mac_addr").ConfigureAwait(false);
        }
    }
}
