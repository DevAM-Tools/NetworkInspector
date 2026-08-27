// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Covers stateful <c>flank(…)</c> edge detection across packet sequences.</summary>
internal sealed class FilterFlankTests
{
    #region Helpers

    /// <summary>
    /// Feeds one packet per TTL value, one millisecond apart, and returns the verdict sequence.
    /// </summary>
    private static List<bool> _Run(Filter filter, Stack stack, params byte[] timeToLiveValues)
    {
        List<bool> verdicts = [];
        for (int i = 0; i < timeToLiveValues.Length; i++)
        {
            Packet packet = FilterTestHelper.Parse(
                stack,
                FilterTestHelper.BuildUdpFrame(53, 1024, timeToLiveValues[i]),
                i,
                i * 1_000_000L);
            verdicts.Add(FilterTestHelper.MatchOrThrow(filter, packet));
        }

        return verdicts;
    }

    /// <summary>
    /// Feeds packets with explicit PacketIds and timestamps so expiry and non-monotonic clocks
    /// can be tested independently of the 1 ms consecutive helper.
    /// </summary>
    private static List<bool> _RunAt(Filter filter, Stack stack, params (byte Ttl, int Id, long Nanos)[] samples)
    {
        List<bool> verdicts = [];
        foreach ((byte ttl, int id, long nanos) in samples)
        {
            Packet packet = FilterTestHelper.Parse(
                stack,
                FilterTestHelper.BuildUdpFrame(53, 1024, ttl),
                id,
                nanos);
            verdicts.Add(FilterTestHelper.MatchOrThrow(filter, packet));
        }

        return verdicts;
    }

    #endregion

    #region Any change

    [Test]
    public async Task Flank_AnyChange_FiresOnEveryDistinctValue()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);

        List<bool> verdicts = _Run(filter, stack, 64, 64, 63, 63, 62);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, false, true, false, true });
    }

    [Test]
    public async Task Flank_WithoutChangedKeyword_DefaultsToAnyChange()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, within: 1s)", stack);

        List<bool> verdicts = _Run(filter, stack, 64, 63);

        await Assert.That(verdicts[1]).IsTrue();
    }

    [Test]
    public async Task Flank_IsStateful()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);

        await Assert.That(filter.IsStateful).IsTrue();
    }

    [Test]
    public async Task Flank_ResetState_ForgetsPreviousSample()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);

        _ = _Run(filter, stack, 64);
        filter.ResetState();
        List<bool> after = _Run(filter, stack, 63);

        await Assert.That(after[0]).IsFalse();
    }

    [Test]
    public async Task Flank_ResetState_Armed_ForgetsArm()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        _ = _Run(filter, stack, 1);
        filter.ResetState();
        List<bool> after = _Run(filter, stack, 2);

        await Assert.That(after[0]).IsFalse();
    }

    #endregion

    #region Endpoints

    [Test]
    public async Task Flank_ToEndpoint_FiresOnceOnArrival()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, to: < 64, within: 1s)", stack);

        List<bool> verdicts = _Run(filter, stack, 64, 63, 62, 64);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, true, false, false });
    }

    [Test]
    public async Task Flank_ToEndpoint_FiresAgainOnReEntry()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, to: < 64, within: 1s)", stack);

        // Always-store: entry at 63, stay in region at 62, leave at 64, re-enter at 63.
        List<bool> verdicts = _Run(filter, stack, 64, 63, 62, 64, 63);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, true, false, false, true });
    }

    [Test]
    public async Task Flank_FromEndpoint_FiresOnceOnDeparture()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: == 64, within: 1s)", stack);

        List<bool> verdicts = _Run(filter, stack, 64, 63, 62, 64);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, true, false, false });
    }

    [Test]
    public async Task Flank_FromAndToEndpoints_RequireBothSides()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: == 64, to: == 1, within: 1s)", stack);

        // Armed at packet 0 (ttl 64); intermediates do not cancel; fire on ttl 1.
        List<bool> verdicts = _Run(filter, stack, 64, 2, 64, 1);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task Flank_EqualityEndpoints_UseExplicitValues()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 64, to: 63, within: 1s)", stack);

        List<bool> verdicts = _Run(filter, stack, 64, 63, 64);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, true, false });
    }

    #endregion

    #region Armed catalog

    [Test]
    public async Task Flank_Armed_A1_CrossIntermediate()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 3, 3, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task Flank_Armed_A2_ReArmAfterFire()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 2, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false, true });
    }

    [Test]
    public async Task Flank_Armed_A3_NoDoubleFireInTo()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 2, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task Flank_Armed_A4_ExpiryBeforeTo()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 9, 9, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task Flank_Armed_A5_PromoteNextOnExpiry()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 9, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task Flank_Armed_A5b_OldestArmStillValid()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 4packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task Flank_Armed_A6_Relational()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: < 10, to: >= 10, within: 5s)", stack);

        await Assert.That(_Run(filter, stack, 8, 12, 15))
            .IsEquivalentTo(new List<bool> { false, true, false });
    }

    [Test]
    public async Task Flank_Armed_OverlappingRegions_AlternateHitsWhileValueStays()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: < 10, to: >= 5, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 7, 7, 7, 7))
            .IsEquivalentTo(new List<bool> { false, true, false, true });
    }

    [Test]
    public async Task Flank_Armed_A7_ToWithoutFrom()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 3, 3, 2, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task Flank_Armed_A8_ReEntry()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 2, 3, 1, 2))
            .IsEquivalentTo(new List<bool> { false, true, false, false, true });
    }

    [Test]
    public async Task Flank_Armed_A9_SecondToDoesNotFire()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 3, 2, 3, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false, false });
    }

    [Test]
    public async Task Flank_Armed_A10_CrossIntermediateFrom64To1()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 64, to: 1, within: 1s)", stack);

        await Assert.That(_Run(filter, stack, 64, 2, 1))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task Flank_Armed_B1()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, by: >= 5, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 2, 3, 10))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task Flank_Armed_B2()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 9, 9, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false, true });
    }

    [Test]
    public async Task Flank_Armed_B3()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 0, to: >= 5, within: 100ms)", stack);

        await Assert.That(_RunAt(filter, stack, (0, 0, 0), (3, 1, 50_000_000L), (8, 2, 200_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, false });
    }

    [Test]
    public async Task Flank_Armed_B4()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 1packet)", stack);

        await Assert.That(_Run(filter, stack, 1, 5, 2))
            .IsEquivalentTo(new List<bool> { false, false, false });
    }

    [Test]
    public async Task Flank_Armed_H1_PromoteNext()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 9, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task Flank_Armed_H2_FireClearsNext()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 2, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task Flank_Armed_H3_TwoSlotLimit()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 1, 1, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false, false });
    }

    [Test]
    public async Task Flank_Armed_H4_EarlierTimestampBecomesArm()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10s)", stack);

        await Assert.That(_RunAt(
                filter,
                stack,
                (1, 0, 100_000_000_000L),
                (1, 1, 50_000_000_000L),
                (2, 2, 105_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task Flank_Armed_H5_BackwardsTimeKeepsArm()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, to: 2, within: 10s)", stack);

        await Assert.That(_RunAt(
                filter,
                stack,
                (1, 0, 100_000_000_000L),
                (2, 1, 90_000_000_000L),
                (2, 2, 105_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    #endregion

    #region Delta catalog

    [Test]
    public async Task Flank_Delta_C1()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: 2, within: 5packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 3, 5, 7))
            .IsEquivalentTo(new List<bool> { false, true, true, true });
    }

    [Test]
    public async Task Flank_Delta_C2()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: >= 2, within: 5packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 2, 4, 5))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task Flank_Delta_C3()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: <= -3, within: 5packets)", stack);

        await Assert.That(_Run(filter, stack, 10, 8, 5, 4))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task Flank_Delta_C4()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: != 0, within: 1s)", stack);

        await Assert.That(_Run(filter, stack, 4, 4, 5, 5))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task Flank_Delta_C5()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, by: 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 3, 3, 2))
            .IsEquivalentTo(new List<bool> { false, true, false, false });
    }

    [Test]
    public async Task Flank_Delta_C6()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 0, by: >= 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 0, 1, 4, 10))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task Flank_Delta_C7()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 1, by: >= 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 1, 2, 5))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task Flank_Delta_C8()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(ip.ttl, from: 0, to: >= 10, by: >= 5, within: 5s)",
            stack);

        await Assert.That(_Run(filter, stack, 0, 3, 12))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task Flank_Delta_C8b()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(ip.ttl, from: 0, to: >= 10, by: >= 50, within: 5s)",
            stack);

        await Assert.That(_Run(filter, stack, 0, 12, 60))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task Flank_Delta_C9()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 0, by: >= 5, within: 2packets)", stack);

        await Assert.That(_Run(filter, stack, 0, 2, 3, 10))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task Flank_Delta_C10()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: -2, within: 5packets)", stack);

        await Assert.That(_Run(filter, stack, 8, 6, 4))
            .IsEquivalentTo(new List<bool> { false, true, true });
    }

    [Test]
    public async Task Flank_Delta_C11()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: <= 2, within: 5packets)", stack);

        await Assert.That(_Run(filter, stack, 5, 5, 8))
            .IsEquivalentTo(new List<bool> { false, true, false });
    }

    [Test]
    public async Task Flank_Delta_C12()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: >= 2, within: 1packet)", stack);

        await Assert.That(_Run(filter, stack, 1, 5, 10))
            .IsEquivalentTo(new List<bool> { false, true, true });
    }

    [Test]
    public async Task Flank_Delta_C13()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: >= 2, within: 1packet)", stack);

        Packet first = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 1), 0, 0);
        _ = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 1), 1, 0);
        Packet second = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 5), 2, 0);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, first)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, second)).IsFalse();
    }

    [Test]
    public async Task Flank_Delta_C14()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, by: > 0, within: 5packets)", stack);

        await Assert.That(_Run(filter, stack, 3, 3, 4))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task Flank_ArmedBy_InclusiveZeroDelta_DoesNotRearmOnFiringPacket()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, from: 0, by: <= 2, within: 10packets)", stack);

        await Assert.That(_Run(filter, stack, 0, 0, 0, 0))
            .IsEquivalentTo(new List<bool> { false, true, false, true });
    }

    #endregion

    #region Windows

    [Test]
    public async Task Flank_TimeWindow_SuppressesDistantEdges()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1ms)", stack);

        Packet first = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0, 0);
        Packet distant = FilterTestHelper.Parse(
            stack,
            FilterTestHelper.BuildUdpFrame(53, 1024, 63),
            1,
            50_000_000L);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, first)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, distant)).IsFalse();
    }

    [Test]
    public async Task Flank_BackwardsTimestamp_SuppressesTheEdge()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);

        // Packet ids stay in order but the capture timestamps do not, which a merged multi-source
        // capture can produce. A negative elapsed time is outside every fire window.
        Packet first = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0, 500_000_000L);
        Packet earlier = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 63), 1, 0);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, first)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, earlier)).IsFalse();
    }

    [Test]
    public async Task Flank_PacketCountWindow_UsesPacketDistance()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1packet)", stack);

        Packet first = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0, 0);
        Packet next = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 63), 1, 0);
        _ = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 2, 0);
        Packet far = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 62), 3, 0);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, first)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, next)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, far)).IsFalse();
    }

    #endregion

    #region Gate

    [Test]
    public async Task Flank_WhenGate_HidesNonMatchingPackets()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(ip.ttl, changed, within: 1s, when: udp.srcport == 53)",
            stack);

        Packet gated = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 0, 0);
        Packet ignored = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(99, 1024, 1), 1, 1000);
        Packet last = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 64), 2, 2000);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, gated)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, ignored)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, last)).IsFalse();
    }

    [Test]
    public async Task Flank_Gate_E1()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(ip.ttl, from: 1, to: 2, within: 10packets, when: udp.srcport == 53)",
            stack);

        Packet arm = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 1), 0, 0);
        Packet hidden = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(99, 1024, 9), 1, 1_000_000L);
        Packet fire = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 2), 2, 2_000_000L);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, arm)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, hidden)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, fire)).IsTrue();
    }

    [Test]
    public async Task Flank_Gate_E2()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(ip.ttl, from: 1, to: 2, within: 10packets, when: udp.srcport == 53)",
            stack);

        Packet hidden = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(99, 1024, 1), 0, 0);
        Packet arm = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 1), 1, 1_000_000L);
        Packet fire = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024, 2), 2, 2_000_000L);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, hidden)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, arm)).IsFalse();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, fire)).IsTrue();
    }

    [Test]
    public async Task Flank_MissingField_DoesNotAdvanceState()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(tcp.srcport, changed, within: 1s)", stack);

        List<bool> verdicts = _Run(filter, stack, 64, 63);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, false });
    }

    [Test]
    public async Task Flank_MissingField_Armed_DoesNotCreateGhostArm()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("flank(tcp.srcport, from: 1, to: 2, within: 1s)", stack);

        List<bool> udp = _Run(filter, stack, 1, 2);
        Packet tcpTo = FilterTestHelper.Parse(stack, FilterTestHelper.BuildTcpFrame(2, 80), 2, 2_000_000L);

        await Assert.That(udp).IsEquivalentTo(new List<bool> { false, false });
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, tcpTo)).IsFalse();
    }

    #endregion

    #region Combination

    [Test]
    public async Task Flank_CombinesWithStatelessTerms()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "udp.srcport == 53 && flank(ip.ttl, changed, within: 1s)",
            stack);

        List<bool> verdicts = _Run(filter, stack, 64, 63);

        await Assert.That(verdicts).IsEquivalentTo(new List<bool> { false, true });
    }

    [Test]
    public async Task Flank_StatefulFilter_IsNotPruned()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = new(stack);
        _ = FilterTestHelper.ParseIndexed(stack, index, FilterTestHelper.BuildUdpFrame(53, 1024));
        Filter filter = FilterTestHelper.CompileOrThrow("flank(ip.ttl, changed, within: 1s)", stack);

        await Assert.That(filter.TryBuildCandidates(index, out _)).IsFalse();
        await Assert.That(filter.TryIsPresenceCandidate(index, 0, out bool isCandidate)).IsFalse();
        await Assert.That(isCandidate).IsTrue();
    }

    [Test]
    public async Task Flank_ProfilingPattern_StillFiresOncePerSpike()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(udp.srcport, from: < 100, to: >= 200, within: 50packets)",
            stack);

        List<bool> verdicts = [];
        for (int i = 0; i < 100; i++)
        {
            ushort sourcePort = i % 50 == 49 ? (ushort)200 : (ushort)10;
            Packet packet = FilterTestHelper.Parse(
                stack,
                FilterTestHelper.BuildUdpFrame(sourcePort, 1024),
                i,
                i * 1_000_000L);
            verdicts.Add(FilterTestHelper.MatchOrThrow(filter, packet));
        }

        await Assert.That(verdicts[49]).IsTrue();
        await Assert.That(verdicts[99]).IsTrue();
        int hits = 0;
        foreach (bool hit in verdicts)
        {
            if (hit)
            {
                hits++;
            }
        }

        await Assert.That(hits).IsEqualTo(2);
    }

    #endregion
}
