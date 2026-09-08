// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Chunk-span, indexer, First/All capture, chunk-shift, and concurrent-reader tests for
/// <see cref="ValueCacheSeries{T}"/>.
/// </summary>
internal sealed class ValueCacheSeriesTests
{
    #region Helpers

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

    private static ulong _SumSpanSimd(ReadOnlySpan<ulong> values)
    {
        ulong sum = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            int width = Vector256<ulong>.Count;
            Vector256<ulong> acc = Vector256<ulong>.Zero;
            for (; i + width <= values.Length; i += width)
            {
                acc += Vector256.Create(values.Slice(i, width));
            }

            sum = Vector256.Sum(acc);
        }

        for (; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    #endregion

    [Test]
    public async Task TryGetValueChunk_LengthEqualsMinChunkSizeObservedCount()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _ParseOne();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)], options: new() { ChunkShift = 4 });
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            int observed = series.Count;
            bool got = series.TryGetValueChunk(0, observed, out ReadOnlySpan<ulong> span);
            int spanLength = got ? span.Length : -1;
            await Assert.That(got).IsTrue();
            await Assert.That(spanLength).IsEqualTo(observed);
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
        store.Append(7);
        await Assert.That(store.TryGetPublishedChunk(0, 0, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetPublishedChunk_Overlapping_ReturnsClippedSpan()
    {
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        store.Append(1);
        store.Append(2);
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
            bool found = cache.TryGetSeries("vcx.num", out ValueCacheSeries<ulong>? series);
            await Assert.That(found).IsTrue();
            await Assert.That(series).IsNotNull();
        }
    }

    [Test]
    public async Task Record_FirstOccurrence_SamePacket_DoesNotGrow()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(7, 1, 1UL);
        series.Record(7, 1, 2UL);
        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].Value).IsEqualTo(1UL);
    }

    [Test]
    public async Task Record_AllOccurrences_SamePacket_GrowsTwoRows()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(7, 1, 1UL);
        series.Record(7, 1, 2UL);
        await Assert.That(series.Count).IsEqualTo(2);
        await Assert.That(series[0].PacketId).IsEqualTo(7);
        await Assert.That(series[1].PacketId).IsEqualTo(7);
        await Assert.That(series[1].Value).IsEqualTo(2UL);
    }

    [Test]
    public async Task Record_ChunkShift2_Throws()
    {
        await Assert.That(() => _ = new ValueCacheSeries<ulong>(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 2))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Record_ChunkShift4_SeventeenRows_CrossesChunk()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        for (int i = 0; i < 17; i++)
        {
            series.Record(i, i, (ulong)i);
        }

        await Assert.That(series.Count).IsEqualTo(17);
        bool first = series.TryGetValueChunk(0, 17, out ReadOnlySpan<ulong> chunk0);
        bool second = series.TryGetValueChunk(1, 17, out ReadOnlySpan<ulong> chunk1);
        int len0 = chunk0.Length;
        int len1 = chunk1.Length;
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(len0).IsEqualTo(16);
        await Assert.That(len1).IsEqualTo(1);
        await Assert.That(series[16].Value).IsEqualTo(16UL);
    }

    [Test]
    public async Task Record_BytesPayload_IsCopiedOutOfCallerBuffer()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId);
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.BytesId)]);
        cache.RecordPacket(packet);
        ValueCacheSeries<byte[]> series = cache.GetSeries<byte[]>(proto.BytesId);
        await Assert.That(series.Count).IsEqualTo(1);
        byte[] stored = series[0].Value;
        stored[0] = 9;
        await Assert.That(series[0].Value[0]).IsEqualTo((byte)9);
    }

    [Test]
    public async Task Record_StringAndIPv6AndUuid()
    {
        ValueCacheSeries<string> text = new(new FieldId(0), FieldType.String, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        text.Record(0, 1, "hello");
        ValueCacheSeries<IPv6Address> ip6 = new(new FieldId(1), FieldType.IPv6Address, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        ip6.Record(0, 1, default);
        ValueCacheSeries<Uuid> uuid = new(new FieldId(2), FieldType.Uuid, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        uuid.Record(0, 1, default);
        await Assert.That(text[0].Value).IsEqualTo("hello");
        await Assert.That(ip6.Count).IsEqualTo(1);
        await Assert.That(uuid.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Record_ConcurrentReaders_NeverSeeTornRow()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 8);
        const int rows = 10_000;
        Exception? readerError = null;
        Thread reader = new(() =>
        {
            try
            {
                while (Volatile.Read(ref series) is not null)
                {
                    int count = series.Count;
                    for (int i = 0; i < count; i++)
                    {
                        ValueCacheRow<ulong> row = series[i];
                        if (row.PacketId != (int)row.Value || row.TimestampNanos != (long)row.Value)
                        {
                            readerError = new InvalidOperationException("torn row");
                            return;
                        }
                    }

                    if (count >= rows)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                readerError = ex;
            }
        });
        reader.Start();
        for (int i = 0; i < rows; i++)
        {
            series.Record(i, i, (ulong)i);
        }

        reader.Join();
        await Assert.That(readerError).IsNull();
        await Assert.That(series.Count).IsEqualTo(rows);
    }

    [Test]
    public async Task Record_ConcurrentWriters_SerializesAllRows()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 8);
        const int attempts = 8_000;

        void Writer(int packetBase)
        {
            for (int i = 0; i < attempts; i++)
            {
                int packetId = packetBase + i;
                series.Record(packetId, packetId, (ulong)packetId);
            }
        }

        Task first = Task.Run(() => Writer(0));
        Task second = Task.Run(() => Writer(1_000_000));
        await Task.WhenAll(first, second);
        await Assert.That(series.Count).IsEqualTo(attempts * 2);

        int torn = 0;
        for (int i = 0; i < series.Count; i++)
        {
            ValueCacheRow<ulong> row = series[i];
            if (row.PacketId != (int)row.Value || row.TimestampNanos != row.PacketId)
            {
                torn++;
            }
        }

        await Assert.That(torn).IsEqualTo(0);
    }

    [Test]
    public async Task Handle_IndexAccess_MatchesIndexer()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(7, 100, 1UL);
        series.Record(8, 200, 2UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        await Assert.That(handle.Count).IsEqualTo(2);
        await Assert.That(handle.GetPacketId(0)).IsEqualTo(7);
        await Assert.That(handle.GetTimestamp(1)).IsEqualTo(200L);
        await Assert.That(handle.GetValue(1)).IsEqualTo(2UL);
        await Assert.That(handle[0].PacketId).IsEqualTo(series[0].PacketId);
        await Assert.That(series.GetPacketId(1)).IsEqualTo(8);
        await Assert.That(series.GetValue(0)).IsEqualTo(1UL);
    }

    [Test]
    public async Task Handle_AfterAppend_CountStaysFrozen()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        series.Record(2, 20, 2UL);
        await Assert.That(handle.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Handle_AfterAppend_NewIndexThrows()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        series.Record(2, 20, 2UL);
        await Assert.That(() => _ = handle.GetValue(1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Handle_AfterAppend_PrefixStillReadable()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        series.Record(2, 20, 2UL);
        await Assert.That(handle.GetValue(0)).IsEqualTo(1UL);
    }

    [Test]
    public async Task Handle_Foreach_DoesNotYieldRowsAppendedAfterSnapshot()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        series.Record(2, 20, 2UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        series.Record(3, 30, 3UL);
        int n = 0;
        foreach (ValueCacheRow<ulong> row in handle)
        {
            n++;
            _ = row.Value;
        }

        await Assert.That(n).IsEqualTo(2);
    }

    [Test]
    public async Task Handle_TryFindPacketIds_BinarySearch_AllOccurrencesRange()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        series.Record(2, 20, 2UL);
        series.Record(2, 20, 3UL);
        series.Record(4, 40, 4UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        bool foundTwo = handle.TryFindPacketId(2, out ValueCacheSeriesRange two);
        await Assert.That(foundTwo).IsTrue();
        await Assert.That(two.Start).IsEqualTo(1);
        await Assert.That(two.End).IsEqualTo(3);
        await Assert.That(two.Count).IsEqualTo(2);
        bool foundSpan = handle.TryFindPacketIds(2, 4, out ValueCacheSeriesRange span);
        await Assert.That(foundSpan).IsTrue();
        await Assert.That(span.Start).IsEqualTo(1);
        await Assert.That(span.End).IsEqualTo(4);
        bool foundMissing = handle.TryFindPacketId(3, out ValueCacheSeriesRange missing);
        await Assert.That(foundMissing).IsTrue();
        await Assert.That(missing.IsEmpty).IsTrue();
        bool foundInverted = handle.TryFindPacketIds(9, 1, out ValueCacheSeriesRange inverted);
        await Assert.That(foundInverted).IsTrue();
        await Assert.That(inverted.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Handle_TryFindTimestamps_BinarySearch()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(1, 100, 1UL);
        series.Record(2, 200, 2UL);
        series.Record(3, 300, 3UL);
        bool found = series.Handle.TryFindTimestamps(200, 300, out ValueCacheSeriesRange range);
        await Assert.That(found).IsTrue();
        await Assert.That(range.Start).IsEqualTo(1);
        await Assert.That(range.End).IsEqualTo(3);
        bool foundExact = series.Handle.TryFindTimestamp(100, out ValueCacheSeriesRange exact);
        await Assert.That(foundExact).IsTrue();
        await Assert.That(exact.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Handle_TryFindPacketIds_NotMonotonic_ReturnsFalse()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(5, 1, 1UL);
        series.Record(3, 2, 2UL);
        bool found = series.Handle.TryFindPacketId(5, out ValueCacheSeriesRange range);
        await Assert.That(found).IsFalse();
        await Assert.That(range.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Handle_NotMonotonic_LinearScanFindsScatteredPacketId()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(5, 1, 1UL);
        series.Record(3, 2, 2UL);
        series.Record(5, 3, 3UL);
        int hits = 0;
        foreach (ValueCacheRow<ulong> row in series.Handle)
        {
            if (row.PacketId == 5)
            {
                hits++;
            }
        }

        await Assert.That(hits).IsEqualTo(2);
    }

    [Test]
    public async Task Handle_TryFindTimestamps_NotMonotonic_ReturnsFalse()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(1, 50, 1UL);
        series.Record(2, 10, 2UL);
        bool found = series.Handle.TryFindTimestamp(50, out ValueCacheSeriesRange range);
        await Assert.That(found).IsFalse();
        await Assert.That(range.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Handle_Foreach_CrossesPartialChunk()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        for (int i = 0; i < 17; i++)
        {
            series.Record(i, i, (ulong)i);
        }

        int n = 0;
        ulong last = 0;
        foreach (ValueCacheRow<ulong> row in series.Handle)
        {
            if (row.PacketId != (int)row.Value)
            {
                throw new InvalidOperationException("packet id / value mismatch");
            }

            last = row.Value;
            n++;
        }

        await Assert.That(n).IsEqualTo(17);
        await Assert.That(last).IsEqualTo(16UL);
    }

    [Test]
    public async Task Handle_EnumerateFrom_WatermarkSkipsAlreadySeenRows()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        series.Record(2, 20, 2UL);
        int watermark = series.Handle.Count;
        series.Record(3, 30, 3UL);
        series.Record(4, 40, 4UL);
        int n = 0;
        ulong first = 0;
        foreach (ValueCacheRow<ulong> row in series.Handle.EnumerateFrom(watermark))
        {
            if (n == 0)
            {
                first = row.Value;
            }

            n++;
        }

        await Assert.That(n).IsEqualTo(2);
        await Assert.That(first).IsEqualTo(3UL);
    }

    [Test]
    public async Task Handle_EnumerateFrom_MidChunk_YieldsFromStartIndex()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        for (int i = 0; i < 17; i++)
        {
            series.Record(i, i, (ulong)i);
        }

        int n = 0;
        ulong first = 0;
        foreach (ValueCacheRow<ulong> row in series.Handle.EnumerateFrom(15))
        {
            if (n == 0)
            {
                first = row.Value;
            }

            n++;
        }

        await Assert.That(n).IsEqualTo(2);
        await Assert.That(first).IsEqualTo(15UL);
    }

    [Test]
    public async Task Handle_EnumerateFrom_AtCount_YieldsNothing()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(1, 1, 1UL);
        int n = 0;
        foreach (ValueCacheRow<ulong> _ in series.Handle.EnumerateFrom(1))
        {
            n++;
        }

        await Assert.That(n).IsEqualTo(0);
    }

    [Test]
    public async Task Handle_EnumerateFrom_PastCount_Throws()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(1, 1, 1UL);
        await Assert.That(() => _ = series.Handle.EnumerateFrom(2)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Handle_Enumerate_TryFindRange_YieldsMatchingRows()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 10, 1UL);
        series.Record(2, 20, 2UL);
        series.Record(2, 20, 3UL);
        series.Record(4, 40, 4UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        _ = handle.TryFindPacketId(2, out ValueCacheSeriesRange range);
        int n = 0;
        foreach (ValueCacheRow<ulong> row in handle.Enumerate(range))
        {
            n++;
            if (row.PacketId != 2)
            {
                throw new InvalidOperationException("range walk left packet id 2");
            }
        }

        await Assert.That(n).IsEqualTo(2);
    }

    [Test]
    public async Task Handle_IndexOutOfRange_Throws()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(1, 1, 1UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        await Assert.That(() => _ = handle.GetValue(1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => _ = handle.GetPacketId(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => _ = new ValueCacheSeriesRange(1, 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Handle_Empty_FindReturnsEmptyRange()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        await Assert.That(handle.Count).IsEqualTo(0);
        bool found = handle.TryFindPacketId(1, out ValueCacheSeriesRange range);
        await Assert.That(found).IsTrue();
        await Assert.That(range.IsEmpty).IsTrue();
        await Assert.That(handle.PacketIdsMonotonic).IsTrue();
    }

    [Test]
    public async Task Handle_EnumerateChunks_SumMatchesRowScan()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        ulong rowSum = 0;
        for (int i = 0; i < 17; i++)
        {
            ulong value = (ulong)(i + 1);
            series.Record(i, i, value);
            rowSum += value;
        }

        ulong chunkSum = 0;
        foreach (ValueCacheSeriesHandle<ulong>.Chunk chunk in series.Handle.EnumerateChunks())
        {
            chunkSum += _SumSpanSimd(chunk.Values);
        }

        await Assert.That(chunkSum).IsEqualTo(rowSum);
    }

    [Test]
    public async Task Handle_EnumerateChunksFrom_SlicesFirstChunk()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        for (int i = 0; i < 17; i++)
        {
            series.Record(i, i, (ulong)i);
        }

        int chunks = 0;
        int firstLength = -1;
        int firstStart = -1;
        ulong firstValue = 0;
        foreach (ValueCacheSeriesHandle<ulong>.Chunk chunk in series.Handle.EnumerateChunksFrom(15))
        {
            if (chunks == 0)
            {
                firstLength = chunk.Length;
                firstStart = chunk.StartIndex;
                firstValue = chunk.Values[0];
            }

            chunks++;
        }

        await Assert.That(chunks).IsEqualTo(2);
        await Assert.That(firstLength).IsEqualTo(1);
        await Assert.That(firstStart).IsEqualTo(15);
        await Assert.That(firstValue).IsEqualTo(15UL);
    }

    [Test]
    public async Task Handle_TryGetValueChunk_ClippedToSnapshotCount()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(1, 1, 1UL);
        ValueCacheSeriesHandle<ulong> handle = series.Handle;
        series.Record(2, 2, 2UL);
        bool got = handle.TryGetValueChunk(0, out ReadOnlySpan<ulong> span);
        int length = got ? span.Length : -1;
        await Assert.That(got).IsTrue();
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Handle_EnumerateChunks_Empty_YieldsNothing()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        int n = 0;
        foreach (ValueCacheSeriesHandle<ulong>.Chunk _ in series.Handle.EnumerateChunks())
        {
            n++;
        }

        await Assert.That(n).IsEqualTo(0);
    }
}
