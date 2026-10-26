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
            {
                ParseOptions options = new(firstProtocol: protoId);
                if (!Packet.TryParse(
                    new PacketId(i),
                    stack,
                    frame,
                    in options,
                    out Packet? parsed,
                    out ParseFailure failure) || parsed is null)
                {
                    throw new InvalidOperationException(failure.ToString());
                }

                packets[i] = parsed;
            }
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
            {
                ParseOptions options = new(firstProtocol: protoId);
                if (!Packet.TryParse(
                    new PacketId(i),
                    stack,
                    frame,
                    in options,
                    out Packet? parsed,
                    out ParseFailure failure) || parsed is null)
                {
                    throw new InvalidOperationException(failure.ToString());
                }

                packets[i] = parsed;
            }
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

    /// <summary>
    /// A reader that asks for the wrong element type while the writer creates the payload series of a first-seen field must
    /// never get a series: a stale payload type paired with a new series would pass the type check and reinterpret the
    /// series as another type. The interleaving is not controllable, so the test repeats the first-seen race on fresh
    /// caches; it can only fail, never hang.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task RecordAllFields_ConcurrentWrongTypeLookup_NeverReturnsASeries()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        proto.ResetParseState();
        ParseOptions options = new(firstProtocol: protoId);
        if (!Packet.TryParse(new PacketId(0), stack, frame, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        // None of these fields stores byte elements, so TryGetSeries<byte> must always fail for them.
        FieldId[] wideFields = [proto.NumberId, proto.I64Id, proto.F64Id, proto.Ipv6Id, proto.UuidId, proto.StringId, proto.BytesId];
        ValueCache?[] current = new ValueCache?[1];
        using CancellationTokenSource cts = new();
        int wrongTypeHits = 0;
        Task reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                ValueCache? cache = Volatile.Read(ref current[0]);
                if (cache is null)
                {
                    continue;
                }

                for (int i = 0; i < wideFields.Length; i++)
                {
                    if (cache.TryGetSeries<byte>(wideFields[i], out _))
                    {
                        Interlocked.Increment(ref wrongTypeHits);
                    }
                }
            }
        }, cts.Token);

        for (int round = 0; round < 200; round++)
        {
            ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
            Volatile.Write(ref current[0], cache);
            cache.RecordPacket(packet);
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(Volatile.Read(ref wrongTypeHits)).IsEqualTo(0);
    }
}
