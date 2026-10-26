// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that the checksum status index group of ICMPv6 equals the emitted field when the enclosing IP layer is not IPv6
/// (ICMPv6 can be carried by a crafted IPv4 packet with protocol 58; the pseudo header cannot be built then).
/// </summary>
internal sealed class Icmpv6HardeningTests
{
    private static byte[] _Icmpv6OverIpv4Frame()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(new IcmpV4Layer(128, 0)).CreateWithFixedValues().EmitFrame(new byte[4]);

        // The frame builder cannot compute an ICMPv6 checksum over IPv4, so the ICMPv4 layer is used and the IPv4 protocol
        // number (offset 14 + 9) is changed to 58 (ICMPv6).
        frame[14 + 9] = 58;
        return frame;
    }

    [Test]
    public async Task ChecksumStatusGroupEqualsTheFieldWhenNoIpv6LayerEncloses()
    {
        byte[] frame = _Icmpv6OverIpv4Frame();

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame, ("icmpv6.verify_checksum", SettingValue.Bool(true)))
            .ConfigureAwait(false);
    }

    [Test]
    public async Task ChecksumStatusIsUnverifiedWhenNoIpv6LayerEncloses()
    {
        byte[] frame = _Icmpv6OverIpv4Frame();

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("icmpv6.verify_checksum", SettingValue.Bool(true)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, packetIndex: 0, timestamp: Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.checksum.status", "[Unverified]").ConfigureAwait(false);
    }
}
