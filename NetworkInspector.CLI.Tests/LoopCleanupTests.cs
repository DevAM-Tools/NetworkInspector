// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// The export and convert loops delete the output that was still open when they end without finishing (cancel or error), keep
/// the outputs that were finished, and never delete a file they refused to overwrite.
/// </summary>
internal sealed class LoopCleanupTests
{
    #region Helpers

    /// <summary>A source that delegates to a random source and cancels a token, or throws, after a number of frames.</summary>
    private sealed class ScriptedSource(IFrameSource inner, int framesBeforeAction, CancellationTokenSource? cancel, bool throwInstead)
        : IFrameSource
    {
        private int _Delivered;

        public string UiName => "scripted";

        public string? Description => null;

        public int? EstimatedFrameCount => null;

        public bool IsRunning => true;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) => inner.Start(sourceId, registry);

        public Frame? NextFrame(CancellationToken cancellationToken = default)
        {
            if (_Delivered == framesBeforeAction)
            {
                if (throwInstead)
                {
                    throw new InvalidOperationException("source failed");
                }

                cancel?.Cancel();
            }

            _Delivered++;
            return inner.NextFrame(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }

    // The way the export command opens an output: prepare the target (an existing file is refused), then create the exporter.
    private static IPacketListener _CreatePrepared(string path)
    {
        if (!ExportTargets.TryPrepare(path, isDirectory: false, overwrite: false, out string? full, out _, out PluginError? error))
        {
            throw new InvalidOperationException(error.Value.Message);
        }

        return JsonExporter.CreateBuilder().ToFile(full).Build();
    }

    private static (Stack Stack, FrameInterfaceRegistry Registry) _Stack()
    {
        FrameInterfaceRegistry registry = new();
        SettingsManager settings = new();
        StackBuilder builder = new(settings, registry);
        builder.RegisterStandardProtocols();
        return (builder.Build(), registry);
    }

    private static ScriptedSource _Source(FrameInterfaceRegistry registry, int framesBeforeAction, CancellationTokenSource? cancel, bool throwInstead)
    {
        RandomFrameSource random = new(count: 20, seed: 3, mode: RandomFrameMode.UdpIPv4);
        ScriptedSource source = new(random, framesBeforeAction, cancel, throwInstead);
        source.Start(registry.RegisterSource(source), registry);
        return source;
    }

    #endregion

    #region Export loop

    [Test]
    public async Task ACancelledExportDeletesTheOpenOutput()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Root);
        string path = sandbox.Combine("out.json");
        (Stack stack, FrameInterfaceRegistry registry) = _Stack();
        using (stack)
        using (CancellationTokenSource cts = new())
        using (ScriptedSource source = _Source(registry, 3, cts, throwInstead: false))
        {
            // A source that honors the token throws when it is cancelled; the loop still discards the open output first.
            int counter = 0;
            bool cancelled = false;
            try
            {
                ExportCommand.RunExportLoop([source], p => JsonExporter.CreateBuilder().ToFile(p).Build(),
                    new SplitOutputManager(path, 0, 0), stack, null, 0, 0, false, TextWriter.Null, ref counter, cts.Token, out _);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            await Assert.That(cancelled).IsTrue();
        }

        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task AFailedExportKeepsFinishedSplitsAndDeletesTheOpenOne()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Root);
        (Stack stack, FrameInterfaceRegistry registry) = _Stack();
        using (stack)
        using (ScriptedSource source = _Source(registry, 5, null, throwInstead: true))
        {
            int counter = 0;
            await Assert.That(() => ExportCommand.RunExportLoop([source], p => JsonExporter.CreateBuilder().ToFile(p).Build(),
                new SplitOutputManager(sandbox.Combine("out.json"), 0, 2), stack, null, 0, 0, false, TextWriter.Null, ref counter, CancellationToken.None, out _))
                .Throws<InvalidOperationException>();
        }

        await Assert.That(File.Exists(sandbox.Combine("out_00001.json"))).IsTrue();
        await Assert.That(File.Exists(sandbox.Combine("out_00002.json"))).IsTrue();
        await Assert.That(File.Exists(sandbox.Combine("out_00003.json"))).IsFalse();
    }

    [Test]
    public async Task ARefusedOutputIsNeverDeleted()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Root);
        string leftover = sandbox.Combine("out_00002.json");
        File.WriteAllText(leftover, "keep");
        (Stack stack, FrameInterfaceRegistry registry) = _Stack();
        using (stack)
        using (ScriptedSource source = _Source(registry, 100, null, throwInstead: false))
        {
            int counter = 0;
            await Assert.That(() => ExportCommand.RunExportLoop([source], _CreatePrepared,
                new SplitOutputManager(sandbox.Combine("out.json"), 0, 2), stack, null, 0, 0, false, TextWriter.Null, ref counter, CancellationToken.None, out _))
                .Throws<InvalidOperationException>();
        }

        await Assert.That(File.ReadAllText(leftover)).IsEqualTo("keep");
    }

    #endregion

    #region Convert loop

    [Test]
    public async Task ACancelledConversionDeletesTheOpenOutput()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Root);
        string path = sandbox.Combine("out.pcapng");
        (Stack stack, FrameInterfaceRegistry registry) = _Stack();
        using (stack)
        using (CancellationTokenSource cts = new())
        using (ScriptedSource source = _Source(registry, 3, cts, throwInstead: false))
        {
            await Assert.That(() => ConvertCommand.RunConvertLoop([new CancellingNextSource(source, cts.Token)],
                p => NetworkInspector.Exporters.Pcapng.PcapngExporter.CreateBuilder().ToFile(p).Build(),
                new SplitOutputManager(path, 0, 0), null, null, 0, 0, false, TextWriter.Null, cts.Token, out _))
                .Throws<OperationCanceledException>();
        }

        await Assert.That(File.Exists(path)).IsFalse();
    }

    /// <summary>Makes <c>NextFrame</c> observe the token the way real file sources do: a cancelled token throws.</summary>
    private sealed class CancellingNextSource(IFrameSource inner, CancellationToken token) : IFrameSource
    {
        public string UiName => inner.UiName;

        public string? Description => inner.Description;

        public int? EstimatedFrameCount => inner.EstimatedFrameCount;

        public bool IsRunning => inner.IsRunning;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) => inner.Start(sourceId, registry);

        public Frame? NextFrame(CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested();
            return inner.NextFrame(cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }

    #endregion
}
