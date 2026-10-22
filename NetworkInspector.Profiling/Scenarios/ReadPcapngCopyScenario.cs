// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>Where <see cref="ReadPcapngCopyScenario"/> reads its frames from.</summary>
internal enum PcapngCopyBackend
{
    /// <summary><see cref="PcapngSource"/> over the memory-mapped file (<c>PreloadBudget = null</c>): one copy per frame.</summary>
    Mapped,

    /// <summary><see cref="PcapngStreamSource"/> over a <see cref="FileStream"/>: one copy per frame out of the block buffer.</summary>
    Stream,
}

/// <summary>
/// Profiling scenario for the frame-copying read paths: the PCAPNG file source on the memory-mapped backend and the
/// PCAPNG stream source. Compares one array per frame (<c>FrameSlabSize = 0</c>) with frame slabs, once with frames
/// dropped right after reading and once with every frame of the iteration held (as a session index or a frame cache
/// holds them), so the measurement shows both the gen-0 cost and the survivor cost.
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</para>
/// </remarks>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated in Program.Main.")]
internal sealed class ReadPcapngCopyScenario : ReadScenarioBase
{
    private const int _Count = 50_000;

    private readonly PcapngCopyBackend _Backend;
    private readonly int _FrameSlabSize;
    private readonly bool _HoldFrames;

    /// <summary>Frames of the current iteration when <see cref="_HoldFrames"/> is set; cleared before every run.</summary>
    private readonly Frame[] _Held = new Frame[_Count];

    /// <summary>Creates the scenario.</summary>
    /// <param name="backend">Read path to measure.</param>
    /// <param name="frameSlabSize"><see cref="PcapngSourceOptions.FrameSlabSize"/>; 0 = one array per frame.</param>
    /// <param name="holdFrames">True keeps every frame of an iteration reachable until the iteration ends.</param>
    internal ReadPcapngCopyScenario(PcapngCopyBackend backend, int frameSlabSize, bool holdFrames)
    {
        _Backend = backend;
        _FrameSlabSize = frameSlabSize;
        _HoldFrames = holdFrames;
    }

    /// <inheritdoc/>
    protected override int FrameCount => _Count;

    /// <inheritdoc/>
    protected override string CreateSampleFile(Frame[] frames)
        => SampleCaptureFiles.CreatePcapngFile(frames);

    /// <inheritdoc/>
    public override string Name => FormattableString.Invariant(
        $"read-pcapng-{(_Backend == PcapngCopyBackend.Mapped ? "mmap" : "stream")}{(_HoldFrames ? "-hold" : "")}{(_FrameSlabSize > 0 ? $"-slab{_FrameSlabSize / 1024}k" : "")}");

    /// <inheritdoc/>
    public override string Description => FormattableString.Invariant(
        $"Read {_Count:N0} frames ({_Backend}, FrameSlabSize={_FrameSlabSize}, hold={_HoldFrames}) per iteration.");

    /// <inheritdoc/>
    protected override void RunIteration(string filePath)
    {
        PcapngSourceOptions options = new() { PreloadBudget = null, FrameSlabSize = _FrameSlabSize };
        using IFrameSource source = _Backend == PcapngCopyBackend.Mapped
            ? PcapngSource.Open(filePath, options)
            : PcapngStreamSource.FromStream(File.OpenRead(filePath), "profiling", leaveOpen: false, options);

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        // Held frames survive until the next iteration clears them: that is the survivor pattern of a session index.
        int index = 0;
        while (source.NextFrame() is { } frame)
        {
            if (_HoldFrames && index < _Held.Length)
            {
                _Held[index++] = frame;
            }
        }

        Array.Clear(_Held);
    }
}
