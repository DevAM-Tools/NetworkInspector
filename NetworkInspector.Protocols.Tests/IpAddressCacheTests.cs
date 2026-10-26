// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that the per-thread IPv4/IPv6 address caches never outlive the parse of the packet that wrote them: an entry keyed
/// by packet id alone would be taken for the addresses of an unrelated packet that has the same id on the same thread (for
/// example the packet of another session).
/// </summary>
internal sealed class IpAddressCacheTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static IPv6Address _V6(byte last) =>
        IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, last]);

    private static byte[] _Icmpv6EchoFrame(byte srcLast, byte dstLast)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_V6(srcLast), _V6(dstLast));
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(128, 0)).CreateWithFixedValues().EmitFrame(new byte[4]);
    }

    [Test]
    public async Task Ipv4CacheHoldsNoEntryAfterTheParseOfThePacketEnds()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(40000, 4000);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(new byte[4]);

        using Stack stack = ProtocolTestHelper.BuildStack();
        _ = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await Assert.That(IPv4Protocol.TryGetCachedAddresses(new PacketId(0), out _, out _)).IsFalse();
    }

    [Test]
    public async Task Ipv6CacheHoldsNoEntryAfterTheParseOfThePacketEnds()
    {
        byte[] frame = _Icmpv6EchoFrame(1, 2);

        using Stack stack = ProtocolTestHelper.BuildStack();
        _ = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await Assert.That(IPv6Protocol.TryGetCachedAddresses(new PacketId(0), out _, out _)).IsFalse();
    }

    [Test]
    public async Task ChecksumStatusIgnoresAnEntryOfAnotherSessionsPacketWithTheSameId()
    {
        // Packet 0 of stack A is parsed first and expanded later, as a UI does. In between, packet 0 of stack B (other
        // addresses) is parsed on the same thread. Verification of A's packet must use A's own addresses.
        using Stack stackA = ProtocolTestHelper.BuildStackWithSettings(("icmpv6.verify_checksum", SettingValue.Bool(true)));
        using Stack stackB = ProtocolTestHelper.BuildStackWithSettings(("icmpv6.verify_checksum", SettingValue.Bool(true)));

        Packet packetA = ProtocolTestHelper.ParseFrame(stackA, _Icmpv6EchoFrame(1, 2), 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stackB, _Icmpv6EchoFrame(9, 10), 0, Timestamp.FromMillis(0));
        packetA.MaterializeAll();

        FieldId? statusId = stackA.GetFieldId("icmpv6.checksum.status");
        bool found = packetA.TryGetFieldValue(statusId!.Value, out FieldValue value, materialize: true); // field is registered by ICMPv6
        bool isString = value.Data.TryGetAsString(out string? status);

        await Assert.That(found).IsTrue();
        await Assert.That(isString).IsTrue();
        await Assert.That(status).IsEqualTo("[Good]");
    }
}
