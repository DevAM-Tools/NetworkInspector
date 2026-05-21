// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Edge-case tests for ICMPv6 — truncated packets, zero-length NDP option,
/// invalid checksum (only when validation is enabled).
/// </summary>
internal sealed class Icmpv6MalformedTests
{
    [Test]
    public async Task Parse_TruncatedHeader_DoesNotThrow()
    {
        // Only 2 bytes of ICMPv6 header — must not crash, must still expose IPv6.
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        byte[] fullFrame = FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(128, 0)).CreateWithFixedValues().EmitFrame([]);
        // Drop the last 2 bytes of the ICMPv6 header (checksum).
        byte[] truncated = fullFrame[..^2];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(truncated);
        using (stack)
        {
            // Type may or may not be present depending on parser strictness;
            // primary requirement is that IPv6 parsing still completed.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "ipv6.nxt", 58).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NdpOption_LengthZero_DoesNotInfiniteLoop()
    {
        // Inject an option with length=0 inside an RA. The NDP parser
        // must terminate the option loop instead of spinning forever.
        byte[] badOption = [0x01 /* type */, 0x00 /* len = 0 */, 0, 0, 0, 0, 0, 0];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: false, other: false,
            routerLifetimeSec: 0, reachableTimeMs: 0, retransTimerMs: 0);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(ra).CreateWithFixedValues().EmitFrame(badOption);

        // If this hangs, the test runner will time the test out — no extra timeout API needed.
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 134).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_InvalidChecksum_VerificationEnabled_MarksBadStatus()
    {
        byte[] body = new byte[4];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(128, 0)).CreateWithFixedValues().EmitFrame(body);
        // Overwrite ICMPv6 checksum (Ethernet 14 + IPv6 40 = offset 54, checksum at +2 = 56) with deliberately wrong value.
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(56, 2), 0xFFFF);

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("icmpv6.verify_checksum", SettingValue.Bool(true)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, packetIndex: 0, timestamp: Timestamp.FromMillis(0));
        await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.checksum.status", "[Bad]").ConfigureAwait(false);
    }
}
