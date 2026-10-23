// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Exit-point coverage tests for <c>NetworkInspector.CLI.Core</c> (ExitPointGaps gate).
/// </summary>
internal sealed class ExitCoverageTests
{
    [Test]
    public async Task GetNextArg_NullElement_ThrowsArgumentException()
    {
        string[] args = ["--output", null!];
        int index = 0;

        await Assert.That(() => CliArgumentParsing.GetNextArg(args, ref index, "--output"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task MiBToCacheBudgetBytes_Valid_ReturnsBytes()
    {
        int bytes = CliArgumentParsing.MiBToCacheBudgetBytes(2, "--blf-cache-size");

        await Assert.That(bytes).IsEqualTo(2 * 1024 * 1024);
    }

    [Test]
    public async Task DisposeSources_AllFail_ThrowsAggregateException()
    {
        List<IFrameSource> sources = [new ThrowingDisposeSource(), new ThrowingDisposeSource()];

        await Assert.That(() => CliSourceLifetime.DisposeSources(sources, TextWriter.Null))
            .Throws<AggregateException>();
    }

    [Test]
    public async Task Convert_NoSources_ReturnsArgumentError()
    {
        int code = await CliRun.ConvertAsync(["-o", "out.pcapng"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task Export_NoSources_ReturnsArgumentError()
    {
        int code = await CliRun.ExportAsync(["-f", "json"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    [Test]
    public async Task RunExportLoop_PacketIdOverflow_Throws()
    {
        FrameInterfaceRegistry registry = new();
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, registry);
        builder.RegisterStandardProtocols();
        using Stack stack = builder.Build();
        using RandomFrameSource source = new(count: 1, seed: 1, mode: RandomFrameMode.UdpIPv4);
        source.Start(registry.RegisterSource(source), registry);
        CapturingExporter exporter = new();
        int counter = Array.MaxLength - 1;
        List<IFrameSource> sources = [source];

        SplitOutputManager split = new("out.json", maxSize: 0, maxCount: 0);
        await Assert.That(() => ExportCommand.RunExportLoop(
                sources,
                _ => exporter,
                split,
                stack,
                null,
                0,
                0,
                false,
                TextWriter.Null,
                ref counter,
                CancellationToken.None,
                out _))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RoundRobin_MoveToNextActive_UnreachableEnd_CoveredViaReflection()
    {
        using EmptyFrameSource source = new();
        RoundRobinSourceIterator iterator = new([source]);
        System.Reflection.FieldInfo activeField = typeof(RoundRobinSourceIterator)
            .GetField("_Active", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        System.Reflection.FieldInfo countField = typeof(RoundRobinSourceIterator)
            .GetField("_ActiveCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool[] active = (bool[])activeField.GetValue(iterator)!;
        active[0] = false;
        countField.SetValue(iterator, 1);

        System.Reflection.MethodInfo move = typeof(RoundRobinSourceIterator)
            .GetMethod("_MoveToNextActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        move.Invoke(iterator, null);

        await Assert.That(iterator.HasActive).IsTrue();
    }

    private sealed class ThrowingDisposeSource : IFrameSource
    {
        public string UiName => "throwing";
        public string? Description => null;
        public int? EstimatedFrameCount => 0;
        public bool IsRunning => false;
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) { }
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }

    private sealed class EmptyFrameSource : IFrameSource
    {
        public string UiName => "empty";
        public string? Description => null;
        public int? EstimatedFrameCount => 0;
        public bool IsRunning => false;
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) { }
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        public void Dispose() { }
    }

    private sealed class CapturingExporter : IPacketListener
    {
        public string UiName => "capturing";
        public string? Description => null;
        public bool OnPacket(Packet packet) => true;
        public void OnFinish() { }
    }
}
