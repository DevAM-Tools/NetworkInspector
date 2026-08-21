// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Covers dependency analysis and presence-index candidate pruning.</summary>
internal sealed class FilterIndexPruneTests
{
    #region Helpers

    /// <summary>Indexes one UDP and one TCP packet and returns the index.</summary>
    private static PacketIndex _BuildIndex(Stack stack)
    {
        PacketIndex index = new(stack);
        _ = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        _ = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildTcpFrame(1024, 80), 1);
        return index;
    }

    #endregion

    #region Pruning

    [Test]
    public async Task Prune_ProtocolPresence_SelectsOnlyPacketsWithThatProtocol()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.Contains(0)).IsTrue();
        await Assert.That(candidates.Contains(1)).IsFalse();
    }

    [Test]
    public async Task PresenceCandidate_Protocol_MatchesLiveIndexWithoutCopy()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        await Assert.That(filter.TryIsPresenceCandidate(index, 0, out bool udpPacket)).IsTrue();
        await Assert.That(udpPacket).IsTrue();
        await Assert.That(filter.TryIsPresenceCandidate(index, 1, out bool tcpPacket)).IsTrue();
        await Assert.That(tcpPacket).IsFalse();

        _ = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024), 2);
        await Assert.That(filter.TryIsPresenceCandidate(index, 2, out bool laterUdp)).IsTrue();
        await Assert.That(laterUdp).IsTrue();
    }

    [Test]
    public async Task PresenceCandidate_ReaderView_UsesGenericReaderWithoutInterfaceCast()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        PacketIndexReaderView view = index.AsReadOnlyView();
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);
        Packet packet = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024), 2);

        await Assert.That(filter.TryIsPresenceCandidate(view, 0, out bool udpPacket)).IsTrue();
        await Assert.That(udpPacket).IsTrue();
        await Assert.That(filter.TryBuildCandidates(view, out RoaringBitmap? candidates)).IsTrue();
        await Assert.That(candidates!.Contains(0)).IsTrue();
        await Assert.That(filter.TryIsMatch(packet, view, out bool matched, out FilterError? failure)).IsTrue();
        await Assert.That(matched).IsTrue();
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task PresenceCandidate_Conjunction_IntersectsWithoutCopy()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp && tcp", stack);

        await Assert.That(filter.TryIsPresenceCandidate(index, 0, out bool packet0)).IsTrue();
        await Assert.That(packet0).IsFalse();
        await Assert.That(filter.TryIsPresenceCandidate(index, 1, out bool packet1)).IsTrue();
        await Assert.That(packet1).IsFalse();
    }

    [Test]
    public async Task Prune_FieldPredicate_UsesFieldBitmap()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("tcp.dstport == 80", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.Contains(0)).IsFalse();
        await Assert.That(candidates.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Prune_Conjunction_IntersectsChildren()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp && tcp", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Prune_Disjunction_UnionsChildren()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp || tcp", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.Contains(0)).IsTrue();
        await Assert.That(candidates.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Prune_ConjunctionWithUnknownSide_KeepsResolvableSide()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp && !tcp", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.Contains(0)).IsTrue();
        await Assert.That(candidates.Contains(1)).IsFalse();
    }

    [Test]
    public async Task Prune_DisjunctionWithUnknownSide_DisablesPruning()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp || !tcp", stack);

        await Assert.That(filter.TryBuildCandidates(index, out _)).IsFalse();
    }

    [Test]
    public async Task Prune_NegationOnly_DisablesPruning()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("!udp", stack);

        await Assert.That(filter.TryBuildCandidates(index, out _)).IsFalse();
    }

    [Test]
    public async Task Prune_BooleanConstant_DisablesPruning()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("true", stack);

        await Assert.That(filter.TryBuildCandidates(index, out _)).IsFalse();
    }

    [Test]
    public async Task Prune_ForeignIndex_IsRejected()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        using Stack other = FilterTestHelper.BuildStack();
        PacketIndex foreignIndex = new(other);
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        await Assert.That(filter.TryBuildCandidates(foreignIndex, out _)).IsFalse();
    }

    [Test]
    public async Task Prune_NullIndex_Throws()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        await Assert.That(() => filter.TryBuildCandidates<PacketIndex>(null!, out _)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Prune_AliasGroup_UnionsMemberFieldBitmaps()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        Filter filter = FilterTestHelper.CompileOrThrow("udp.port == 53", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsTrue();
        await Assert.That(candidates!.Contains(0)).IsTrue();
        await Assert.That(candidates.Contains(1)).IsFalse();
    }

    [Test]
    public async Task Prune_ProtocolNotTrackedByTheIndex_DisablesPruning()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        PartialPacketIndexReader reader = new(
            index,
            FilterTestHelper.ProtocolIdOf(stack, "udp"),
            FieldId.Invalid);
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        await Assert.That(filter.TryBuildCandidates(reader, out _)).IsFalse();
    }

    [Test]
    public async Task Prune_FieldNotTrackedByTheIndex_DisablesPruning()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = _BuildIndex(stack);
        PartialPacketIndexReader reader = new(
            index,
            ProtocolId.Invalid,
            FilterTestHelper.FieldIdOf(stack, "udp.srcport"));
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);

        await Assert.That(filter.TryBuildCandidates(reader, out _)).IsFalse();
    }

    #endregion

    #region Soundness

    [Test]
    public async Task Prune_CandidateSet_IsSupersetOfMatches()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = new(stack);
        List<Packet> packets =
        [
            FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024), 0),
            FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(99, 1024), 1),
            FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildTcpFrame(1024, 80), 2),
        ];
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);

        bool built = filter.TryBuildCandidates(index, out RoaringBitmap? candidates);
        await Assert.That(built).IsTrue();

        foreach (Packet packet in packets)
        {
            bool matched = FilterTestHelper.MatchOrThrow(filter, packet, index);
            if (matched)
            {
                await Assert.That(candidates!.Contains((uint)packet.Id.Value)).IsTrue();
            }
        }
    }

    [Test]
    public async Task Match_WithIndex_EqualsMatchWithoutIndex()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = new(stack);
        Packet indexed = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        Packet plain = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        Filter withIndex = FilterTestHelper.CompileOrThrow("udp && udp.srcport == 53", stack);
        Filter withoutIndex = FilterTestHelper.CompileOrThrow("udp && udp.srcport == 53", stack);

        await Assert.That(FilterTestHelper.MatchOrThrow(withIndex, indexed, index))
            .IsEqualTo(FilterTestHelper.MatchOrThrow(withoutIndex, plain));
    }

    #endregion
}
