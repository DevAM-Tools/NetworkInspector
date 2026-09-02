// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Chunk-span and indexer tests for value-cache series and <see cref="ChunkedGrowOnlyStore{T}.TryGetPublishedChunk"/>.</summary>
internal sealed class ValueCacheSeriesTests
{
    private static (Stack Stack, ValueCacheExerciseProtocol Proto, ProtocolId ProtoId, Packet Packet) _ParseOne()
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

    [Test]
    public async Task TryGetValueChunk_LengthEqualsMinChunkSizeObservedCount()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _ParseOne();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            int observed = series.Count;
            bool got = series.TryGetValueChunk(0, observed, out ReadOnlySpan<ulong> span);
            int spanLength = got ? span.Length : -1;
            await Assert.That(got).IsTrue();
            await Assert.That(spanLength).IsEqualTo(Math.Min(ValueCacheColumnState.ChunkSize, observed));
        }
    }

    [Test]
    public async Task TryGetValueChunk_HostileObservedCount_ClipsToCommittedCount()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _ParseOne();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            bool got = series.TryGetValueChunk(0, int.MaxValue, out ReadOnlySpan<ulong> span);
            int spanLength = got ? span.Length : -1;
            await Assert.That(got).IsTrue();
            await Assert.That(spanLength).IsEqualTo(series.Count);
        }
    }

    [Test]
    public async Task Indexer_OutOfRange_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _ParseOne();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            await Assert.That(() => _ = series[series.Count]).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task TryGetPublishedChunk_EmptyCount_ReturnsFalse()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 12);
        store.Set(0, 7);
        await Assert.That(store.TryGetPublishedChunk(0, 0, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetPublishedChunk_Overlapping_ReturnsClippedSpan()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        store.Set(0, 1);
        store.Set(1, 2);
        bool got = store.TryGetPublishedChunk(0, 2, out ReadOnlySpan<int> span);
        int spanLength = got ? span.Length : -1;
        int second = got ? span[1] : 0;
        await Assert.That(got).IsTrue();
        await Assert.That(spanLength).IsEqualTo(2);
        await Assert.That(second).IsEqualTo(2);
    }

    [Test]
    public async Task TryGetSeries_ByName()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _ParseOne();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            bool hasSeries = cache.TryGetSeries<ulong>("vcx.num", out ValueCacheSeries<ulong>? series);
            int count = series?.Count ?? 0;
            await Assert.That(hasSeries && series is not null && count == 1).IsTrue();
        }
    }
}
