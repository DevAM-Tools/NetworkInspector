// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Covers the match cache, poisoning, state reset and stack rebinding.</summary>
internal sealed class FilterLifecycleTests
{
    #region Match cache

    [Test]
    public async Task Cache_RecordsEvaluatedAndMatchedPackets()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);
        Packet match = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        Packet miss = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(99, 1024), 1);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, match)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, miss)).IsFalse();

        await Assert.That(filter.EvaluatedCount).IsEqualTo(2L);
        await Assert.That(filter.EvaluatedPackets.Contains(0)).IsTrue();
        await Assert.That(filter.EvaluatedPackets.Contains(1)).IsTrue();
        await Assert.That(filter.MatchedPackets.Contains(0)).IsTrue();
        await Assert.That(filter.MatchedPackets.Contains(1)).IsFalse();
    }

    [Test]
    public async Task Cache_ReQuery_ReusesStoredVerdict()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 0);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();

        await Assert.That(filter.EvaluatedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task Cache_UnidentifiedPacket_IsEvaluatedButNotCached()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("eth", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), PacketId.Invalid.Value);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();

        await Assert.That(filter.EvaluatedCount).IsEqualTo(0L);
    }

    [Test]
    public async Task Cache_ReQueryOfStatefulFilter_DoesNotAdvanceState()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);
        Packet first = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0);
        Packet second = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 63), 1);

        _ = FilterTestHelper.MatchOrThrow(filter, first);
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, second)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, second)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, first)).IsFalse();

        await Assert.That(filter.IsPoisoned).IsFalse();
    }

    [Test]
    public async Task Cache_ResetState_ClearsVerdicts()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 0);

        _ = FilterTestHelper.MatchOrThrow(filter, packet);
        filter.ResetState();

        await Assert.That(filter.EvaluatedCount).IsEqualTo(0L);
        await Assert.That(filter.EvaluatedPackets.IsEmpty).IsTrue();
        await Assert.That(filter.MatchedPackets.IsEmpty).IsTrue();
    }

    [Test]
    public async Task TryIsMatch_NullPacket_Throws()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        await Assert.That(() => filter.TryIsMatch(null!, out _, out _)).Throws<ArgumentNullException>();
    }

    #endregion

    #region Poison

    [Test]
    public async Task Poison_OutOfOrderPacketOnStatefulFilter()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);
        Packet later = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0);
        Packet earlier = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 63), 1);

        _ = FilterTestHelper.MatchOrThrow(filter, earlier);
        bool produced = filter.TryIsMatch(later, out bool matched, out FilterError? failure);

        await Assert.That(produced).IsFalse();
        await Assert.That(matched).IsFalse();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.OutOfOrder);
        await Assert.That(filter.IsPoisoned).IsTrue();
        await Assert.That(filter.PoisonError).IsNotNull();
    }

    [Test]
    public async Task Poison_StaysPoisonedUntilReset()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);
        Packet later = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0);
        Packet earlier = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 63), 1);
        Packet fresh = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 62), 2);

        _ = FilterTestHelper.MatchOrThrow(filter, earlier);
        _ = filter.TryIsMatch(later, out _, out _);

        await Assert.That(filter.TryIsMatch(fresh, out _, out FilterError? stillFailing)).IsFalse();
        await Assert.That(stillFailing).IsNotNull();

        filter.ResetState();

        await Assert.That(filter.IsPoisoned).IsFalse();
        await Assert.That(filter.PoisonError).IsNull();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, fresh)).IsFalse();
    }

    [Test]
    public async Task Poison_StatelessFilter_AcceptsOutOfOrderPackets()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);
        Packet later = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        Packet earlier = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 1);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, earlier)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, later)).IsTrue();
        await Assert.That(filter.IsPoisoned).IsFalse();
    }

    #endregion

    #region Derive

    [Test]
    public async Task TryDerive_ProducesIndependentFilterOnNewStack()
    {
        using Stack first = FilterTestHelper.BuildStack();
        using Stack second = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", first);
        Packet packet = FilterTestHelper.Parse(first, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        _ = FilterTestHelper.MatchOrThrow(filter, packet);

        bool derived = filter.TryDerive(second, out Filter? clone, out FilterError? failure);

        await Assert.That(derived).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(clone!.Expression).IsEqualTo(filter.Expression);
        await Assert.That(clone.Stack).IsSameReferenceAs(second);
        await Assert.That(clone.EvaluatedCount).IsEqualTo(0L);

        Packet onSecond = FilterTestHelper.Parse(second, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        await Assert.That(FilterTestHelper.MatchOrThrow(clone, onSecond)).IsTrue();
    }

    [Test]
    public async Task TryDerive_ClearsPoison()
    {
        using Stack first = FilterTestHelper.BuildStack();
        using Stack second = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", first);
        Packet later = FilterTestHelper.Parse(first, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        Packet earlier = FilterTestHelper.Parse(first, FilterTestHelper.BuildUdpFrame(53, 1024), 1);
        _ = FilterTestHelper.MatchOrThrow(filter, earlier);
        _ = filter.TryIsMatch(later, out _, out _);

        bool derived = filter.TryDerive(second, out Filter? clone, out _);

        await Assert.That(derived).IsTrue();
        await Assert.That(filter.IsPoisoned).IsTrue();
        await Assert.That(clone!.IsPoisoned).IsFalse();
    }

    [Test]
    public async Task TryDerive_NullStack_Throws()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);

        await Assert.That(() => filter.TryDerive(null!, out _, out _)).Throws<ArgumentNullException>();
    }

    #endregion
}
