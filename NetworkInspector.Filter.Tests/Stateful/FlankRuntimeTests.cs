// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Stateful;

/// <summary>
/// Drives <see cref="FlankRuntime.Advance"/> against the armed-latch / delta catalog without
/// compiling expressions or parsing packets.
/// </summary>
internal sealed class FlankRuntimeTests
{
    #region Helpers

    private static readonly ValueAccessor _DummyAccessor = ValueAccessor.Direct([new FieldId(1)]);

    private static FlankEndpoint _Eq(ulong value) => new(CompareOp.Equal, FieldValueData.NewU64(value));

    private static FlankDelta _By(CompareOp op, long value) => new(op, FieldValueData.NewI64(value));

    private static FlankDelta _ByU64(CompareOp op, ulong value) => new(op, FieldValueData.NewU64(value));

    private static FlankRuntime _ArmedFromTo(int packets, ulong from = 1, ulong to = 2) =>
        new(_DummyAccessor, _Eq(from), _Eq(to), by: null, isAnyChange: false, FlankWindow.FromPackets(packets));

    private static FlankRuntime _ArmedFromToTime(long nanoseconds, ulong from = 1, ulong to = 2) =>
        new(_DummyAccessor, _Eq(from), _Eq(to), by: null, isAnyChange: false, FlankWindow.FromNanoseconds(nanoseconds));

    private static List<bool> _Run(FlankRuntime runtime, params (byte Value, int Id, long Nanos)[] samples)
    {
        List<bool> hits = [];
        foreach ((byte value, int id, long nanos) in samples)
        {
            hits.Add(runtime.Advance(FieldValueData.NewU64(value), nanos, id));
        }

        return hits;
    }

    private static List<bool> _RunSeq(FlankRuntime runtime, params byte[] values)
    {
        (byte Value, int Id, long Nanos)[] samples = new (byte, int, long)[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            samples[i] = (values[i], i, i * 1_000_000L);
        }

        return _Run(runtime, samples);
    }

    #endregion

    #region A — Armed from + to

    [Test]
    public async Task A1_CrossIntermediate()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 1, 3, 3, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task A2_ReArmAfterFire()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 1, 1, 2, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false, true });
    }

    [Test]
    public async Task A3_NoDoubleFireInTo()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 1, 1, 2, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task A4_ExpiryBeforeTo()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(2), 1, 9, 9, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task A5_PromoteNextOnExpiry()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(2), 1, 1, 9, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task A5b_OldestArmStillValid()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(4), 1, 1, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task A6_RelationalUpward()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            new FlankEndpoint(CompareOp.LessThan, FieldValueData.NewU64(10)),
            new FlankEndpoint(CompareOp.GreaterEqual, FieldValueData.NewU64(10)),
            by: null,
            isAnyChange: false,
            FlankWindow.FromNanoseconds(5_000_000_000L));

        await Assert.That(_RunSeq(runtime, 8, 12, 15))
            .IsEquivalentTo(new List<bool> { false, true, false });
    }

    [Test]
    public async Task A6_RelationalDownward()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            new FlankEndpoint(CompareOp.GreaterThan, FieldValueData.NewU64(100)),
            new FlankEndpoint(CompareOp.LessEqual, FieldValueData.NewU64(50)),
            by: null,
            isAnyChange: false,
            FlankWindow.FromNanoseconds(1_000_000_000L));

        await Assert.That(_RunSeq(runtime, 200, 150, 40))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task ArmedEndpoints_OverlappingRegions_AlternateHitsWhileValueStays()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            new FlankEndpoint(CompareOp.LessThan, FieldValueData.NewU64(10)),
            new FlankEndpoint(CompareOp.GreaterEqual, FieldValueData.NewU64(5)),
            by: null,
            isAnyChange: false,
            FlankWindow.FromPackets(10));

        await Assert.That(_RunSeq(runtime, 7, 7, 7, 7))
            .IsEquivalentTo(new List<bool> { false, true, false, true });
    }

    [Test]
    public async Task A7_ToWithoutFrom()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 3, 3, 2, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task A8_ReEntry()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 1, 2, 3, 1, 2))
            .IsEquivalentTo(new List<bool> { false, true, false, false, true });
    }

    [Test]
    public async Task A9_SecondToDoesNotFire()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 1, 3, 2, 3, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false, false });
    }

    [Test]
    public async Task A10_CrossIntermediateFrom64To1()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10, from: 64, to: 1), 64, 2, 1))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    #endregion

    #region B — Expiry

    [Test]
    public async Task B1_PacketWindowExpiryNoFire()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            _Eq(1),
            to: null,
            _ByU64(CompareOp.GreaterEqual, 5),
            isAnyChange: false,
            FlankWindow.FromPackets(2));

        await Assert.That(_RunSeq(runtime, 1, 2, 3, 10))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task B2_ReArmAfterExpiryThenFire()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(2), 1, 9, 9, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false, true });
    }

    [Test]
    public async Task B3_TimeWindowExpiry()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            _Eq(0),
            new FlankEndpoint(CompareOp.GreaterEqual, FieldValueData.NewU64(5)),
            by: null,
            isAnyChange: false,
            FlankWindow.FromNanoseconds(100_000_000L));

        await Assert.That(_Run(runtime, (0, 0, 0), (3, 1, 50_000_000L), (8, 2, 200_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, false });
    }

    [Test]
    public async Task B4_ExpireOnToThatIsNotFrom()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(1), 1, 5, 2))
            .IsEquivalentTo(new List<bool> { false, false, false });
    }

    #endregion

    #region C — Delta

    [Test]
    public async Task C1_PairwiseExact()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.Equal, 2), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 1, 3, 5, 7))
            .IsEquivalentTo(new List<bool> { false, true, true, true });
    }

    [Test]
    public async Task C2_PairwiseAtLeast()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.GreaterEqual, 2), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 1, 2, 4, 5))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task C3_PairwiseAtMostNegative()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _By(CompareOp.LessEqual, -3), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 10, 8, 5, 4))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task C4_PairwiseNotEqualZero()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.NotEqual, 0), isAnyChange: false, FlankWindow.FromNanoseconds(1_000_000_000L));

        await Assert.That(_RunSeq(runtime, 4, 4, 5, 5))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task C5_ArmedExactBy()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, _Eq(1), to: null, _ByU64(CompareOp.Equal, 2), isAnyChange: false, FlankWindow.FromPackets(10));

        await Assert.That(_RunSeq(runtime, 1, 3, 3, 2))
            .IsEquivalentTo(new List<bool> { false, true, false, false });
    }

    [Test]
    public async Task C6_ArmedAtLeast()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, _Eq(0), to: null, _ByU64(CompareOp.GreaterEqual, 2), isAnyChange: false, FlankWindow.FromPackets(10));

        await Assert.That(_RunSeq(runtime, 0, 1, 4, 10))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task C7_ArmedAtLeastAcrossIntermediates()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, _Eq(1), to: null, _ByU64(CompareOp.GreaterEqual, 2), isAnyChange: false, FlankWindow.FromPackets(10));

        await Assert.That(_RunSeq(runtime, 1, 2, 5))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task C8_ArmedFromToBy()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            _Eq(0),
            new FlankEndpoint(CompareOp.GreaterEqual, FieldValueData.NewU64(10)),
            _ByU64(CompareOp.GreaterEqual, 5),
            isAnyChange: false,
            FlankWindow.FromNanoseconds(5_000_000_000L));

        await Assert.That(_RunSeq(runtime, 0, 3, 12))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task C8b_ToMatchesByFailsStayArmed()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            _Eq(0),
            new FlankEndpoint(CompareOp.GreaterEqual, FieldValueData.NewU64(10)),
            _ByU64(CompareOp.GreaterEqual, 50),
            isAnyChange: false,
            FlankWindow.FromNanoseconds(5_000_000_000L));

        await Assert.That(_RunSeq(runtime, 0, 12, 60))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task C9_ArmedDeltaExpiry()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, _Eq(0), to: null, _ByU64(CompareOp.GreaterEqual, 5), isAnyChange: false, FlankWindow.FromPackets(2));

        await Assert.That(_RunSeq(runtime, 0, 2, 3, 10))
            .IsEquivalentTo(new List<bool> { false, false, false, false });
    }

    [Test]
    public async Task C10_PairwiseExactNegative()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _By(CompareOp.Equal, -2), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 8, 6, 4))
            .IsEquivalentTo(new List<bool> { false, true, true });
    }

    [Test]
    public async Task C11_LessEqualIncludesZero()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.LessEqual, 2), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 5, 5, 8))
            .IsEquivalentTo(new List<bool> { false, true, false });
    }

    [Test]
    public async Task C12_PairwiseWindowAdjacent()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.GreaterEqual, 2), isAnyChange: false, FlankWindow.FromPackets(1));

        await Assert.That(_RunSeq(runtime, 1, 5, 10))
            .IsEquivalentTo(new List<bool> { false, true, true });
    }

    [Test]
    public async Task C13_PairwiseWindowMiss()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.GreaterEqual, 2), isAnyChange: false, FlankWindow.FromPackets(1));

        await Assert.That(_Run(runtime, (1, 0, 0), (5, 2, 0)))
            .IsEquivalentTo(new List<bool> { false, false });
    }

    [Test]
    public async Task C14_GreaterThanZero()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.GreaterThan, 0), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 3, 3, 4))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task C14_LessThanZero()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.LessThan, 0), isAnyChange: false, FlankWindow.FromPackets(5));

        await Assert.That(_RunSeq(runtime, 4, 4, 3))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task ArmedBy_InclusiveZeroDelta_DoesNotRearmOnFiringPacket()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            _Eq(0),
            to: null,
            _By(CompareOp.LessEqual, 2),
            isAnyChange: false,
            FlankWindow.FromPackets(10));

        await Assert.That(_RunSeq(runtime, 0, 0, 0, 0))
            .IsEquivalentTo(new List<bool> { false, true, false, true });
    }

    #endregion

    #region D — Pairwise unchanged

    [Test]
    public async Task D1_ArrivalCrossesIntermediates()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, _Eq(2), by: null, isAnyChange: false, FlankWindow.FromPackets(10));

        await Assert.That(_RunSeq(runtime, 1, 3, 3, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task D2_ArrivalReEntry()
    {
        FlankRuntime runtime = new(
            _DummyAccessor,
            from: null,
            new FlankEndpoint(CompareOp.LessThan, FieldValueData.NewU64(64)),
            by: null,
            isAnyChange: false,
            FlankWindow.FromNanoseconds(1_000_000_000L));

        await Assert.That(_RunSeq(runtime, 64, 63, 62, 64, 63))
            .IsEquivalentTo(new List<bool> { false, true, false, false, true });
    }

    [Test]
    public async Task D3_Departure()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, _Eq(64), to: null, by: null, isAnyChange: false, FlankWindow.FromNanoseconds(1_000_000_000L));

        await Assert.That(_RunSeq(runtime, 64, 63, 62, 64))
            .IsEquivalentTo(new List<bool> { false, true, false, false });
    }

    [Test]
    public async Task D4_AnyChange()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, by: null, isAnyChange: true, FlankWindow.FromNanoseconds(1_000_000_000L));

        await Assert.That(_RunSeq(runtime, 64, 64, 63, 63, 62))
            .IsEquivalentTo(new List<bool> { false, false, true, false, true });
    }

    #endregion

    #region H — Promote and non-monotonic time

    [Test]
    public async Task H1_PromoteNextWhenArmExpires()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(2), 1, 1, 9, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task H2_FireClearsNext()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(10), 1, 1, 2, 2))
            .IsEquivalentTo(new List<bool> { false, false, true, false });
    }

    [Test]
    public async Task H3_TwoSlotLimitDropsThirdFrom()
    {
        await Assert.That(_RunSeq(_ArmedFromTo(2), 1, 1, 1, 1, 2))
            .IsEquivalentTo(new List<bool> { false, false, false, false, false });
    }

    [Test]
    public async Task H4_EarlierTimestampBecomesArm()
    {
        FlankRuntime runtime = _ArmedFromToTime(10_000_000_000L);

        await Assert.That(_Run(
                runtime,
                (1, 0, 100_000_000_000L),
                (1, 1, 50_000_000_000L),
                (2, 2, 105_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    [Test]
    public async Task H5_BackwardsTimeKeepsArm()
    {
        FlankRuntime runtime = _ArmedFromToTime(10_000_000_000L);

        await Assert.That(_Run(
                runtime,
                (1, 0, 100_000_000_000L),
                (2, 1, 90_000_000_000L),
                (2, 2, 105_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, true });
    }

    #endregion

    #region Reset

    [Test]
    public async Task Reset_DropsArmSoLaterToDoesNotFire()
    {
        FlankRuntime runtime = _ArmedFromTo(10);
        _ = runtime.Advance(FieldValueData.NewU64(1), 0, 0);
        runtime.Reset();

        await Assert.That(runtime.Advance(FieldValueData.NewU64(2), 1_000_000L, 1)).IsFalse();
    }

    [Test]
    public async Task Reset_DropsNextSoStayInToDoesNotFire()
    {
        FlankRuntime runtime = _ArmedFromTo(10);
        _ = runtime.Advance(FieldValueData.NewU64(1), 0, 0);
        _ = runtime.Advance(FieldValueData.NewU64(1), 1_000_000L, 1);
        runtime.Reset();

        await Assert.That(runtime.Advance(FieldValueData.NewU64(2), 2_000_000L, 2)).IsFalse();
    }

    [Test]
    public async Task TimeWindow_ThirdFromEarlierThanArm_DisplacesBothSlots()
    {
        FlankRuntime runtime = _ArmedFromToTime(10_000_000_000L);

        await Assert.That(_Run(
                runtime,
                (1, 0, 100_000_000_000L),
                (1, 1, 80_000_000_000L),
                (1, 2, 70_000_000_000L),
                (2, 3, 75_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task TimeWindow_ThirdFromNewerThanBoth_IsIgnored()
    {
        FlankRuntime runtime = _ArmedFromToTime(10_000_000_000L);

        await Assert.That(_Run(
                runtime,
                (1, 0, 100_000_000_000L),
                (1, 1, 105_000_000_000L),
                (1, 2, 108_000_000_000L),
                (2, 3, 109_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task TimeWindow_ThirdFromBetweenArmAndNext_DisplacesNext()
    {
        FlankRuntime runtime = _ArmedFromToTime(10_000_000_000L);

        await Assert.That(_Run(
                runtime,
                (1, 0, 100_000_000_000L),
                (1, 1, 50_000_000_000L),
                (1, 2, 80_000_000_000L),
                (2, 3, 90_000_000_000L)))
            .IsEquivalentTo(new List<bool> { false, false, false, true });
    }

    [Test]
    public async Task PairwiseBy_ValueTooLargeForI64_DoesNotFire()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.Equal, 1), isAnyChange: false, FlankWindow.FromPackets(5));

        bool first = runtime.Advance(FieldValueData.NewU64(ulong.MaxValue), 0, 0);
        bool second = runtime.Advance(FieldValueData.NewU64(0), 1_000_000L, 1);

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task PairwiseBy_NonIntegerSample_DoesNotFire()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.Equal, 1), isAnyChange: false, FlankWindow.FromPackets(5));

        bool first = runtime.Advance(FieldValueData.NewF64(1.0), 0, 0);
        bool second = runtime.Advance(FieldValueData.NewF64(2.0), 1_000_000L, 1);

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task PairwiseBy_CheckedSubtractOverflow_DoesNotFire()
    {
        FlankRuntime runtime = new(
            _DummyAccessor, from: null, to: null, _ByU64(CompareOp.Equal, 0), isAnyChange: false, FlankWindow.FromPackets(5));

        bool first = runtime.Advance(FieldValueData.NewI64(1), 0, 0);
        bool second = runtime.Advance(FieldValueData.NewI64(long.MinValue), 1_000_000L, 1);

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsFalse();
    }

    #endregion
}
