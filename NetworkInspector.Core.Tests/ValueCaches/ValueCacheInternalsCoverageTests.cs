// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Direct coverage of ValueCache capacity, column state, and series Stage/Commit exits.</summary>
internal sealed class ValueCacheInternalsCoverageTests
{
    #region Helpers

    private static (Stack Stack, ValueCacheExerciseProtocol Proto, Packet Packet) _Parse()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        Stack stack = builder.Build();
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId);
        return (stack, proto, packet);
    }

    #endregion

    #region Capacity and column state

    [Test]
    public async Task Capacity_AndColumnState_DefensiveExits()
    {
        ValueCacheCapacity unlimited = new(ValueCacheLimits.Unlimited);
        unlimited.RemoveStagedRows(0);
        unlimited.RemoveStagedRows(-3);
        unlimited.SubtractBytes(0);
        unlimited.SubtractBytes(-1);
        _ = unlimited.BytesCharged;
        await Assert.That(unlimited.WouldExceedBytes(1)).IsFalse();
        await Assert.That(unlimited.WouldExceedBytes(-5)).IsFalse();

        ValueCacheCapacity bounded = new(new ValueCacheLimits(2, 50));
        bounded.AddBytes(40);
        await Assert.That(bounded.WouldExceedBytes(-1)).IsFalse();
        await Assert.That(bounded.WouldExceedBytes(20)).IsTrue();
        bounded.SubtractBytes(100);
        await Assert.That(bounded.BytesCharged).IsEqualTo(0);

        ValueCacheColumnState state = new(unlimited, ValueCaptureMode.AllOccurrences);
        _ = state.OccurrenceInPacket;
        _ = state.PacketIds;
        _ = state.Timestamps;
        _ = state.Capacity;
        _ = state.StagedCount;
        state.BeginPacket();
        state.RetractLastOccurrenceRow();
        state.CompleteOverwrite(0);
        unlimited.MarkReached();
        await Assert.That(state.TryPrepareStage(out _, out _)).IsFalse();

        long charge = ValueCacheColumnState.ComputeNewRowCharge(0, int.MaxValue, int.MaxValue, int.MaxValue);
        int overflowHeap = ValueCacheColumnState.StringHeapBytes(int.MaxValue);
        await Assert.That(charge).IsGreaterThan(0);
        await Assert.That(overflowHeap).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task ColumnState_LastOccurrenceOverwriteExceedsBytes()
    {
        ValueCacheCapacity cap = new(new ValueCacheLimits(null, 8));
        ValueCacheColumnState state = new(cap, ValueCaptureMode.LastOccurrence);
        state.BeginPacket();
        bool prepared = state.TryPrepareStage(out int index, out bool overwrite);
        await Assert.That(prepared && !overwrite).IsTrue();
        _ = state.TryChargeNewRow(4, 0, 1, out _);
        bool second = state.TryPrepareStage(out _, out bool overwriteSecond);
        await Assert.That(second && overwriteSecond).IsTrue();
        state.CompleteOverwrite(100);
        await Assert.That(cap.IsReached).IsTrue();
        _ = index;
    }

    [Test]
    public async Task ColumnState_AllOccurrences_CapsAtUshortMax()
    {
        ValueCacheCapacity cap = new(ValueCacheLimits.Unlimited);
        ValueCacheColumnState state = new(cap, ValueCaptureMode.AllOccurrences);
        state.BeginPacket();
        for (int i = 0; i < ushort.MaxValue; i++)
        {
            if (!state.TryPrepareStage(out int index, out _))
            {
                break;
            }

            _ = state.TryChargeNewRow(1, 0, 1, out _);
            _ = index;
        }

        await Assert.That(state.TryPrepareStage(out _, out _)).IsFalse();
    }

    [Test]
    public async Task ColumnState_CommitRetractsWhenReachedMidPacket()
    {
        ValueCacheCapacity cap = new(new ValueCacheLimits(1, null));
        ValueCacheColumnState state = new(cap, ValueCaptureMode.AllOccurrences);
        state.BeginPacket();
        _ = state.TryPrepareStage(out _, out _);
        _ = state.TryChargeNewRow(1, 0, 1, out _);
        bool second = state.TryPrepareStage(out _, out _);
        await Assert.That(second).IsFalse();
        state.Commit();
        await Assert.That(state.CommittedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ColumnState_TryChargeNewRow_NegativeAndExceed()
    {
        ValueCacheColumnState negative = new(new ValueCacheCapacity(ValueCacheLimits.Unlimited), ValueCaptureMode.AllOccurrences);
        negative.BeginPacket();
        _ = negative.TryPrepareStage(out _, out _);
        await Assert.That(negative.TryChargeNewRow(-1, 0, 1, out _)).IsFalse();

        ValueCacheCapacity cap = new(new ValueCacheLimits(null, 10));
        ValueCacheColumnState exceed = new(cap, ValueCaptureMode.AllOccurrences);
        exceed.BeginPacket();
        _ = exceed.TryPrepareStage(out _, out _);
        await Assert.That(exceed.TryChargeNewRow(100, 0, 1, out _)).IsFalse();
        await Assert.That(cap.IsReached).IsTrue();
    }

    #endregion

    #region Series Stage paths

    [Test]
    public async Task UnmanagedSeries_Reached_AndChargeOverflow()
    {
        ValueCacheCapacity reached = new(ValueCacheLimits.Unlimited);
        reached.MarkReached();
        ValueCacheSeries<ulong> skipped = new(reached, new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences);
        skipped.BeginPacket();
        skipped.Stage(0, 1, 1UL);
        await Assert.That(skipped.Count).IsEqualTo(0);

        ValueCacheCapacity cap = new(ValueCacheLimits.Unlimited);
        ValueCacheSeries<ulong> series = new(cap, new FieldId(0), FieldType.U64, ValueCaptureMode.LastOccurrence);
        series.BeginPacket();
        series.Stage(0, 1, 1UL);
        series.Stage(0, 1, 2UL);
        series.Commit();
        await Assert.That(series[0].Value).IsEqualTo(2UL);
    }

    [Test]
    public async Task IPv6AndUuid_StageSkipAndLastOccurrenceOverwrite()
    {
        ValueCacheCapacity reached = new(ValueCacheLimits.Unlimited);
        reached.MarkReached();
        ValueCacheIPv6Series ip6Skip = new(reached, new FieldId(0), ValueCaptureMode.FirstOccurrence);
        ip6Skip.BeginPacket();
        ip6Skip.Stage(0, 1, 1, 2);

        ValueCacheUuidSeries uuidSkip = new(reached, new FieldId(0), ValueCaptureMode.FirstOccurrence);
        uuidSkip.BeginPacket();
        uuidSkip.Stage(0, 1, 3, 4);

        ValueCacheCapacity cap = new(ValueCacheLimits.Unlimited);
        ValueCacheIPv6Series ip6 = new(cap, new FieldId(1), ValueCaptureMode.LastOccurrence);
        ip6.BeginPacket();
        ip6.Stage(0, 1, 1, 2);
        ip6.Stage(0, 1, 5, 6);
        ip6.Commit();
        await Assert.That(ip6.Count).IsEqualTo(1);

        ValueCacheUuidSeries uuid = new(cap, new FieldId(2), ValueCaptureMode.LastOccurrence);
        uuid.BeginPacket();
        uuid.Stage(0, 1, 7, 8);
        uuid.Stage(0, 1, 9, 10);
        uuid.Commit();
        await Assert.That(uuid.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BytesSeries_ArenaDedicatedEmptyReachedAndOverwrite()
    {
        ValueCacheCapacity reached = new(ValueCacheLimits.Unlimited);
        reached.MarkReached();
        ValueCacheBytesSeries skip = new(reached, new FieldId(0), ValueCaptureMode.AllOccurrences);
        skip.BeginPacket();
        skip.Stage(0, 1, [1]);

        ValueCacheCapacity cap = new(ValueCacheLimits.Unlimited);
        ValueCacheBytesSeries series = new(cap, new FieldId(1), ValueCaptureMode.AllOccurrences);
        series.BeginPacket();
        series.Stage(0, 1, ReadOnlySpan<byte>.Empty);
        series.Stage(0, 1, [1, 2, 3]);
        byte[] large = new byte[65537];
        large[0] = 42;
        series.Stage(0, 1, large);
        series.Commit();
        bool empty = series.TryGetAsBytes(0, out ReadOnlyMemory<byte> emptyMem);
        bool small = series.TryGetAsBytes(1, out ReadOnlyMemory<byte> smallMem);
        bool huge = series.TryGetAsBytes(2, out ReadOnlyMemory<byte> hugeMem);
        await Assert.That(empty && emptyMem.Length == 0).IsTrue();
        await Assert.That(small && smallMem.Length == 3).IsTrue();
        await Assert.That(huge && hugeMem.Length == 65537 && hugeMem.Span[0] == 42).IsTrue();

        ValueCacheCapacity tight = new(new ValueCacheLimits(null, 8));
        ValueCacheBytesSeries over = new(tight, new FieldId(2), ValueCaptureMode.LastOccurrence);
        over.BeginPacket();
        over.Stage(0, 1, [1]);
        over.Stage(0, 1, [2, 3, 4]);
        over.Commit();
        await Assert.That(tight.IsReached).IsTrue();
    }

    [Test]
    public async Task BytesSeries_OverwriteExceeds_AndNullRef_AndArenaFit()
    {
        ValueCacheCapacity fit = new(ValueCacheLimits.Unlimited);
        ValueCacheBytesSeries twoSmall = new(fit, new FieldId(0), ValueCaptureMode.AllOccurrences);
        twoSmall.BeginPacket();
        twoSmall.Stage(0, 1, [1]);
        twoSmall.Stage(0, 1, [2]);
        twoSmall.Commit();
        await Assert.That(twoSmall.Count).IsEqualTo(2);

        ValueCacheCapacity overwriteCap = new(new ValueCacheLimits(null, 250_000));
        ValueCacheBytesSeries overwrite = new(overwriteCap, new FieldId(1), ValueCaptureMode.LastOccurrence);
        overwrite.BeginPacket();
        overwrite.Stage(0, 1, [1]);
        overwrite.Stage(0, 1, new byte[65536]);
        overwrite.Commit();
        await Assert.That(overwriteCap.IsReached).IsTrue();

        ValueCacheCapacity arenaExceed = new(new ValueCacheLimits(null, 150_000));
        ValueCacheBytesSeries arenaTight = new(arenaExceed, new FieldId(3), ValueCaptureMode.FirstOccurrence);
        arenaTight.BeginPacket();
        arenaTight.Stage(0, 1, [1]);
        arenaTight.Commit();
        await Assert.That(arenaExceed.IsReached).IsTrue();

        ValueCacheCapacity nullCap = new(ValueCacheLimits.Unlimited);
        ValueCacheBytesSeries nullRefs = new(nullCap, new FieldId(2), ValueCaptureMode.FirstOccurrence);
        nullRefs.BeginPacket();
        nullRefs.Stage(0, 1, [9]);
        nullRefs.Commit();
        System.Reflection.FieldInfo refsField = typeof(ValueCacheBytesSeries).GetField("_Refs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ChunkedGrowOnlyStore<byte[]?> refs = (ChunkedGrowOnlyStore<byte[]?>)refsField.GetValue(nullRefs)!;
        refs.Set(0, null);
        await Assert.That(nullRefs.TryGetAsBytes(0, out _)).IsFalse();
    }

    [Test]
    public async Task UnmanagedAndWideSeries_NewRowExceedsBytes()
    {
        ValueCacheLimits tiny = new(null, 8);
        ValueCacheSeries<ulong> numbers = new(new ValueCacheCapacity(tiny), new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences);
        numbers.BeginPacket();
        numbers.Stage(0, 1, 1UL);
        numbers.Commit();

        ValueCacheIPv6Series ip6 = new(new ValueCacheCapacity(tiny), new FieldId(1), ValueCaptureMode.AllOccurrences);
        ip6.BeginPacket();
        ip6.Stage(0, 1, 1, 2);
        ip6.Commit();

        ValueCacheUuidSeries uuid = new(new ValueCacheCapacity(tiny), new FieldId(2), ValueCaptureMode.AllOccurrences);
        uuid.BeginPacket();
        uuid.Stage(0, 1, 3, 4);
        uuid.Commit();
        await Assert.That(numbers.Count + ip6.Count + uuid.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StringSeries_OverwriteExceeds_AndHasStaged()
    {
        ValueCacheCapacity cap = new(ValueCacheLimits.Unlimited);
        ValueCacheStringSeries series = new(cap, new FieldId(0), FieldType.String, ValueCaptureMode.LastOccurrence);
        series.BeginPacket();
        series.Stage(0, 1, "a");
        bool staged = series.HasStagedThisPacket;
        series.Stage(0, 1, new string('x', 4000));
        await Assert.That(staged).IsTrue();
        await Assert.That(series.Count).IsEqualTo(0);
        bool missing = series.TryGetAsString(0, out _);
        await Assert.That(missing).IsFalse();
    }

    [Test]
    public async Task StringSeries_NewRowAndOverwriteExceedBytes_AndNullRef()
    {
        ValueCacheStringSeries tiny = new(
            new ValueCacheCapacity(new ValueCacheLimits(null, 8)),
            new FieldId(0),
            FieldType.String,
            ValueCaptureMode.AllOccurrences);
        tiny.BeginPacket();
        tiny.Stage(0, 1, "a");
        tiny.Commit();
        await Assert.That(tiny.Count).IsEqualTo(0);

        ValueCacheCapacity cap = new(new ValueCacheLimits(null, 90_000));
        ValueCacheStringSeries series = new(cap, new FieldId(1), FieldType.String, ValueCaptureMode.LastOccurrence);
        series.BeginPacket();
        series.Stage(0, 1, "a");
        series.Stage(0, 1, new string('x', 8000));
        series.Commit();
        await Assert.That(cap.IsReached).IsTrue();

        ValueCacheCapacity nullCap = new(ValueCacheLimits.Unlimited);
        ValueCacheStringSeries nullRefs = new(nullCap, new FieldId(2), FieldType.String, ValueCaptureMode.FirstOccurrence);
        nullRefs.BeginPacket();
        nullRefs.Stage(0, 1, "kept");
        nullRefs.Commit();
        System.Reflection.FieldInfo refsField = typeof(ValueCacheStringSeries).GetField("_Refs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ChunkedGrowOnlyStore<string?> refs = (ChunkedGrowOnlyStore<string?>)refsField.GetValue(nullRefs)!;
        refs.Set(0, null);
        await Assert.That(nullRefs.TryGetAsString(0, out _)).IsFalse();
    }

    [Test]
    public async Task ColumnState_RetractNothing_ViaReflection()
    {
        ValueCacheColumnState state = new(new ValueCacheCapacity(ValueCacheLimits.Unlimited), ValueCaptureMode.FirstOccurrence);
        MethodInfo retract = typeof(ValueCacheColumnState).GetMethod(
            "_RetractStagedThisPacket",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = retract.Invoke(state, null);
        await Assert.That(state.CommittedCount).IsEqualTo(0);
    }

    #endregion

    #region ValueCache remaining exits

    [Test]
    public async Task TeeCustomText_NonNull_AndShouldMaterializeNoGroup()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, RecordValue: false, RecordCustomText: true)]);
            cache.BeginPacket(0, 1);
            cache.TeeCustomText(proto.NumberId, new LazyString("hello"));
            cache.EndPacket();
            cache.EnsureMaterialized(packet);
            await Assert.That(cache.GetCustomTextSeries(proto.NumberId).Count).IsEqualTo(1);
            await Assert.That(cache.TryGetSeries<ulong>(proto.StringId, out _)).IsFalse();
            await Assert.That(cache.TryGetIPv6Series(proto.NumberId, out _)).IsFalse();
        }
    }

    [Test]
    public async Task TryGetSeries_StringPayload_TypeMismatchDefaultArm()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.StringId)]);
            cache.RecordPacket(packet);
            await Assert.That(cache.TryGetSeries<ulong>(proto.StringId, out _)).IsFalse();
            await Assert.That(cache.TryGetIPv6Series(proto.UuidId, out _)).IsFalse();
            await Assert.That(cache.TryGetUuidSeries(proto.Ipv6Id, out _)).IsFalse();
            await Assert.That(cache.TryGetBytesSeries(proto.StringId, out _)).IsFalse();
        }
    }

    [Test]
    public async Task CreatePayloadSeries_UnsupportedFieldType_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            MethodInfo method = typeof(ValueCache).GetMethod("_CreatePayloadSeries", BindingFlags.Instance | BindingFlags.NonPublic)!;
            NetworkInspector.Core.Infos.FieldInfo bogus = new(
                new FieldId(0),
                new ProtocolId(0),
                "bogus",
                "bogus",
                (FieldType)255,
                null,
                null);
            try
            {
                _ = method.Invoke(cache, [bogus, ValueCaptureMode.FirstOccurrence]);
                throw new InvalidOperationException("Expected ArgumentException for unsupported FieldType.");
            }
            catch (TargetInvocationException ex)
            {
                await Assert.That(ex.InnerException).IsTypeOf<ArgumentException>();
            }
        }
    }

    [Test]
    public async Task EnsureMaterialized_PassCap_SetsIncomplete()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new() { NestedLazyOnMaterialize = true };
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId);
        ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        cache.BeginPacket(0, 1);
        cache.EnsureMaterialized(packet, 1);
        cache.EndPacket();
        await Assert.That(cache.IsMaterializationIncomplete).IsTrue();
        await Assert.That(() => cache.EnsureMaterialized(packet, 0)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion
}
