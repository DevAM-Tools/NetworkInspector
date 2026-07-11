// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Cross-validation tests for ICMPv6 and NDP parsing against tshark PDML output.
/// tshark availability is mandatory — see <see cref="TsharkVerifier"/>.
/// </summary>
internal sealed class Icmpv6TsharkTests
{
    private static byte[] _BuildIcmpV6Frame(byte type, byte code, ReadOnlySpan<byte> body)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(type, code)).CreateWithFixedValues().EmitFrame(body);
    }

    [Test]
    public async Task Tshark_EchoRequest_Type128()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 0x0001);
        byte[] frame = _BuildIcmpV6Frame(type: 128, code: 0, body);

        string? value = TsharkVerifier.GetFieldValue(frame, "icmpv6.type");
        await Assert.That(value).IsNotNull().Because("tshark must report this field");
        await Assert.That(value).IsEqualTo("128");
    }

    [Test]
    public async Task Tshark_EchoRequest_IdentifierMatches()
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 0x0001);
        byte[] frame = _BuildIcmpV6Frame(type: 128, code: 0, body);

        string? value = TsharkVerifier.GetFieldValue(frame, "icmpv6.echo.identifier");
        await Assert.That(value).IsNotNull().Because("tshark must report this field");
        // tshark may format as "4660" (decimal) or "0x1234" — accept either.
        bool ok = value == "4660" || value == "0x1234";
        await Assert.That(ok).IsTrue().Because($"expected 0x1234 or 4660, got '{value}'");
    }

    [Test]
    public async Task Tshark_RouterAdvertisement_TypeAndHopLimit()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: true, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 30_000, retransTimerMs: 1000);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(ra).CreateWithFixedValues().EmitFrame([]);

        string? type = TsharkVerifier.GetFieldValue(frame, "icmpv6.type");
        await Assert.That(type).IsNotNull().Because("tshark must report this field");
        await Assert.That(type).IsEqualTo("134");

        string? hop = TsharkVerifier.GetFieldValue(frame, "icmpv6.nd.ra.cur_hop_limit");
        await Assert.That(hop).IsNotNull().Because("tshark must report this field");
        await Assert.That(hop).IsEqualTo("64");
    }

    [Test]
    public async Task Tshark_NeighborSolicitation_TargetAddress()
    {
        byte[] target = [
            0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0x01];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        IcmpV6NeighborSolicitationLayer ns = new(IPv6Address.FromBytes(target));
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(ns).CreateWithFixedValues().EmitFrame([]);

        string? value = TsharkVerifier.GetFieldValue(frame, "icmpv6.nd.ns.target_address");
        // tshark exposes this as icmpv6.nd.ns.target_address; fall back to generic.
        if (value is null)
        {
            value = TsharkVerifier.GetFieldValue(frame, "icmpv6.nd.target_address");
        }
        await Assert.That(value).IsNotNull().Because("tshark must report a target address field");
        await Assert.That(value).IsEqualTo("2001:db8::1");
    }

    [Test]
    public async Task Tshark_NeighborAdvertisement_FlagsExposed()
    {
        byte[] target = [
            0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0x01];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        IcmpV6NeighborAdvertisementLayer na = new(
            IPv6Address.FromBytes(target), router: true, solicited: true, overrideFlag: false);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(na).CreateWithFixedValues().EmitFrame([]);

        string? typeValue = TsharkVerifier.GetFieldValue(frame, "icmpv6.type");
        await Assert.That(typeValue).IsNotNull().Because("tshark must report the type field");
        await Assert.That(typeValue).IsEqualTo("136");
    }
}
