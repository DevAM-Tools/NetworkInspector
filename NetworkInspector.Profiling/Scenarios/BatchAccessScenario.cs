// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// <see cref="IRandomAccessFrameSource.FramesById"/> throughput over a fully scanned memory-mapped PCAPNG file (IMIX): the
/// same workload as <see cref="AccessScenario"/>, read as ranges of 64 consecutive ids instead of one call per id. The
/// order decides the order of the ranges (random: a fixed-seed permutation of the range starts; sequential: ascending).
/// Only the current build runs it (the frozen starting build has no range read); the comparison partner is the per-id
/// scenario of the same build.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class BatchAccessScenario : IProfilingScenario
{
    #region Constants

    private const int _Count = 200_000;
    private const int _RangeLength = 64;

    #endregion

    #region Fields

    private readonly AccessOrder _Order;
    private readonly Frame?[] _RangeFrames = new Frame?[_RangeLength];
    private IRandomAccessFrameSource? _Source;
    private int[] _RangeStarts = [];

    /// <summary>Sum of frame lengths, so the JIT cannot drop the calls.</summary>
    private long _Sink;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="order">Order of the ranges in every run.</param>
    internal BatchAccessScenario(AccessOrder order) => _Order = order;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => Invariant($"access-pcapng-batch-{_Order.ToString().ToLowerInvariant()}");

    /// <inheritdoc/>
    public string Description =>
        Invariant($"FramesById over ranges of {_RangeLength} ids, {_Count:N0} frames (PcapngImix), ranges in {_Order} order, 1 thread.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _Count;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        string path;
        using (Stack stack = StackHelper.CreateFullStack())
        {
            path = CaptureWorkloads.CreateFile(CaptureWorkload.PcapngImix, _Count, stack, Invariant($"{Name}.capture"));
        }

        _Source = CaptureWorkloads.Open(CaptureWorkload.PcapngImix, path, CaptureWorkloads.DefaultBlfCacheBudget);
        int read = CaptureWorkloads.StartAndDrain(_Source);
        if (read != _Count)
        {
            throw new InvalidOperationException(Invariant($"Scenario {Name}: workload has {read} frames, expected {_Count}."));
        }

        // The range starts 0, 64, 128, … in the configured order (the permutation of AccessOrders over the range indexes).
        int ranges = (_Count + _RangeLength - 1) / _RangeLength;
        int[] order = AccessOrders.Build(_Order, ranges);
        _RangeStarts = new int[ranges];
        for (int i = 0; i < ranges; i++)
        {
            _RangeStarts[i] = order[i] * _RangeLength;
        }
    }

    /// <inheritdoc/>
    public void Run()
    {
        IRandomAccessFrameSource source = _Source!;
        long sink = 0;
        foreach (int start in _RangeStarts)
        {
            int length = Math.Min(_RangeLength, _Count - start);
            Span<Frame?> frames = _RangeFrames.AsSpan(0, length);
            int found = source.FramesById(new FrameId(start), frames);
            if (found < length)
            {
                throw new InvalidOperationException(Invariant($"Scenario {Name}: FramesById returned {found} of {length} frames at {start}."));
            }

            for (int i = 0; i < length; i++)
            {
                sink += frames[i]!.Value.Length;
            }
        }

        _Sink += sink;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Source?.Dispose();
        _Source = null;
        _RangeStarts = [];
        Array.Clear(_RangeFrames);
        GC.KeepAlive(_Sink);
        SampleCaptureFiles.Cleanup();
    }

    #endregion
}
