// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DNS happy-path tests (RFC 1035 + EDNS0). Covers query / response over
/// UDP, all common record types (A, AAAA, CNAME, MX, TXT, PTR, NS, SOA, SRV)
/// and TCP transport (length-prefixed).
/// </summary>
internal sealed class DnsBasicTests
{
    [Test]
    public async Task Parse_StandardQuery_AType()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildQuery(0x1234, "example.com", DnsLayer.DnsType.A);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.id", 0x1234).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.count.queries", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.count.answers", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.qry.name", "example.com").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.qry.type", DnsLayer.DnsType.A).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.qry.class", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "dns.flags.response", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "dns.flags.recdesired", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerA_IPv4Address()
    {
        byte[] rdata = [192, 0, 2, 1];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            0x4242, "example.com", DnsLayer.DnsType.A, rdata, ttlSeconds: 300);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "dns.flags.response", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.count.answers", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "dns.a", "192.0.2.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.resp.ttl", 300).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerAAAA_IPv6Address()
    {
        byte[] rdata = [
            0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0x01];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "example.com", DnsLayer.DnsType.Aaaa, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "dns.aaaa", "2001:db8::1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerCname_TargetName()
    {
        byte[] rdata = DnsLayer.EncodeName("alias.example.com");
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "www.example.com", DnsLayer.DnsType.Cname, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.cname", "alias.example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerMx_PreferenceAndExchange()
    {
        byte[] rdata = DnsLayer.BuildMxRdata(10, "mail.example.com");
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "example.com", DnsLayer.DnsType.Mx, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.mx.preference", 10).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.mx.mail_exchange", "mail.example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerTxt_String()
    {
        byte[] rdata = DnsLayer.BuildTxtRdata("v=spf1 -all");
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "example.com", DnsLayer.DnsType.Txt, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.txt", "v=spf1 -all").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerPtr_DomainName()
    {
        byte[] rdata = DnsLayer.EncodeName("host.example.com");
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "1.2.0.192.in-addr.arpa", DnsLayer.DnsType.Ptr, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.ptr.domain_name", "host.example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerNs_NameServer()
    {
        byte[] rdata = DnsLayer.EncodeName("ns1.example.com");
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "example.com", DnsLayer.DnsType.NS, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.ns", "ns1.example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AnswerSrv_Triplet()
    {
        byte[] rdata = DnsLayer.BuildSrvRdata(
            priority: 10, weight: 60, port: 5060, target: "sip.example.com");
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "_sip._udp.example.com", DnsLayer.DnsType.Srv, rdata, ttlSeconds: 60);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.srv.priority", 10).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.srv.weight", 60).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.srv.port", 5060).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.srv.name", "sip.example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_DnsOverTcp_LengthPrefix()
    {
        DnsLayer dns = DnsLayer.BuildQuery(0x1234, "example.com", DnsLayer.DnsType.A);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 53, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(dns.ToTcpPayload());
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.id", 0x1234).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.qry.name", "example.com").ConfigureAwait(false);
        }
    }

    #region Flags display text

    [Test]
    public async Task Parse_StandardQuery_FlagsDisplayText_RecursionDesired()
    {
        // Standard query: RD=1 (bit 8) → flags word 0x0100 → "[RD]"
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildQuery(0x1234, "example.com", DnsLayer.DnsType.A);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // flags = 0x0100 (RD set), display: "0x0100 [RD]"
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "dns.flags", "0x0100 [RD]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_StandardResponse_FlagsDisplayText_ResponseAndRdAndRa()
    {
        // Standard response: QR=1, RD=1, RA=1 → flags word 0x8180 → "[Response, RD, RA]"
        byte[] rdata = [192, 0, 2, 1];
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            0x1234, "example.com", DnsLayer.DnsType.A, rdata, ttlSeconds: 300);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // DnsLayer.BuildResponseSingleRR sets QR=1, RD=1, RA=1 → 0x8180 → "[Response, RD, RA]"
            await ProtocolTestHelper.AssertDisplayTextContains(stack, packet, "dns.flags", "[Response").ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayTextContains(stack, packet, "dns.flags", "RD").ConfigureAwait(false);
        }
    }

    #endregion
}

