// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that reads a pre-generated BLF file by iterating all frames
/// via <see cref="BlfSource"/>.
///
/// <para>
/// <b>Hot path:</b> <see cref="BlfSource.NextFrame"/> — container decompression,
/// object parsing, and frame materialisation from the BLF binary format.
/// The sample file is generated once in <see cref="Setup"/>.
/// </para>
/// </summary>
internal sealed class ReadBlfScenario : IProfilingScenario
{
    /// <summary>Number of frames in the sample file.</summary>
    private const int FrameCount = 50_000;

    private Stack? _Stack;
    private string? _FilePath;

    /// <inheritdoc/>
    public string Name => "read-blf";

    /// <inheritdoc/>
    public string Description =>
        $"Read {FrameCount:N0} frames from a BLF file per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(FrameCount, _Stack);
        _FilePath = SampleFileHelper.CreateBlfFile(frames);
    }

    /// <inheritdoc/>
    public void Run()
    {
        using BlfSource source = BlfSource.Open(_FilePath!);

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        while (source.NextFrame() is not null)
        {
            // Consume frames — measure read/decompression throughput only.
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        SampleFileHelper.Cleanup();
        _Stack?.Dispose();
        _Stack = null;
        _FilePath = null;
    }
}
