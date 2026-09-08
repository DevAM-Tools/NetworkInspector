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
    public async Task RecordPacket_AllUnmanagedTypes_AndReadOnlyView()
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
            _ = cache.ChunkShift;
            ValueCacheSeries<byte> bools = cache.GetSeries<byte>(proto.BoolId);
            _ = bools.FieldId;
            _ = bools.FieldType;
            _ = bools.CaptureMode;
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

            ValueCacheSeries<IPv6Address> ip6 = cache.GetSeries<IPv6Address>(proto.Ipv6Id);
            _ = ip6.FieldId;
            _ = ip6.FieldType;
            _ = ip6.CaptureMode;
            _ = ip6[0];
            bool gotIp6Value = ip6.TryGetValueChunk(0, ip6.Count, out _);
            bool gotIp6Pid = ip6.TryGetPacketIdChunk(0, 1, out _);
            bool gotIp6Ts = ip6.TryGetTimestampChunk(0, 1, out _);
            await Assert.That(gotIp6Value && gotIp6Pid && gotIp6Ts).IsTrue();

            ValueCacheSeries<Uuid> uuid = cache.GetSeries<Uuid>(proto.UuidId);
            _ = uuid.FieldId;
            _ = uuid.FieldType;
            _ = uuid.CaptureMode;
            _ = uuid[0];
            _ = uuid.TryGetValueChunk(0, 1, out _);
            _ = uuid.TryGetPacketIdChunk(0, 1, out _);
            _ = uuid.TryGetTimestampChunk(0, 1, out _);

            ValueCacheSeries<byte[]> bytes = cache.GetSeries<byte[]>(proto.BytesId);
            _ = bytes.FieldId;
            _ = bytes.FieldType;
            _ = bytes.CaptureMode;
            _ = bytes[0].PacketId;
            _ = bytes[0].TimestampNanos;
            _ = bytes[0].Value;
            _ = bytes.TryGetValueChunk(0, 1, out _);
            _ = bytes.TryGetPacketIdChunk(0, 1, out _);
            _ = bytes.TryGetTimestampChunk(0, 1, out _);

            foreach (ValueCacheSeries facade in cache.Series)
            {
                if (facade is ValueCacheSeries<string> strings)
                {
                    _ = strings.FieldId;
                    _ = strings.FieldType;
                    _ = strings.CaptureMode;
                    _ = strings.TryGetValueChunk(0, strings.Count, out _);
                    _ = strings.TryGetPacketIdChunk(0, strings.Count, out _);
                    _ = strings.TryGetTimestampChunk(0, strings.Count, out _);
                    if (strings.Count > 0)
                    {
                        _ = strings[0].PacketId;
                        _ = strings[0].TimestampNanos;
                        _ = strings[0].Value;
                    }
                }
            }

            ReadOnlyValueCache view = cache.AsReadOnlyView();
            _ = view.IsAbandoned;
            _ = view.Stack;
            _ = view.RecordAllFields;
            _ = view.PacketIdsStrictlyIncreasing;
            _ = view.TimestampsStrictlyIncreasing;
            _ = view.IsMaterializationIncomplete;
            _ = view.Series;
            _ = view.GetSeries<ulong>(proto.NumberId);
            _ = view.TryGetSeries<ulong>(proto.NumberId, out _);
            _ = view.TryGetSeries<ulong>("vcx.num", out _);
            _ = view.TryGetCustomTextSeries(proto.NumberId, out _);
            _ = view.TryGetCustomTextSeries("vcx.num", out _);
            _ = view.TryGetCustomRepresentationSeries(proto.NumberId, out _);
            _ = view.TryGetCustomRepresentationSeries("missing", out _);
            _ = view.TryGetSeries<IPv6Address>(proto.Ipv6Id, out _);
            _ = view.TryGetSeries<IPv6Address>("vcx.ip6", out _);
            _ = view.GetSeries<IPv6Address>(proto.Ipv6Id);
            _ = view.TryGetSeries<Uuid>(proto.UuidId, out _);
            _ = view.TryGetSeries<Uuid>("vcx.uuid", out _);
            _ = view.GetSeries<Uuid>(proto.UuidId);
            _ = view.TryGetSeries<byte[]>(proto.BytesId, out _);
            _ = view.TryGetSeries<byte[]>("vcx.bytes", out _);
            _ = view.GetSeries<byte[]>(proto.BytesId);
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
            await Assert.That(() => cache.GetSeries<IPv6Address>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.GetSeries<Uuid>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.GetSeries<byte[]>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(cache.TryGetSeries<ulong>((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<ulong>("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetCustomTextSeries("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetCustomRepresentationSeries("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<IPv6Address>("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<Uuid>("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<byte[]>("missing", out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<IPv6Address>(proto.NumberId, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<Uuid>(proto.NumberId, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<byte[]>(proto.NumberId, out _)).IsFalse();
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
            await Assert.That(() => _ = cache.GetSeries<IPv6Address>(proto.Ipv6Id)[99]).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = cache.GetSeries<Uuid>(proto.UuidId)[99]).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = cache.GetSeries<byte[]>(proto.BytesId)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task StringAndBytes_Indexer_OutOfRange_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.StringId), new ValueCacheFieldConfig(proto.BytesId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<string> strings = cache.GetSeries<string>(proto.StringId);
            ValueCacheSeries<byte[]> bytes = cache.GetSeries<byte[]>(proto.BytesId);
            await Assert.That(() => _ = strings[99]).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => _ = bytes[99]).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task TryGetPublishedChunk_PastStart_AndMissingInnerChunk()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        store.Append(1);
        bool past = store.TryGetPublishedChunk(1, 1, out _);
        bool missing = store.TryGetPublishedChunk(1, 32, out _);
        await Assert.That(past).IsFalse();
        await Assert.That(missing).IsFalse();
    }

    [Test]
    public async Task Record_FirstOccurrence_SkipsSecond_AllStoresBoth()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache first = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence)]);
            first.Record(0, 1, proto.NumberId, FieldValue.NewU64(1), default);
            first.Record(0, 1, proto.NumberId, FieldValue.NewU64(2), default);
            await Assert.That(first.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
            await Assert.That(first.GetSeries<ulong>(proto.NumberId)[0].Value).IsEqualTo(1UL);

            ValueCache all = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
            all.Record(0, 1, proto.NumberId, FieldValue.NewU64(1), default);
            all.Record(0, 1, proto.NumberId, FieldValue.NewU64(2), default);
            await Assert.That(all.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
            await Assert.That(all.GetSeries<ulong>(proto.NumberId)[1].Value).IsEqualTo(2UL);
        }
    }

    [Test]
    public async Task TryGetPublishedChunk_ShiftOverflow_ReturnsFalse()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 12);
        store.Append(1);
        await Assert.That(store.TryGetPublishedChunk(1 << 20, 1, out _)).IsFalse();
    }

    [Test]
    public async Task Record_Bytes_EmptyAndAllOccurrences_StoresBoth()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.BytesId, ValueCaptureMode.AllOccurrences)]);
            cache.Record(0, 1, proto.BytesId, FieldValue.NewBytes(ReadOnlyMemory<byte>.Empty), default);
            cache.Record(0, 1, proto.BytesId, FieldValue.NewBytes(new byte[] { 9, 8, 7 }), default);
            ValueCacheSeries<byte[]> series = cache.GetSeries<byte[]>(proto.BytesId);
            await Assert.That(series.Count).IsEqualTo(2);
            await Assert.That(series[0].Value).IsEquivalentTo(Array.Empty<byte>());
            await Assert.That(series[1].Value).IsEquivalentTo(new byte[] { 9, 8, 7 });
        }
    }

    [Test]
    public async Task Record_String_NullLazyString_IsNoOp()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet _) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.StringId)]);
            cache.Record(0, 1, proto.StringId, default, default);
            ValueCacheSeries<string> strings = cache.GetSeries<string>(proto.StringId);
            await Assert.That(strings.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ReadOnlyValueCache_Getters_ThrowWhenMissing()
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
            ReadOnlyValueCache view = cache.AsReadOnlyView();
            _ = view.GetSeries<ulong>(proto.NumberId);
            _ = view.GetCustomTextSeries(proto.NumberId);
            await Assert.That(() => view.GetCustomRepresentationSeries(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => view.GetSeries<IPv6Address>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => view.GetSeries<Uuid>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => view.GetSeries<byte[]>(proto.NumberId)).Throws<ArgumentException>();
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
            await Assert.That(cache.TryGetSeries<IPv6Address>((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<Uuid>((string?)null!, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<byte[]>((string?)null!, out _)).IsFalse();
        }
    }

    #endregion
}
