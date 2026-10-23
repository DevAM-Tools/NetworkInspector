// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The <c>SampleText</c> exporter created through its registration: lines, statistics, option, target rules, cleanup.</summary>
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

    private static ExportRequest _Request(string path, Stack stack, SettingsManager options) =>
        new(path, options.ReadOnly, stack, 4, CancellationToken.None);

    private static IPacketListener _Create(string path, Stack stack, SettingsManager options)
    {
        if (!SampleTextExporter.Registration.Create(_Request(path, stack, options), out IPacketListener? listener, out PluginError? error))
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

    #endregion

    #region Output

    [Test]
    public async Task WritesHeaderAndOneLinePerSamplePacket()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string path = folder.FilePath("out.sampletxt");
        IPacketListener listener = _Create(path, stack, options);

        foreach (Packet packet in _Packets(stack))
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
        ((IDisposable)listener).Dispose();

        string[] lines = (await File.ReadAllTextAsync(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo(_ExpectedLines, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StatisticsAndByteProgressDescribeTheOutput()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string path = folder.FilePath("out.sampletxt");
        IPacketListener listener = _Create(path, stack, options);

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
        await Assert.That(progress.EstimatedOutputBytes).IsEqualTo(new FileInfo(path).Length);
    }

    [Test]
    public async Task HeaderCanBeSwitchedOff()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        OptionManager.Set(options, "includeHeader", SettingValue.Bool(false));
        string path = folder.FilePath("out.sampletxt");
        IPacketListener listener = _Create(path, stack, options);

        foreach (Packet packet in _Packets(stack))
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
        ((IDisposable)listener).Dispose();

        string[] lines = (await File.ReadAllTextAsync(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("0\t10\t1\t20");
    }

    #endregion

    #region Target rules

    [Test]
    public async Task ExistingFileIsRefusedAndLeftUnchanged()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string path = folder.FilePath("out.sampletxt");
        await File.WriteAllTextAsync(path, "keep");

        bool created = SampleTextExporter.Registration.Create(_Request(path, stack, options), out IPacketListener? listener, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(listener).IsNull();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("keep");
    }

    [Test]
    public async Task MissingFolderIsFileNotFound()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);

        bool created = SampleTextExporter.Registration.Create(_Request(folder.FilePath("missing/out.sampletxt"), stack, options),
            out _, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.FileNotFound);
    }

    [Test]
    public async Task EmptyTargetIsInvalidOption()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);

        bool created = SampleTextExporter.Registration.Create(_Request(" ", stack, options), out _, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.InvalidOption);
    }

    [Test]
    public async Task PathWithNullCharacterIsInvalidOptionNotAnException()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);

        // The contract: expected failures are returned, never thrown.
        bool created = SampleTextExporter.Registration.Create(_Request("out\0.sampletxt", stack, options), out _, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.InvalidOption);
    }

    [Test]
    public async Task FileSystemRootIsInvalidOptionBecauseItNamesNoFile()
    {
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string root = Path.GetPathRoot(Path.GetTempPath()) ?? "/";

        bool created = SampleTextExporter.Registration.Create(_Request(root, stack, options), out _, out PluginError? error);

        await Assert.That(created).IsFalse();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.InvalidOption);
    }

    [Test]
    public async Task StackWithoutTheSampleProtocolFailsNamingTheField()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build(withSample: false);
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string path = folder.FilePath("out.sampletxt");

        bool created = SampleTextExporter.Registration.Create(_Request(path, stack, options), out _, out PluginError? error);

        await Assert.That(created).IsFalse();
        PluginError failure = Expect.NotNull(error);
        await Assert.That(failure.Code).IsEqualTo(PluginErrorCode.Failed);
        await Assert.That(failure.Message).Contains("sample.counter");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    #endregion

    #region Partial output

    [Test]
    public async Task DisposeWithoutFinishDeletesThePartialFile()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string path = folder.FilePath("out.sampletxt");
        IPacketListener listener = _Create(path, stack, options);
        listener.OnPacket(_Packets(stack)[0]);

        ((IDisposable)listener).Dispose();

        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task PacketAfterFinishIsRefused()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        IPacketListener listener = _Create(folder.FilePath("out.sampletxt"), stack, options);
        Packet packet = _Packets(stack)[0];
        listener.OnFinish();

        await Assert.That(() => listener.OnPacket(packet)).Throws<InvalidOperationException>();
        ((IDisposable)listener).Dispose();
    }

    [Test]
    public async Task PacketAfterDisposeIsRefused()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        IPacketListener listener = _Create(folder.FilePath("out.sampletxt"), stack, options);
        Packet packet = _Packets(stack)[0];
        ((IDisposable)listener).Dispose();

        await Assert.That(() => listener.OnPacket(packet)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposeAfterFinishKeepsTheFile()
    {
        using TempFolder folder = new("sample-text");
        using Stack stack = SampleStack.Build();
        using SettingsManager options = OptionManager.Create(SampleTextExporter.RegisterOptions);
        string path = folder.FilePath("out.sampletxt");
        IPacketListener listener = _Create(path, stack, options);
        listener.OnPacket(_Packets(stack)[0]);
        listener.OnFinish();

        ((IDisposable)listener).Dispose();
        ((IDisposable)listener).Dispose();

        await Assert.That(File.Exists(path)).IsTrue();
    }

    #endregion
}
