// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// ICMPv6 checksum validation. Addresses come from the innermost enclosing IPv6
/// layer via sibling walk, matching UDP/TCP — not from a first-occurrence flat-array scan.
/// </summary>
internal sealed class Icmpv6ChecksumTests
{
    #region Helpers

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]);
    private static readonly IPv6Address _SrcIp = IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    private static readonly IPv6Address _DstIp = IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);

    private static byte[] _BuildEchoRequestFrame()
    {
        byte[] body = new byte[4];
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_SrcIp, _DstIp);
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(128, 0)).CreateWithFixedValues().EmitFrame(body);
    }

    /// <summary>Corrupts the ICMPv6 checksum (Ethernet 14 + IPv6 40 + 2 = offset 56).</summary>
    private static void _CorruptChecksum(byte[] frame) =>
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), 0xFFFF);

    #endregion

    #region Tests

    [Test]
    public async Task Checksum_Status_Good_WhenEnabled()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("icmpv6.verify_checksum", SettingValue.Bool(true)));

        byte[] frame = _BuildEchoRequestFrame();
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, packetIndex: 0, timestamp: Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.checksum.status", "[Good]").ConfigureAwait(false);
    }

    [Test]
    public async Task Checksum_Status_Bad_WhenCorrupted()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("icmpv6.verify_checksum", SettingValue.Bool(true)));

        byte[] frame = _BuildEchoRequestFrame();
        _CorruptChecksum(frame);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, packetIndex: 0, timestamp: Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.checksum.status", "[Bad]").ConfigureAwait(false);
    }

    [Test]
    public async Task Checksum_Status_Absent_WhenDisabled()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = _BuildEchoRequestFrame();
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, packetIndex: 0, timestamp: Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.checksum.status").ConfigureAwait(false);
    }

    #endregion
}
