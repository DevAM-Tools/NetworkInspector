// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Retained managed memory per frame of one file source after a full scan: memory-mapped PCAPNG and BLF (BLF container
/// cache off) and disk-backed ASC. The value is the random-access bookkeeping the source keeps alive.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class FootprintSourceScenario : FootprintScenarioBase
{
    #region Fields

    private readonly CaptureWorkload _Workload;
    private readonly int _Count;
    private string? _Path;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="workload">Capture workload; decides the format and the source.</param>
    /// <param name="count">Frames in the workload.</param>
    internal FootprintSourceScenario(CaptureWorkload workload, int count)
    {
        _Workload = workload;
        _Count = count;
    }

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public override string Name => _Workload switch
    {
        CaptureWorkload.PcapngUniform => "footprint-pcapng-uniform",
        CaptureWorkload.PcapngImix => "footprint-pcapng-imix",
        CaptureWorkload.BlfCan => "footprint-blf-can",
        CaptureWorkload.AscAbsolute => "footprint-asc-absolute",
        CaptureWorkload.AscRelative => "footprint-asc-relative",
        _ => Invariant($"footprint-{_Workload}"),
    };

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Retained bytes per frame after a full scan of {_Count:N0} frames ({_Workload}, no preload).");

    /// <inheritdoc/>
    public override long WorkUnitsPerIteration => _Count;

    /// <inheritdoc/>
    public override string WorkUnitName => "frames";

    /// <inheritdoc/>
    public override void Setup()
    {
        // The frames only feed the writer; they are unreachable before the first measurement.
        using (Stack stack = StackHelper.CreateFullStack())
        {
            _Path = CaptureWorkloads.CreateFile(_Workload, _Count, stack, Invariant($"{Name}.capture"));
        }

        CaptureWorkloads.Verify(Name, _Workload, _Path, _Count);
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
        SampleCaptureFiles.Cleanup();
        _Path = null;
    }

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected override IDisposable OpenAndScan()
    {
        // Open scans the whole file (ScanMode.Full; ASC always indexes at open), so the index is complete here.
        // Frames are not read: reading would add container and frame churn that is not bookkeeping.
        IRandomAccessFrameSource source = CaptureWorkloads.Open(_Workload, _Path!, blfCacheBudget: 0);
        FrameInterfaceRegistry registry = new();
        source.Start(registry.RegisterSource(source), registry);
        if (source.EstimatedFrameCount != _Count)
        {
            source.Dispose();
            throw new InvalidOperationException(
                Invariant($"Scenario {Name}: index holds {source.EstimatedFrameCount} frames, expected {_Count}."));
        }

        return source;
    }

    #endregion
}
