// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Sequential read throughput of a capture workload without preloading: every <see cref="Run"/> opens the file
/// (memory-mapped PCAPNG with a full scan, or disk-backed ASC, which indexes at open), starts the source, and drains
/// <see cref="IFrameSource.NextFrame"/>. Covers the index-driven sequential path that the compact frame index changes.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class ReadWorkloadScenario : IProfilingScenario
{
    #region Constants

    private const int _Count = 50_000;

    #endregion

    #region Fields

    private readonly CaptureWorkload _Workload;
    private readonly string _Name;
    private string? _Path;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="workload">Capture workload to read.</param>
    /// <param name="name">Scenario name (<c>read-pcapng-full</c>, <c>read-asc</c>).</param>
    internal ReadWorkloadScenario(CaptureWorkload workload, string name)
    {
        _Workload = workload;
        _Name = name;
    }

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => _Name;

    /// <inheritdoc/>
    public string Description =>
        Invariant($"Open and read {_Count:N0} frames ({_Workload}, no preload) per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _Count;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        using (Stack stack = StackHelper.CreateFullStack())
        {
            _Path = CaptureWorkloads.CreateFile(_Workload, _Count, stack, Invariant($"{_Name}.capture"));
        }

        CaptureWorkloads.Verify(_Name, _Workload, _Path, _Count);
    }

    /// <inheritdoc/>
    public void Run()
    {
        using IRandomAccessFrameSource source = CaptureWorkloads.Open(_Workload, _Path!, CaptureWorkloads.DefaultBlfCacheBudget);
        _ = CaptureWorkloads.StartAndDrain(source);
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        SampleCaptureFiles.Cleanup();
        _Path = null;
    }

    #endregion
}
