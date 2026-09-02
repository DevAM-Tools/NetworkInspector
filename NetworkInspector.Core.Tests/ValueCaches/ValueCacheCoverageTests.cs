// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-path coverage for ValueCache public and internal APIs not hit by scenario tests.</summary>
internal sealed class ValueCacheCoverageTests
{
    #region Helpers

    private static (Stack Stack, ValueCacheExerciseProtocol Proto, ProtocolId ProtoId, Packet Packet) _Parse()
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
        return (stack, proto, protoId, packet);
    }

    #endregion

    #region Construction

    [Test]
    public async Task Ctor_DuplicateCustomText_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            await Assert.That(() => new ValueCache(
                stack,
                [
                    new ValueCacheFieldConfig(proto.NumberId, RecordValue: false, RecordCustomText: true),
                    new ValueCacheFieldConfig(proto.NumberId, RecordValue: false, RecordCustomText: true),
                ])).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_DuplicateCustomRep_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            await Assert.That(() => new ValueCache(
                stack,
                [
                    new ValueCacheFieldConfig(proto.NumberId, RecordValue: false, RecordCustomRepresentation: true),
                    new ValueCacheFieldConfig(proto.NumberId, RecordValue: false, RecordCustomRepresentation: true),
                ])).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_MaxBytesZero_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            await Assert.That(() => new ValueCache(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId)],
                options: new ValueCacheBuildOptions { Limits = new ValueCacheLimits(null, 0) }))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_GroupAllFlagsFalse_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            await Assert.That(() => new ValueCache(
                stack,
                [],
                [new ValueCacheGroupConfig(proto.NumberGroupId, RecordValue: false)]))
                .Throws<ArgumentException>();
        }
    }

    #endregion

    #region Recording and readers

    [Test]
    public async Task RecordPacket_AllUnmanagedTypes_AndReaderView()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [
                    new ValueCacheFieldConfig(proto.NumberId),
                    new ValueCacheFieldConfig(proto.BoolId),
                    new ValueCacheFieldConfig(proto.I64Id),
                    new ValueCacheFieldConfig(proto.F64Id),
                    new ValueCacheFieldConfig(proto.MacId),
                    new ValueCacheFieldConfig(proto.Ipv4Id),
                    new ValueCacheFieldConfig(proto.Eui64Id),
                    new ValueCacheFieldConfig(proto.TimestampId),
                    new ValueCacheFieldConfig(proto.NoneId),
                    new ValueCacheFieldConfig(proto.StringId),
                    new ValueCacheFieldConfig(proto.BytesId),
                    new ValueCacheFieldConfig(proto.Ipv6Id),
                    new ValueCacheFieldConfig(proto.UuidId),
                    new ValueCacheFieldConfig(proto.NumberId, RecordValue: false, RecordCustomText: true),
                ]);
            cache.RecordPacket(packet);

            _ = cache.Stack;
            _ = cache.RecordAllFields;
            _ = cache.IsMaterializationIncomplete;
            _ = cache.ByteSize;
            ValueCacheSeries<byte> bools = cache.GetSeries<byte>(proto.BoolId);
            _ = bools.FieldId;
            _ = bools.FieldType;
            _ = bools.CaptureMode;
            _ = bools.ByteSize;
            bool gotPid = bools.TryGetPacketIdChunk(0, bools.Count, out ReadOnlySpan<int> pidSpan);
            bool gotTs = bools.TryGetTimestampChunk(0, bools.Count, out ReadOnlySpan<long> tsSpan);
            int pidLen = gotPid ? pidSpan.Length : 0;
            int tsLen = gotTs ? tsSpan.Length : 0;

            await Assert.That(cache.GetSeries<byte>(proto.BoolId)[0].Value).IsEqualTo((byte)1);
            await Assert.That(cache.GetSeries<long>(proto.I64Id)[0].Value).IsEqualTo(-7L);
            await Assert.That(cache.GetSeries<double>(proto.F64Id)[0].Value).IsEqualTo(1.5);
            await Assert.That(cache.GetSeries<ulong>(proto.MacId)[0].Value).IsEqualTo(0xAABBCCDDEEFFUL);
            await Assert.That(cache.GetSeries<uint>(proto.Ipv4Id)[0].Value).IsEqualTo(0xC0A80101u);
            await Assert.That(cache.GetSeries<ulong>(proto.Eui64Id)[0].Value).IsEqualTo(0x1122334455667788UL);
            await Assert.That(cache.GetSeries<long>(proto.TimestampId)[0].Value).IsEqualTo(123L);
            await Assert.That(cache.GetSeries<byte>(proto.NoneId).Count).IsEqualTo(1);
            await Assert.That(pidLen).IsGreaterThan(0);
            await Assert.That(tsLen).IsGreaterThan(0);

            ValueCacheIPv6Series ip6 = cache.GetIPv6Series(proto.Ipv6Id);
            _ = ip6.FieldId;
            _ = ip6.FieldType;
            _ = ip6.CaptureMode;
            _ = ip6.ByteSize;
            _ = ip6[0];
            bool gotH = ip6.TryGetHighChunk(0, ip6.Count, out _);
            bool gotL = ip6.TryGetLowChunk(0, ip6.Count, out _);
            bool gotIp6Pid = ip6.TryGetPacketIdChunk(0, 1, out _);
            bool gotIp6Ts = ip6.TryGetTimestampChunk(0, 1, out _);
            await Assert.That(gotH && gotL && gotIp6Pid && gotIp6Ts).IsTrue();

            ValueCacheUuidSeries uuid = cache.GetUuidSeries(proto.UuidId);
            _ = uuid.FieldId;
            _ = uuid.FieldType;
            _ = uuid.CaptureMode;
            _ = uuid.ByteSize;
            _ = uuid[0];
            _ = uuid.TryGetHighChunk(0, 1, out _);
            _ = uuid.TryGetPacketIdChunk(0, 1, out _);
            _ = uuid.TryGetTimestampChunk(0, 1, out _);

            ValueCacheBytesSeries bytes = cache.GetBytesSeries(proto.BytesId);
            _ = bytes.FieldId;
            _ = bytes.FieldType;
            _ = bytes.CaptureMode;
            _ = bytes.ByteSize;
            _ = bytes.GetPacketId(0);
            _ = bytes.GetTimestampNanos(0);
            _ = bytes.TryGetDataChunk(0, 1, out _);
            _ = bytes.TryGetRefChunk(0, 1, out _);
            _ = bytes.TryGetPacketIdChunk(0, 1, out _);
            _ = bytes.TryGetTimestampChunk(0, 1, out _);
            _ = bytes.TryGetAsBytes(-1, out _);

            foreach (ValueCacheSeries facade in cache.Series)
            {
                if (facade is ValueCacheStringSeries strings)
                {
                    _ = strings.FieldId;
                    _ = strings.FieldType;
                    _ = strings.CaptureMode;
                    _ = strings.ByteSize;
                    _ = strings.TryGetRefChunk(0, strings.Count, out _);
                    _ = strings.TryGetPacketIdChunk(0, strings.Count, out _);
                    _ = strings.TryGetTimestampChunk(0, strings.Count, out _);
                    if (strings.Count > 0)
                    {
                        _ = strings.GetPacketId(0);
                        _ = strings.GetTimestampNanos(0);
                    }

                    _ = strings.TryGetAsString(-1, out _);
                }
            }

            ValueCacheReaderView view = cache.AsReadOnlyView();
            _ = view.Source;
            _ = view.IsAbandoned;
            _ = view.Stack;
            _ = view.RecordAllFields;
            _ = view.PacketIdsStrictlyIncreasing;
            _ = view.TimestampsStrictlyIncreasing;
            _ = view.IsCapacityReached;
            _ = view.IsMaterializationIncomplete;
            _ = view.ByteSize;
            _ = view.Series;
            _ = view.GetSeries<ulong>(proto.NumberId);
            _ = view.TryGetSeries<ulong>(proto.NumberId, out _);
            _ = view.TryGetSeries<ulong>("vcx.num", out _);
            _ = view.TryGetCustomTextSeries(proto.NumberId, out _);
            _ = view.TryGetCustomTextSeries("vcx.num", out _);
            _ = view.TryGetCustomRepresentationSeries(proto.NumberId, out _);
            _ = view.TryGetCustomRepresentationSeries("missing", out _);
            _ = view.TryGetIPv6Series(proto.Ipv6Id, out _);
            _ = view.TryGetIPv6Series("vcx.ip6", out _);
            _ = view.GetIPv6Series(proto.Ipv6Id);
            _ = view.TryGetUuidSeries(proto.UuidId, out _);
            _ = view.TryGetUuidSeries("vcx.uuid", out _);
            _ = view.GetUuidSeries(proto.UuidId);
            _ = view.TryGetBytesSeries(proto.BytesId, out _);
            _ = view.TryGetBytesSeries("vcx.bytes", out _);
            _ = view.GetBytesSeries(proto.BytesId);
            await Assert.That(view.TryGetSeries<ulong>("no.such", out _)).IsFalse();
        }
    }

    [Test]
    public async Task Getters_MissingSeries_ThrowOrFalse()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);

            await Assert.That(() => cache.GetCustomTextSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.GetCustomRepresentationSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.GetIPv6Series(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.GetUuidSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.GetBytesSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(cache.TryGetSeries<ulong>((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<ulong>("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetCustomTextSeries("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetCustomRepresentationSeries("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetIPv6Series("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetUuidSeries("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetBytesSeries("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetIPv6Series(proto.NumberId, out _)).IsFalse();
            await Assert.That(cache.TryGetUuidSeries(proto.NumberId, out _)).IsFalse();
            await Assert.That(cache.TryGetBytesSeries(proto.NumberId, out _)).IsFalse();
        }
    }

    [Test]
    public async Task Tee_AndCustomText_ExitPaths()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.LastOccurrence, RecordValue: true, RecordCustomText: true)]);
            cache.BeginPacket(0, 1);
            cache.Tee(new FieldId(50_000), FieldValue.NewU64(1), default);
            cache.Tee(proto.StringId, FieldValue.NewU64(1), default);
            cache.Tee(proto.NumberId, FieldValue.NewU64(9), new LazyString("t1"));
            cache.TeeCustomText(new FieldId(50_000), default);
            cache.TeeCustomText(proto.StringId, default);
            cache.TeeCustomText(proto.NumberId, default);
            cache.EndPacket();
            await Assert.That(cache.GetCustomTextSeries(proto.NumberId).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task MaxBytes_StopsRecording()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId)],
                options: new ValueCacheBuildOptions { Limits = new ValueCacheLimits(null, 8) });
            cache.RecordPacket(packet);
            await Assert.That(cache.IsCapacityReached).IsTrue();
        }
    }

    [Test]
    public async Task Indexer_IPv6AndUuid_OutOfRange_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.Ipv6Id), new ValueCacheFieldConfig(proto.UuidId)]);
            cache.RecordPacket(packet);
            await Assert.That(() => _ = cache.GetIPv6Series(proto.Ipv6Id)[99]).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = cache.GetUuidSeries(proto.UuidId)[99]).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = cache.GetBytesSeries(proto.BytesId)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task StringAndBytes_Getters_OutOfRange_Throw()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.StringId), new ValueCacheFieldConfig(proto.BytesId)]);
            cache.RecordPacket(packet);
            ValueCacheStringSeries strings = (ValueCacheStringSeries)cache.Series[0];
            ValueCacheBytesSeries bytes = cache.GetBytesSeries(proto.BytesId);
            await Assert.That(() => _ = strings.GetPacketId(99)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = bytes.GetPacketId(99)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = strings.GetTimestampNanos(99)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = bytes.GetTimestampNanos(99)).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task TryGetPublishedChunk_PastStart_AndMissingInnerChunk()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        store.Set(0, 1);
        bool past = store.TryGetPublishedChunk(1, 1, out _);
        bool missing = store.TryGetPublishedChunk(1, 32, out _);
        await Assert.That(past).IsFalse();
        await Assert.That(missing).IsFalse();
    }

    [Test]
    public async Task LastOccurrence_Overwrite_AndFirstOccurrenceSkip()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.LastOccurrence)]);
            cache.BeginPacket(0, 1);
            cache.Tee(proto.NumberId, FieldValue.NewU64(1), default);
            cache.Tee(proto.NumberId, FieldValue.NewU64(2), default);
            cache.EndPacket();
            await Assert.That(cache.GetSeries<ulong>(proto.NumberId)[0].Value).IsEqualTo(2UL);

            ValueCache first = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence)]);
            first.BeginPacket(0, 1);
            first.Tee(proto.NumberId, FieldValue.NewU64(1), default);
            first.Tee(proto.NumberId, FieldValue.NewU64(2), default);
            first.EndPacket();
            await Assert.That(first.GetSeries<ulong>(proto.NumberId)[0].Value).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task CustomText_LastOccurrence_OverwriteThenNullRetract()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.LastOccurrence, RecordValue: false, RecordCustomText: true)]);
            cache.BeginPacket(0, 1);
            cache.Tee(proto.NumberId, FieldValue.NewU64(1), new LazyString("a"));
            cache.Tee(proto.NumberId, FieldValue.NewU64(1), new LazyString("b"));
            cache.EndPacket();
            ValueCacheStringSeries series = cache.GetCustomTextSeries(proto.NumberId);
            _ = series.TryGetAsString(0, out string text);
            await Assert.That(text).IsEqualTo("b");
        }
    }

    [Test]
    public async Task TryGetPublishedChunk_ShiftOverflow_ReturnsFalse()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 12);
        store.Set(0, 1);
        await Assert.That(store.TryGetPublishedChunk(1 << 20, 1, out _)).IsFalse();
    }

    [Test]
    public async Task Bytes_Empty_AndLastOccurrenceOverwrite()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.BytesId, ValueCaptureMode.LastOccurrence)]);
            cache.BeginPacket(0, 1);
            cache.Tee(proto.BytesId, FieldValue.NewBytes(ReadOnlyMemory<byte>.Empty), default);
            cache.Tee(proto.BytesId, FieldValue.NewBytes(new byte[] { 9, 8, 7 }), default);
            cache.EndPacket();
            ValueCacheBytesSeries series = cache.GetBytesSeries(proto.BytesId);
            bool got = series.TryGetAsBytes(0, out ReadOnlyMemory<byte> payload);
            await Assert.That(got).IsTrue();
            await Assert.That(payload.ToArray()).IsEquivalentTo(new byte[] { 9, 8, 7 });
        }
    }

    [Test]
    public async Task String_StageNullLazyString_IsNoOp()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.StringId)]);
            cache.BeginPacket(0, 1);
            ValueCacheStringSeries strings = (ValueCacheStringSeries)cache.Series[0];
            strings.Stage(0, 1, default(LazyString));
            cache.EndPacket();
            await Assert.That(strings.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ReaderView_Getters_ThrowWhenMissing()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [
                    new ValueCacheFieldConfig(proto.NumberId, RecordValue: true, RecordCustomText: true),
                ]);
            cache.RecordPacket(packet);
            ValueCacheReaderView view = cache.AsReadOnlyView();
            _ = view.GetSeries<ulong>(proto.NumberId);
            _ = view.GetCustomTextSeries(proto.NumberId);
            await Assert.That(() => view.GetCustomRepresentationSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => view.GetIPv6Series(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => view.GetUuidSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => view.GetBytesSeries(proto.NumberId)).Throws<ArgumentException>();
            ValueCacheReaderView unset = default;
            await Assert.That(unset.Source).IsNull();
            await Assert.That(unset.IsAbandoned).IsFalse();
        }
    }

    [Test]
    public async Task TryGetValueChunk_AndCustomTextNameMiss()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            bool got = series.TryGetValueChunk(0, series.Count, out _);
            await Assert.That(got).IsTrue();
            await Assert.That(cache.TryGetCustomTextSeries((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetCustomRepresentationSeries((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetIPv6Series((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetUuidSeries((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetBytesSeries((string?)null!, out _)).IsFalse();
        }
    }

    #endregion
}
