// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Covers <c>$Name { ... }</c> and <c>$Name[i] { ... }</c> subtree evaluation.</summary>
internal sealed class FilterScopeTests
{
    #region Existential scope

    [Test]
    [Arguments("$udp { udp.srcport == 53 }", true)]
    [Arguments("$udp { udp.srcport == 54 }", false)]
    [Arguments("$udp { udp.srcport == 53 && udp.dstport == 1024 }", true)]
    [Arguments("$udp { udp.srcport == 53 || udp.dstport == 9 }", true)]
    [Arguments("$udp { !udp.srcport }", false)]
    [Arguments("$udp { udp.payload }", true)]
    [Arguments("$ip { ip.ttl == 64 }", true)]
    [Arguments("$ip { udp.srcport == 53 }", false)]
    [Arguments("$udp { ip.ttl == 64 }", false)]
    [Arguments("$eth { eth.src == 66:77:88:99:aa:bb }", true)]
    [Arguments("$tcp { tcp.srcport == 53 }", false)]
    [Arguments("$udp.payload { udp.payload }", true)]
    [Arguments("$udp.srcport { udp.srcport == 53 }", true)]
    [Arguments("$udp.srcport { udp.dstport == 1024 }", false)]
    public async Task Scope_RestrictsEvaluationToSubtree(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    [Test]
    public async Task Scope_NestedScopes_Compose()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "$dns { $dns.qry { dns.qry.name contains \"example\" } }",
            stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDnsQueryFrame());

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
    }

    [Test]
    public async Task Scope_DoesNotReachIntoSiblingProtocolLayers()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter inside = FilterTestHelper.CompileOrThrow("$dns { dns.qry.name contains \"example\" }", stack);
        Filter outside = FilterTestHelper.CompileOrThrow("$udp { dns.qry.name contains \"example\" }", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDnsQueryFrame());

        await Assert.That(FilterTestHelper.MatchOrThrow(inside, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(outside, packet)).IsFalse();
    }

    [Test]
    public async Task Scope_CombinedWithOuterTerms()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("ip.ttl == 64 && $udp { udp.srcport == 53 }", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
    }

    #endregion

    #region Indexed scope

    [Test]
    [Arguments("$vlan[0] { vlan.id == 100 }", true)]
    [Arguments("$vlan[1] { vlan.id == 200 }", true)]
    [Arguments("$vlan[0] { vlan.id == 200 }", false)]
    [Arguments("$vlan[1] { vlan.id == 100 }", false)]
    [Arguments("$vlan[2] { vlan.id == 100 }", false)]
    [Arguments("$vlan { vlan.id == 200 }", true)]
    [Arguments("$vlan { vlan.id == 300 }", false)]
    public async Task Scope_OccurrenceSelectsOneBfsHit(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDoubleVlanUdpFrame());

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    [Test]
    public async Task Scope_OccurrenceBeyondHitCount_DoesNotMatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("$udp[1] { udp.srcport == 53 }", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    [Test]
    public async Task Scope_MissingAnchor_DoesNotMatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("$vlan { vlan.id == 100 }", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    #endregion

    #region Scope and index pruning

    [Test]
    public async Task Scope_ParticipatesInIndexPruning()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = new(stack);
        Packet packet = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024));
        Filter filter = FilterTestHelper.CompileOrThrow("$udp { udp.srcport == 53 }", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.Contains((uint)packet.Id.Value)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet, index)).IsTrue();
    }

    #endregion
}
