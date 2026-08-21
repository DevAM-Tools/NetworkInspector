// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>End-to-end evaluation of classic filter expressions against parsed packets.</summary>
internal sealed class FilterMatchTests
{
    #region Presence and logic

    [Test]
    [Arguments("udp", true)]
    [Arguments("tcp", false)]
    [Arguments("ip", true)]
    [Arguments("eth", true)]
    [Arguments("!tcp", true)]
    [Arguments("!udp", false)]
    [Arguments("udp && ip", true)]
    [Arguments("udp && tcp", false)]
    [Arguments("udp || tcp", true)]
    [Arguments("tcp || icmp", false)]
    [Arguments("tcp || udp && ip", true)]
    [Arguments("(tcp || udp) && !icmp", true)]
    [Arguments("true", true)]
    [Arguments("false", false)]
    [Arguments("udp.srcport", true)]
    [Arguments("udp.payload", true)]
    [Arguments("tcp.flags", false)]
    public async Task Match_PresenceAndLogic(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    #endregion

    #region Comparisons

    [Test]
    [Arguments("udp.srcport == 53", true)]
    [Arguments("udp.srcport == 54", false)]
    [Arguments("udp.srcport != 54", true)]
    [Arguments("udp.srcport < 54", true)]
    [Arguments("udp.srcport <= 53", true)]
    [Arguments("udp.srcport > 52", true)]
    [Arguments("udp.srcport >= 54", false)]
    [Arguments("udp.dstport == 1024", true)]
    [Arguments("ip.ttl == 64", true)]
    [Arguments("ip.ttl == 0x40", true)]
    [Arguments("ip.ttl == 0b1000000", true)]
    [Arguments("ip.ttl == 0o100", true)]
    [Arguments("ip.proto == 17", true)]
    [Arguments("ip.src == 192.168.1.10", true)]
    [Arguments("ip.dst == 192.168.1.20", true)]
    [Arguments("ip.src == 192.168.1.99", false)]
    [Arguments("eth.src == 66:77:88:99:aa:bb", true)]
    [Arguments("eth.dst == 00:11:22:33:44:55", true)]
    [Arguments("eth.dst == 00:11:22:33:44:56", false)]
    [Arguments("ip.flags.df == true", true)]
    [Arguments("ip.flags.mf == false", true)]
    [Arguments("ip.ttl == 64.0", true)]
    public async Task Match_Comparisons(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    [Test]
    public async Task Match_AliasGroup_TestsEveryMember()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter source = FilterTestHelper.CompileOrThrow("ip.addr == 192.168.1.10", stack);
        Filter destination = FilterTestHelper.CompileOrThrow("ip.addr == 192.168.1.20", stack);
        Filter other = FilterTestHelper.CompileOrThrow("ip.addr == 10.0.0.1", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(source, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(destination, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(other, packet)).IsFalse();
    }

    #endregion

    #region Sets, ranges, slices and length

    [Test]
    [Arguments("udp.srcport in {53, 80}", true)]
    [Arguments("udp.srcport in {80, 443}", false)]
    [Arguments("udp.srcport in 1..100", true)]
    [Arguments("udp.srcport in 100..200", false)]
    [Arguments("udp.srcport in 53..53", true)]
    [Arguments("ip.src in {192.168.1.10, 10.0.0.1}", true)]
    [Arguments("len(udp.payload) == 4", true)]
    [Arguments("len(udp.payload) > 8", false)]
    [Arguments("udp.payload[0:2] == de:ad", true)]
    [Arguments("udp.payload[2:4] == be:ef", true)]
    [Arguments("udp.payload[0:2] == be:ef", false)]
    [Arguments("eth.src[0:3] == 66:77:88", true)]
    [Arguments("udp.payload[0:9] == de:ad", false)]
    public async Task Match_SetsRangesAndSlices(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    #endregion

    #region String predicates

    [Test]
    [Arguments("ip.checksum.status contains \"a\"", false)]
    [Arguments("udp.error.no_ip contains \"IP\"", false)]
    public async Task Match_StringPredicatesOnAbsentFields_DoNotMatch(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("dns.qry.name contains \"example\"", true)]
    [Arguments("dns.qry.name contains \"nope\"", false)]
    [Arguments("dns.qry.name matches \"^example\\\\.com$\"", true)]
    [Arguments("dns.qry.name matches \"^com\"", false)]
    [Arguments("dns.qry.name == \"example.com\"", true)]
    [Arguments("dns.qry.name != \"other.com\"", true)]
    public async Task Match_StringPredicates(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDnsQueryFrame());

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    [Test]
    public async Task Match_MatchesRespectsCustomTimeout()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterCompileOptions options = new() { RegexTimeout = TimeSpan.FromSeconds(2) };
        FilterResult<Filter> result = Filter.Compile("dns.qry.name matches \"example\"", stack, options);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDnsQueryFrame());

        await Assert.That(FilterTestHelper.MatchOrThrow(result.Value, packet)).IsTrue();
    }

    [Test]
    public async Task Match_StringPredicateOnBytesField_DoesNotMatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.payload contains \"dead\"", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    [Test]
    public async Task Match_RegexOnBytesField_DoesNotMatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.payload matches \".*\"", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    [Test]
    public async Task Compile_InvalidRegex_ReportsInvalidValue()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("udp.payload matches \"(\"", stack);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    #endregion

    #region Absent operands

    [Test]
    [Arguments("tcp.srcport == 53")]
    [Arguments("tcp.srcport != 53")]
    [Arguments("tcp.srcport in {1, 2}")]
    [Arguments("tcp.srcport in 1..2")]
    [Arguments("len(tcp.payload) == 0")]
    [Arguments("tcp.payload[0:1] == 00")]
    public async Task Match_AbsentOperand_NeverMatches(string expression)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    [Test]
    public async Task Match_TcpFrame_MatchesTcpExpressions()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("tcp && tcp.dstport == 80", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildTcpFrame(1024, 80));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
    }

    #endregion
}
