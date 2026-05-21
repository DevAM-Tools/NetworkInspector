// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Cross-validation tests for DNS parsing against tshark.
/// </summary>
internal sealed class DnsTsharkTests
{
    [Test]
    public async Task Tshark_QueryName_Matches()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildQuery(0x1234, "example.com", DnsLayer.DnsType.A);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        string? value = TsharkVerifier.GetFieldValue(frame, "dns.qry.name");
        await Assert.That(value).IsNotNull().Because("tshark must report this field");
        await Assert.That(value).IsEqualTo("example.com");
    }

    [Test]
    public async Task Tshark_QueryType_Matches()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildQuery(0x1234, "example.com", DnsLayer.DnsType.A);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        string? value = TsharkVerifier.GetFieldValue(frame, "dns.qry.type");
        await Assert.That(value).IsNotNull().Because("tshark must report this field");
        await Assert.That(value).IsEqualTo("1");
    }

    [Test]
    public async Task Tshark_TransactionId_Matches()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildQuery(0x4242, "example.com", DnsLayer.DnsType.A);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        string? value = TsharkVerifier.GetFieldValue(frame, "dns.id");
        await Assert.That(value).IsNotNull().Because("tshark must report this field");
        // tshark may produce "0x4242" or "16962" — accept either.
        bool ok = string.Equals(value, "0x4242", StringComparison.OrdinalIgnoreCase) || value == "16962";
        await Assert.That(ok).IsTrue().Because($"got '{value}'");
    }

    [Test]
    public async Task Tshark_AnswerA_Address_Matches()
    {
        byte[] rdata = [192, 0, 2, 99];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "example.com", DnsLayer.DnsType.A, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        string? value = TsharkVerifier.GetFieldValue(frame, "dns.a");
        await Assert.That(value).IsNotNull().Because("tshark must report this field");
        await Assert.That(value).IsEqualTo("192.0.2.99");
    }
}

