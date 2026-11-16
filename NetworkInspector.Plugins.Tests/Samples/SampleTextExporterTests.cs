// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The <c>SampleText</c> exporter created through its registration: lines, statistics, option, target rules, lifecycle.</summary>
internal sealed class SampleTextExporterTests
{
    #region Constants

    private static readonly string[] _ExpectedLines =
    [
        "packet_id\tcounter\tgear\tspeed",
        "0\t10\t1\t20",
        "1\t11\t2\t254",
        "2\t12\t3\t255",
    ];

    #endregion

    #region Helpers

    /// <summary>A target that refuses its entry and counts how often the format asked for it.</summary>
    private sealed class RefusingTarget : IExportTarget
    {
        /// <summary>How many times <see cref="TryOpenWrite"/> was called.</summary>
        internal int OpenCount { get; private set; }

        /// <inheritdoc/>
        public bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure)
        {
            OpenCount++;
            stream = null;
            failure = "share offline";
            return false;
        }
    }

    private static StreamExportRequest _Request(IExportTarget target, Stack stack, SettingsManager options) =>
        new(target, options.ReadOnly, stack, 4, CancellationToken.None);

    private static IPacketListener _Create(IExportTarget target, Stack stack, SettingsManager options)
    {
        if (!SampleTextExporter.Registration.Creation.TryGetForStreams(out StreamExporterCreate<IPacketListener>? create))
        {
            throw new InvalidOperationException("The sample exporter writes through streams.");
        }

        if (!create(_Request(target, stack, options), out IPacketListener? listener, out PluginError? error))
        {
            throw new InvalidOperationException(Expect.NotNull(error).Message);
        }

        return listener;
    }

    // Three sample packets (ids 0-2) and one DNS-like packet (id 3).
    private static Packet[] _Packets(Stack stack) =>
    [
        SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.Write(10, 1, 20, 40000), 0)),
        SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.Write(11, 2, 254, 40000), 1)),
        SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.Write(12, 3, 255, 40000), 2)),
        SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Udp("\0\u0001\u0001\0\0\u0001\0\0\0\0\0\0"u8, 50000, 53), 3)),
    ];

    private static string[] _Lines(MemoryStream memory) =>
        Encoding.UTF8.GetString(memory.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    #endregion

    #region Output

    [Test]
    public async Task WritesHeaderAndOneLinePerSamplePacket()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        using MemoryStream memory = new();
        IPacketListener listener = _Create(new StreamExportTarget(memory), stack, options);

        foreach (Packet packet in _Packets(stack))
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
        ((IDisposable)listener).Dispose();

        await Assert.That(_Lines(memory)).IsEquivalentTo(_ExpectedLines, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StatisticsAndByteProgressDescribeTheOutput()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        using MemoryStream memory = new();
        IPacketListener listener = _Create(new StreamExportTarget(memory), stack, options);

        foreach (Packet packet in _Packets(stack))
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
        IExporterStatistics statistics = (IExporterStatistics)listener;
        IExportByteProgress progress = (IExportByteProgress)listener;
        ((IDisposable)listener).Dispose();

        await Assert.That(statistics.WrittenCount).IsEqualTo(3);
        await Assert.That(statistics.SkippedCount).IsEqualTo(1);
        await Assert.That(statistics.ErrorCount).IsEqualTo(0);
        await Assert.That(statistics.IsFinished).IsTrue();
        await Assert.That(progress.EstimatedOutputBytes).IsEqualTo(memory.Length);
    }

    [Test]
    public async Task HeaderCanBeSwitchedOff()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        OptionManager.Set(options, "includeHeader", SettingValue.Bool(false));
        using MemoryStream memory = new();
        IPacketListener listener = _Create(new StreamExportTarget(memory), stack, options);

        foreach (Packet packet in _Packets(stack))
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
        ((IDisposable)listener).Dispose();

        string[] lines = _Lines(memory);
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("0\t10\t1\t20");
    }

    #endregion

    #region Target rules

    [Test]
    public async Task ARefusedMainEntryIsFileUnreadableWithTheReasonOfTheTarget()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        RefusingTarget target = new();
        SampleTextExporter.Registration.Creation.TryGetForStreams(out StreamExporterCreate<IPacketListener>? create);

        // Invariant: the sample registers ForStreams, so create is set.
        bool created = create!(_Request(target, stack, options), out IPacketListener? listener, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(listener).IsNull();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.FileUnreadable);
        await Assert.That(Expect.NotNull(error).Message).IsEqualTo("share offline");
        await Assert.That(target.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task StackWithoutTheSampleProtocolFailsNamingTheFieldBeforeTheTargetIsTouched()
    {
        using Stack stack = SampleStack.Build(withSample: false);
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        RefusingTarget target = new();
        SampleTextExporter.Registration.Creation.TryGetForStreams(out StreamExporterCreate<IPacketListener>? create);

        // Invariant: the sample registers ForStreams, so create is set.
        bool created = create!(_Request(target, stack, options), out _, out PluginError? error);

        await Assert.That(created).IsFalse();
        PluginError failure = Expect.NotNull(error);
        await Assert.That(failure.Code).IsEqualTo(PluginErrorCode.Failed);
        await Assert.That(failure.Message).Contains("sample.counter");
        await Assert.That(target.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task TheRegistrationWritesThroughStreamsAndNotThroughAPath()
    {
        await Assert.That(SampleTextExporter.Registration.Creation.WritesFile).IsFalse();
        await Assert.That(SampleTextExporter.Registration.Creation.TryGetForStreams(out _)).IsTrue();
        await Assert.That(SampleTextExporter.Registration.Creation.TryGetForFile(out _)).IsFalse();
    }

    #endregion

    #region Lifecycle

    [Test]
    public async Task PacketAfterFinishIsRefused()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        using MemoryStream memory = new();
        IPacketListener listener = _Create(new StreamExportTarget(memory), stack, options);
        Packet packet = _Packets(stack)[0];
        listener.OnFinish();

        await Assert.That(() => listener.OnPacket(packet)).Throws<InvalidOperationException>();
        ((IDisposable)listener).Dispose();
    }

    [Test]
    public async Task PacketAfterDisposeIsRefused()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        using MemoryStream memory = new();
        IPacketListener listener = _Create(new StreamExportTarget(memory), stack, options);
        Packet packet = _Packets(stack)[0];
        ((IDisposable)listener).Dispose();

        await Assert.That(() => listener.OnPacket(packet)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposeAfterFinishKeepsTheOutputAndASecondDisposeIsHarmless()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        using MemoryStream memory = new();
        IPacketListener listener = _Create(new StreamExportTarget(memory), stack, options);
        listener.OnPacket(_Packets(stack)[0]);
        listener.OnFinish();

        ((IDisposable)listener).Dispose();
        ((IDisposable)listener).Dispose();

        await Assert.That(_Lines(memory).Length).IsEqualTo(2);
    }

    #endregion
}
