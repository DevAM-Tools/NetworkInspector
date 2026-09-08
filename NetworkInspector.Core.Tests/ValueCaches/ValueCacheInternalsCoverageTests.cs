// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Internals coverage for grow-only <see cref="ValueCacheSeries{T}"/> and cache getters.</summary>
internal sealed class ValueCacheInternalsCoverageTests
{
    [Test]
    public async Task Series_ClearRows_ResetsCount()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.AllOccurrences, chunkShift: 4);
        series.Record(0, 1, 1UL);
        series.ClearRows();
        await Assert.That(series.Count).IsEqualTo(0);
        series.Record(1, 2, 3UL);
        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].Value).IsEqualTo(3UL);
    }

    [Test]
    public async Task Series_NegativeObservedCount_ReturnsEmpty()
    {
        ValueCacheSeries<ulong> series = new(new FieldId(0), FieldType.U64, ValueCaptureMode.FirstOccurrence, chunkShift: 4);
        series.Record(0, 1, 1UL);
        bool got = series.TryGetValueChunk(0, -1, out ReadOnlySpan<ulong> span);
        int spanLength = got ? span.Length : 0;
        await Assert.That(got).IsFalse();
        await Assert.That(spanLength).IsEqualTo(0);
    }

    [Test]
    public async Task Cache_WrongPayloadType_TryGetSeriesFalse()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
        await Assert.That(cache.TryGetSeries<byte>(proto.NumberId, out _)).IsFalse();
        await Assert.That(cache.TryGetSeries<string>(proto.NumberId, out _)).IsFalse();
        await Assert.That(cache.TryGetCustomTextSeries(proto.NumberId, out _)).IsFalse();
    }

    [Test]
    public async Task Cache_ChunkShift21_Throws()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        await Assert.That(() => _ = new ValueCache(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId)],
                options: new ValueCacheBuildOptions { ChunkShift = 21 }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Cache_InvalidCaptureMode_Throws()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        await Assert.That(() => _ = new ValueCache(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId)],
                options: new ValueCacheBuildOptions { DefaultCaptureMode = (ValueCaptureMode)99 }))
            .Throws<ArgumentOutOfRangeException>();
    }
}
