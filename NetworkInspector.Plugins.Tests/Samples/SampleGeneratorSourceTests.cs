// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The <c>SampleGen</c> source created through its registration: frames, options, statistics, errors, threads.</summary>
internal sealed class SampleGeneratorSourceTests
{
    #region Helpers

    // Creates and starts a source from option values the way a request does.
    private static IFrameSource _Start(Action<SettingsManager> configure, out FrameInterfaceRegistry registry)
    {
        using SettingsManager options = OptionManager.Create(SampleSource.RegisterOptions);
        configure(options);
        if (!SampleSource.Registration.Create(OptionManager.SourceRequest(options), out IFrameSource? source, out PluginError? error))
        {
            throw new InvalidOperationException(Expect.NotNull(error).Message);
        }

        // A session registers the source in the interface registry before it starts it; the source then adds its interface.
        registry = new();
        source.Start(registry.RegisterSource(source), registry);
        return source;
    }

    private static List<Frame> _ReadAll(IFrameSource source)
    {
        List<Frame> frames = [];
        while (source.NextFrame() is Frame frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    #endregion

    #region Frames

    [Test]
    public async Task UdpSourceYieldsTheGeneratedFrames()
    {
        using IFrameSource source = _Start(static options =>
        {
            OptionManager.Set(options, "count", SettingValue.I64(5));
            OptionManager.Set(options, "port", SettingValue.I64(41234));
            OptionManager.Set(options, "startCounter", SettingValue.U64(10));
        }, out _);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(5);
        for (int i = 0; i < frames.Count; i++)
        {
            byte[] expected = SampleFrameWriter.Write((ulong)(10 + i), (byte)(i % 5), SampleFrameWriter.SpeedFor(i), 41234);
            await Assert.That(frames[i].Id.Value).IsEqualTo(i);
            await Assert.That(frames[i].Data.Span.SequenceEqual(expected)).IsTrue();
            await Assert.That(frames[i].Timestamp.AsNanos).IsEqualTo((1_767_225_600L * 1_000_000_000L) + (i * 1_000_000L));
        }
    }

    [Test]
    public async Task TcpSourceYieldsSequenceNumbersOfOneConnection()
    {
        using IFrameSource source = _Start(static options =>
        {
            OptionManager.Set(options, "count", SettingValue.I64(3));
            OptionManager.Set(options, "transport", SettingValue.Enum("tcp", 1));
            OptionManager.Set(options, "port", SettingValue.I64(41234));
        }, out _);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(3);
        uint[] sequences = [1, 15, 29];
        for (int i = 0; i < 3; i++)
        {
            byte[] expected = SampleFrameWriter.WriteTcp((ulong)i, (byte)(i % 5), SampleFrameWriter.SpeedFor(i), 41234, sequences[i]);
            await Assert.That(frames[i].Data.Span.SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    public async Task RandomAccessReturnsTheSameBytesAsSequentialReading()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(5)), out _);
        IRandomAccessFrameSource random = (IRandomAccessFrameSource)source;
        List<Frame> sequential = _ReadAll(source);

        Frame? fourth = random.FrameById(new FrameId(3));

        await Assert.That(fourth).IsNotNull();
        await Assert.That(Expect.NotNull(fourth).Data.Span.SequenceEqual(sequential[3].Data.Span)).IsTrue();
        await Assert.That(fourth.Value.Timestamp).IsEqualTo(sequential[3].Timestamp);
    }

    [Test]
    public async Task RandomAccessOutsideTheRangeReturnsNull()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(5)), out _);
        IRandomAccessFrameSource random = (IRandomAccessFrameSource)source;

        await Assert.That(random.FrameById(new FrameId(5))).IsNull();
        await Assert.That(random.FrameById(new FrameId(-1))).IsNull();
    }

    [Test]
    public async Task StatisticsCountReadFramesOnly()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(5)), out _);
        IFrameSourceStatistics statistics = (IFrameSourceStatistics)source;

        _ReadAll(source);

        await Assert.That(statistics.ReadFrameCount).IsEqualTo(5);
        await Assert.That(statistics.SkippedFrameCount).IsEqualTo(0);
        await Assert.That(statistics.ErrorCount).IsEqualTo(0);
        await Assert.That(statistics.HasErrors).IsFalse();
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(5);
    }

    [Test]
    public async Task ZeroCountYieldsNoFrame()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(0)), out _);

        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task SpeedsFollowThePatternAcrossTwoHundredFramesWhenDecoded()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(200)), out FrameInterfaceRegistry registry);
        using Stack stack = SampleStack.Build();

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(200);
        for (int i = 0; i < frames.Count; i++)
        {
            // The source registered its own interface in its own registry; the stack parses the bytes with its registry.
            Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, frames[i].Data.ToArray(), i));
            await Assert.That(SampleStack.U64(stack, packet, "sample.speed")).IsEqualTo((ulong)SampleFrameWriter.SpeedFor(i));
        }

        await Assert.That(registry).IsNotNull();
    }

    #endregion

    #region Errors and lifecycle

    [Test]
    public async Task PathIsRefusedWithTheOptionNamePaths()
    {
        using SettingsManager options = OptionManager.Create(SampleSource.RegisterOptions);

        bool created = SampleSource.Registration.Create(OptionManager.SourceRequest(options, "x.bin"), out IFrameSource? source, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(source).IsNull();
        PluginError failure = Expect.NotNull(error);
        await Assert.That(failure.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(failure.Option).IsEqualTo("paths");
    }

    [Test]
    public async Task NextFrameBeforeStartThrows()
    {
        using SettingsManager options = OptionManager.Create(SampleSource.RegisterOptions);
        SampleSource.Registration.Create(OptionManager.SourceRequest(options), out IFrameSource? source, out _);
        using IFrameSource notStarted = Expect.NotNull(source);

        await Assert.That(() => notStarted.NextFrame()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NextFrameAfterDisposeThrows()
    {
        IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(2)), out _);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task CancelledTokenStopsNextFrame()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(2)), out _);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.That(() => source.NextFrame(cancelled.Token)).Throws<OperationCanceledException>();
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task RandomAccessFromFourThreadsEqualsSequentialFrames()
    {
        using IFrameSource source = _Start(static options => OptionManager.Set(options, "count", SettingValue.I64(1000)), out _);
        IRandomAccessFrameSource random = (IRandomAccessFrameSource)source;
        List<Frame> sequential = _ReadAll(source);
        using Barrier start = new(4);

        Task<int>[] workers = new Task<int>[4];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            int offset = worker;
            workers[worker] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                int mismatches = 0;

                // Each worker walks all ids in a different order: forward, backward, strided, and interleaved.
                for (int step = 0; step < 1000; step++)
                {
                    int id = offset switch
                    {
                        0 => step,
                        1 => 999 - step,
                        2 => (step * 7) % 1000,
                        _ => (step * 3) % 1000,
                    };
                    Frame? frame = random.FrameById(new FrameId(id));
                    if (frame is null
                        || !frame.Value.Data.Span.SequenceEqual(sequential[id].Data.Span)
                        || frame.Value.Timestamp != sequential[id].Timestamp)
                    {
                        mismatches++;
                    }
                }

                return mismatches;
            }, TaskCreationOptions.LongRunning);
        }

        int[] results = await Task.WhenAll(workers);

        await Assert.That(results.Sum()).IsEqualTo(0);
    }

    #endregion
}
