// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Concurrent reader vs <see cref="ValueCache.RecordPacket"/> writer.
/// Stresses live <see cref="ValueCacheSeries.Count"/> against the writer; Core has no growth event.
/// </summary>
internal sealed class ValueCacheConcurrencyTests
{
    /// <summary>
    /// Stresses live Count vs writer. Must not run in parallel with other tests that share process-wide timing assumptions.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task RecordPacket_ConcurrentReaders_SeeOnlyCommittedRows()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
        const int packetCount = 64;
        Packet[] packets = new Packet[packetCount];
        for (int i = 0; i < packetCount; i++)
        {
            Frame frame = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i + 1),
                new byte[16],
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            proto.ResetParseState();
            packets[i] = Packet.ParseFrame(new PacketId(i), stack, frame, protoId);
        }

        using CancellationTokenSource cts = new();
        int lastSeen = 0;
        int readErrors = 0;
        Task reader = Task.Run(() =>
        {
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            while (!cts.IsCancellationRequested)
            {
                int count = series.Count;
                if (count < lastSeen)
                {
                    Interlocked.Increment(ref readErrors);
                }

                lastSeen = count;
                for (int i = 0; i < count; i++)
                {
                    _ = series[i];
                }
            }
        }, cts.Token);

        for (int i = 0; i < packetCount; i++)
        {
            cache.RecordPacket(packets[i]);
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(Volatile.Read(ref readErrors)).IsEqualTo(0);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(packetCount);
    }

    /// <summary>
    /// Readers re-read <see cref="ValueCache.Series"/> count while RecordAllFields grows columns.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task RecordAllFields_ConcurrentReaders_TolerateSeriesGrowth()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        const int packetCount = 32;
        Packet[] packets = new Packet[packetCount];
        for (int i = 0; i < packetCount; i++)
        {
            Frame frame = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i + 1),
                new byte[16],
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            proto.ResetParseState();
            packets[i] = Packet.ParseFrame(new PacketId(i), stack, frame, protoId);
        }

        using CancellationTokenSource cts = new();
        int readErrors = 0;
        Task reader = Task.Run(() =>
        {
            IReadOnlyList<ValueCacheSeries> series = cache.Series;
            while (!cts.IsCancellationRequested)
            {
                int count = series.Count;
                for (int i = 0; i < count; i++)
                {
                    ValueCacheSeries column = series[i];
                    int rows = column.Count;
                    if (rows < 0)
                    {
                        Interlocked.Increment(ref readErrors);
                    }
                }

                int enumerated = 0;
                foreach (ValueCacheSeries column in series)
                {
                    enumerated++;
                    _ = column.FieldId;
                    _ = column.Count;
                }

                _ = enumerated;
                if (cache.TryGetSeries<ulong>(proto.NumberId, out ValueCacheSeries<ulong>? numbers)
                    && numbers is not null
                    && numbers.Count < 0)
                {
                    Interlocked.Increment(ref readErrors);
                }
            }
        }, cts.Token);

        for (int i = 0; i < packetCount; i++)
        {
            cache.RecordPacket(packets[i]);
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(Volatile.Read(ref readErrors)).IsEqualTo(0);
        await Assert.That(cache.TryGetSeries<ulong>(proto.NumberId, out _)).IsTrue();
        await Assert.That(cache.Series.Count).IsGreaterThan(0);
        await Assert.That(cache.Series.Count).IsLessThan(stack.FieldCount);
    }
}
