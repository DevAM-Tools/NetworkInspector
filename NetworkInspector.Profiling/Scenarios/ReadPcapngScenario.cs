// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that reads a pre-generated PCAPNG file by iterating all frames
/// via <see cref="PcapSource"/>.
///
/// <para>
/// <b>Hot path:</b> <see cref="PcapSource.NextFrame"/> — block parsing, section/interface
/// tracking, and frame materialisation from the PCAPNG binary format.
/// The sample file is generated once in <see cref="Setup"/>.
/// </para>
/// </summary>
internal sealed class ReadPcapngScenario : IProfilingScenario
{
    /// <summary>Number of frames in the sample file.</summary>
    private const int FrameCount = 50_000;

    private Stack? _Stack;
    private string? _FilePath;

    /// <inheritdoc/>
    public string Name => "read-pcapng";

    /// <inheritdoc/>
    public string Description =>
        $"Read {FrameCount:N0} frames from a PCAPNG file per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(FrameCount, _Stack);
        _FilePath = SampleFileHelper.CreatePcapngFile(frames);
    }

    /// <inheritdoc/>
    public void Run()
    {
        using PcapSource source = PcapSource.Open(_FilePath!);

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        while (source.NextFrame() is not null)
        {
            // Consume frames — measure read/parse throughput only.
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
