// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ReadOnlyValueCache"/>, <see cref="ReadOnlyValueCacheSeries"/>,
/// and <see cref="ReadOnlyValueCacheSeries{T}"/>.
/// </summary>
internal sealed class ReadOnlyValueCacheTests
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
    public void ReadOnlyValueCache_NullOwner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _ = new ReadOnlyValueCache(null!));

    [Test]
    public void ReadOnlyValueCacheSeries_NullOwner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _ = new ReadOnlyValueCacheSeries(null!));

    [Test]
    public void ReadOnlyValueCacheSeriesOfT_NullOwner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _ = new ReadOnlyValueCacheSeries<ulong>(null!));

    #endregion

    #region Forwarding

    [Test]
    public async Task ValueCache_AsReadOnlyView_ForwardsFlagsAndSeries()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ReadOnlyValueCache view = cache.AsReadOnlyView();
            ReadOnlyValueCache alias = cache.ReadOnly;

            await Assert.That(view.IsAbandoned).IsFalse();
            await Assert.That(view.Stack).IsEqualTo(cache.Stack);
            await Assert.That(view.RecordAllFields).IsEqualTo(cache.RecordAllFields);
            await Assert.That(view.ChunkShift).IsEqualTo(cache.ChunkShift);
            await Assert.That(view.PacketIdsStrictlyIncreasing).IsTrue();
            await Assert.That(view.TimestampsStrictlyIncreasing).IsTrue();
            await Assert.That(view.IsMaterializationIncomplete).IsFalse();
            await Assert.That(view.Series.Count).IsEqualTo(cache.Series.Count);
            await Assert.That(view.AllSeries.Count).IsEqualTo(cache.AllSeries.Count);
            await Assert.That(view.Series[0].FieldId).IsEqualTo(proto.NumberId);
            await Assert.That(view.Series[0].Count).IsEqualTo(1);

            ReadOnlyValueCacheSeries<ulong> series = view.GetSeries<ulong>(proto.NumberId);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series.GetValue(0)).IsEqualTo(1UL);
            await Assert.That(series.Handle.Count).IsEqualTo(1);
            await Assert.That(alias.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ValueCache_AsIReadOnlyValueCache_GetSeries_ReturnsReadOnlySeries()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> writer = cache.GetSeries<ulong>(proto.NumberId);
            IReadOnlyValueCache read = cache;

            ReadOnlyValueCacheSeries<ulong> series = read.GetSeries<ulong>(proto.NumberId);
            await Assert.That(writer.Count).IsEqualTo(1);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series.GetValue(0)).IsEqualTo(writer.GetValue(0));
            await Assert.That(read.TryGetSeries<ulong>("vcx.num", out ReadOnlyValueCacheSeries<ulong> byName)).IsTrue();
            await Assert.That(byName.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ValueCacheSeries_AsReadOnlyView_ForwardsHandleAndChunks()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheSeries<ulong> writer = cache.GetSeries<ulong>(proto.NumberId);
            ReadOnlyValueCacheSeries<ulong> view = writer.AsReadOnlyView();
            ReadOnlyValueCacheSeries untyped = ((ValueCacheSeries)writer).AsReadOnlyView();

            await Assert.That(view.FieldId).IsEqualTo(writer.FieldId);
            await Assert.That(view.Count).IsEqualTo(writer.Count);
            await Assert.That(view[0].Value).IsEqualTo(1UL);
            await Assert.That(view.Handle.GetValue(0)).IsEqualTo(1UL);
            await Assert.That(untyped.GetPacketId(0)).IsEqualTo(writer.GetPacketId(0));
            bool gotChunk = view.TryGetValueChunk(0, view.Count, out ReadOnlySpan<ulong> values);
            int chunkLength = values.Length;
            await Assert.That(gotChunk).IsTrue();
            await Assert.That(chunkLength).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ReadOnlyValueCache_SeesAbandonOnOwner()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _, Packet packet) = _Parse();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ReadOnlyValueCache view = cache.AsReadOnlyView();
            cache.Abandon();

            await Assert.That(view.IsAbandoned).IsTrue();
            await Assert.That(view.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        }
    }

    #endregion
}
