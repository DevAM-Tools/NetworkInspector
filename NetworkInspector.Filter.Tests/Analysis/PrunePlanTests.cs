// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Analysis;

/// <summary>
/// Covers the live presence-pruning program behind <see cref="Filter.TryIsPresenceCandidate{TIndex}"/>: verdicts per
/// dependency shape, live index growth, unresolved readers, index switches, and stateful filters.
/// </summary>
internal sealed class PrunePlanTests
{
    #region Helpers

    /// <summary>The six shapes of the shape table, indexed 0..5 in this order.</summary>
    private static byte[][] _ShapeFrames() =>
    [
        FilterTestHelper.BuildUdpFrame(1, 2),        // 0: UDP
        FilterTestHelper.BuildTcpFrame(1, 80),       // 1: TCP
        FilterTestHelper.BuildDnsQueryFrame(),       // 2: UDP + DNS
        _WithIpProtocol(FilterTestHelper.BuildUdpFrame(1, 2), 253), // 3: IPv4 without transport
        _WithEtherType(FilterTestHelper.BuildUdpFrame(1, 2), 0x88B5), // 4: Ethernet only
        FilterTestHelper.BuildDoubleVlanUdpFrame(),  // 5: VLAN-tagged UDP
    ];

    private static byte[] _WithIpProtocol(byte[] frame, byte protocol)
    {
        frame[14 + 9] = protocol;
        return frame;
    }

    private static byte[] _WithEtherType(byte[] frame, ushort etherType)
    {
        frame[12] = (byte)(etherType >> 8);
        frame[13] = (byte)etherType;
        return frame;
    }

    private static PacketIndex _IndexShapes(Stack stack)
    {
        PacketIndex index = new(stack);
        byte[][] frames = _ShapeFrames();
        for (int i = 0; i < frames.Length; i++)
        {
            _ = FilterTestHelper.ParseIndexed(stack, index, frames[i], i);
        }

        return index;
    }

    /// <summary>Per-id presence verdicts as a string: <c>n</c> no pruning, else <c>1</c>/<c>0</c>.</summary>
    private static string _Verdicts<TIndex>(Filter filter, TIndex index, int count)
        where TIndex : IPacketIndexReader
    {
        StringBuilder text = new(count);
        for (int i = 0; i < count; i++)
        {
            if (!filter.TryIsPresenceCandidate(index, (uint)i, out bool candidate))
            {
                text.Append('n');
                continue;
            }

            text.Append(candidate ? '1' : '0');
        }

        return text.ToString();
    }

    /// <summary>The materialized candidate set in the same notation (<c>n</c> for every id when it cannot prune).</summary>
    private static string _Materialized(Filter filter, PacketIndex index, int count)
    {
        if (!filter.TryBuildCandidates(index, out RoaringBitmap? candidates))
        {
            return new string('n', count);
        }

        StringBuilder text = new(count);
        for (int i = 0; i < count; i++)
        {
            text.Append(candidates.Contains((uint)i) ? '1' : '0');
        }

        return text.ToString();
    }

    #endregion

    #region Shapes

    [Test]
    [Arguments("udp", "101001")]
    [Arguments("udp.srcport == 1", "101001")]
    [Arguments("udp.port == 1", "101001")]
    [Arguments("udp && tcp", "000000")]
    [Arguments("udp || tcp", "111001")]
    [Arguments("udp && !tcp", "101001")]
    [Arguments("udp || !tcp", "nnnnnn")]
    [Arguments("(udp || tcp) && ip", "111001")]
    [Arguments("$udp { udp.srcport == 1 }", "101001")]
    [Arguments("dns && udp", "001000")]
    [Arguments("udp && (tcp || !udp)", "101001")]
    [Arguments("(tcp || !udp) && udp", "101001")]
    [Arguments("!udp", "nnnnnn")]
    public async Task PresenceVerdictFollowsDependencyShape(string expression, string expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _IndexShapes(stack);
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);

        string live = _Verdicts(filter, index, 6);
        string view = _Verdicts(filter, index.AsReadOnlyView(), 6);
        string materialized = _Materialized(filter, index, 6);

        await Assert.That(live).IsEqualTo(expected);
        await Assert.That(view).IsEqualTo(expected);
        await Assert.That(materialized).IsEqualTo(expected);
    }

    #endregion

    #region Live growth

    [Test]
    public async Task CachedPlanSeesPacketsIndexedAfterItWasBuilt()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = new(stack);
        _ = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildTcpFrame(1, 80), 0);
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        // The first query resolves and caches the plan for this index.
        await Assert.That(filter.TryIsPresenceCandidate(index, 0, out bool first)).IsTrue();
        await Assert.That(first).IsFalse();

        // 70 000 more packets cross the roaring container key at 65 536; UDP on every third id.
        byte[] udp = FilterTestHelper.BuildUdpFrame(1, 2);
        byte[] tcp = FilterTestHelper.BuildTcpFrame(1, 80);
        for (int id = 1; id <= 70_000; id++)
        {
            _ = FilterTestHelper.ParseIndexed(stack, index, id % 3 == 0 ? udp : tcp, id);
        }

        foreach (int id in new[] { 1, 65_535, 65_536, 65_537, 70_000 })
        {
            await Assert.That(filter.TryIsPresenceCandidate(index, (uint)id, out bool candidate)).IsTrue();
            await Assert.That(candidate).IsEqualTo(id % 3 == 0);
        }
    }

    #endregion

    #region Unresolved readers

    [Test]
    [Arguments("tcp && udp.srcport == 1", true, "010000")]
    [Arguments("tcp || udp.srcport == 1", false, "nnnnnn")]
    [Arguments("udp.srcport == 1", false, "nnnnnn")]
    [Arguments("udp", false, "nnnnnn")]
    public async Task UnresolvedConjunctIsDroppedAndUnresolvedDisjunctDisablesPruning(
        string expression,
        bool prunes,
        string expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _IndexShapes(stack);
        PartialPacketIndexReader partial = new(
            index,
            FilterTestHelper.ProtocolIdOf(stack, "udp"),
            FilterTestHelper.FieldIdOf(stack, "udp.srcport"));
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);

        string verdicts = _Verdicts(filter, partial, 6);

        await Assert.That(verdicts).IsEqualTo(expected);
        await Assert.That(verdicts.Contains('n', StringComparison.Ordinal)).IsEqualTo(!prunes);
    }

    [Test]
    public async Task ForeignReaderIsResolvedOnEveryCall()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _IndexShapes(stack);
        PartialPacketIndexReader hidesUdp = new(index, FilterTestHelper.ProtocolIdOf(stack, "udp"), FieldId.Invalid);
        SwitchingPacketIndexReader reader = new(index, hidesUdp);
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        string before = _Verdicts(filter, reader, 6);
        reader.UseSecond = true;
        string after = _Verdicts(filter, reader, 6);
        reader.UseSecond = false;
        string again = _Verdicts(filter, reader, 6);

        await Assert.That(before).IsEqualTo("101001");
        await Assert.That(after).IsEqualTo("nnnnnn");
        await Assert.That(again).IsEqualTo("101001");
    }

    #endregion

    #region Index switch

    [Test]
    public async Task AlternatingIndexesAnswerFromTheIndexOfEachCall()
    {
        // Index A records ids 0..5 with UDP on even ids; index B records ids 6..11 with UDP on odd ids. A stack
        // parses each id first only once, so the two indexes cover disjoint id ranges.
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex a = new(stack);
        PacketIndex b = new(stack);
        byte[] udp = FilterTestHelper.BuildUdpFrame(1, 2);
        byte[] tcp = FilterTestHelper.BuildTcpFrame(1, 80);
        for (int id = 0; id < 12; id++)
        {
            bool isUdp = id < 6 ? id % 2 == 0 : id % 2 == 1;
            _ = FilterTestHelper.ParseIndexed(stack, id < 6 ? a : b, isUdp ? udp : tcp, id);
        }

        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);
        StringBuilder direct = new();
        StringBuilder viewed = new();
        for (int round = 0; round < 2; round++)
        {
            for (int id = 0; id < 12; id++)
            {
                _ = filter.TryIsPresenceCandidate(a, (uint)id, out bool inA);
                _ = filter.TryIsPresenceCandidate(b, (uint)id, out bool inB);
                direct.Append(inA ? '1' : '0').Append(inB ? '1' : '0');
                _ = filter.TryIsPresenceCandidate(a.AsReadOnlyView(), (uint)id, out bool viewA);
                _ = filter.TryIsPresenceCandidate(b.AsReadOnlyView(), (uint)id, out bool viewB);
                viewed.Append(viewA ? '1' : '0').Append(viewB ? '1' : '0');
            }
        }

        // Per id: A then B. Even ids < 6 are UDP in A; odd ids ≥ 6 are UDP in B.
        const string expectedRound = "100010001000000100010001";
        await Assert.That(direct.ToString()).IsEqualTo(expectedRound + expectedRound);
        await Assert.That(viewed.ToString()).IsEqualTo(expectedRound + expectedRound);
    }

    #endregion

    #region Stateful

    [Test]
    public async Task StatefulFilterNeverPrunes()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _IndexShapes(stack);
        Filter warm = FilterTestHelper.CompileOrThrow("udp", stack);
        _ = _Verdicts(warm, index, 6);
        Filter stateful = FilterTestHelper.CompileOrThrow("udp && flank(udp.srcport, changed, within: 2packets)", stack);

        string verdicts = _Verdicts(stateful, index, 6);
        bool built = stateful.TryBuildCandidates(index, out _);

        await Assert.That(verdicts).IsEqualTo("nnnnnn");
        await Assert.That(built).IsFalse();
    }

    #endregion
}
