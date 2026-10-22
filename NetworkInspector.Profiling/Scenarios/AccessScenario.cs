// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>Order in which <see cref="AccessScenario"/> and <see cref="SessionAccessScenario"/> request ids.</summary>
internal enum AccessOrder
{
    /// <summary>A fixed-seed permutation of all ids.</summary>
    Random,

    /// <summary>Ids 0 … n−1.</summary>
    Sequential,

    /// <summary>Ids n−1 … 0.</summary>
    Reverse,
}

/// <summary>
/// <see cref="IRandomAccessFrameSource.FrameById"/> throughput over a fully scanned file: memory-mapped PCAPNG (IMIX),
/// memory-mapped BLF (CAN, default container cache), or disk-backed ASC (CAN). The source is opened once in
/// <see cref="Setup"/>; every <see cref="Run"/> requests every id once in the configured order on each of the
/// configured threads.
/// </summary>
/// <remarks>
/// With several threads every thread walks the whole order, starting at its own offset (thread <c>t</c> of <c>n</c> starts
/// at <c>t · count / n</c> and wraps), so the threads read the same source concurrently but not the same frames at the same
/// time — the pattern of several session consumers. <b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run,
/// and Cleanup sequentially.
/// </remarks>
internal sealed class AccessScenario : IProfilingScenario
{
    #region Constants

    private const int _DefaultCount = 200_000;

    #endregion

    #region Fields

    private readonly CaptureWorkload _Workload;
    private readonly int _Count;
    private readonly AccessOrder _Order;
    private readonly int _Threads;
    private readonly string? _SizeLabel;
    private IRandomAccessFrameSource? _Source;
    private int[] _Ids = [];

    /// <summary>Sum of frame lengths, so the JIT cannot drop the calls.</summary>
    private long _Sink;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="workload">Capture workload; its format decides the source type.</param>
    /// <param name="order">Id order of every run.</param>
    /// <param name="threads">Concurrent reader threads (1 = the calling thread).</param>
    /// <param name="count">Frames in the workload; smaller for workloads with large frames, so the file stays small.</param>
    /// <param name="sizeLabel">
    /// Inserted after the format in the name (<c>access-pcapng-large-random</c>) for a workload that is not the default
    /// size; <see langword="null"/> keeps the default names.
    /// </param>
    internal AccessScenario(
        CaptureWorkload workload, AccessOrder order, int threads = 1, int count = _DefaultCount, string? sizeLabel = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _Workload = workload;
        _Order = order;
        _Threads = threads;
        _Count = count;
        _SizeLabel = sizeLabel;
    }

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name
    {
        get
        {
            string format = _SizeLabel is null
                ? _FormatName()
                : Invariant($"{_FormatName()}-{_SizeLabel}");
            string order = _Order.ToString().ToLowerInvariant();
            return _Threads == 1
                ? Invariant($"access-{format}-{order}")
                : Invariant($"access-{format}-{order}-{_Threads}t");
        }
    }

    /// <inheritdoc/>
    public string Description =>
        Invariant($"FrameById over {_Count:N0} frames ({_Workload}), {_Order} order, {_Threads} thread(s), per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => (long)_Count * _Threads;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        string path;
        using (Stack stack = StackHelper.CreateFullStack())
        {
            path = CaptureWorkloads.CreateFile(_Workload, _Count, stack, Invariant($"{Name}.capture"));
        }

        // Default BLF cache: the access scenarios measure the configuration users run.
        _Source = CaptureWorkloads.Open(_Workload, path, CaptureWorkloads.DefaultBlfCacheBudget);
        int read = CaptureWorkloads.StartAndDrain(_Source);
        if (read != _Count)
        {
            throw new InvalidOperationException(Invariant($"Scenario {Name}: workload has {read} frames, expected {_Count}."));
        }

        _Ids = AccessOrders.Build(_Order, _Count);
    }

    /// <inheritdoc/>
    public void Run()
    {
        IRandomAccessFrameSource source = _Source!;
        _Sink += AccessOrders.RunOnThreads(_Threads, _Ids, id =>
        {
            if (source.FrameById(new FrameId(id)) is not { } frame)
            {
                throw new InvalidOperationException(Invariant($"Scenario {Name}: FrameById({id}) returned null."));
            }

            return frame.Length;
        });
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Source?.Dispose();
        _Source = null;
        _Ids = [];
        GC.KeepAlive(_Sink);
        SampleCaptureFiles.Cleanup();
    }

    #endregion

    #region Private helpers

    private string _FormatName() => _Workload switch
    {
        CaptureWorkload.BlfCan => "blf",
        CaptureWorkload.AscAbsolute or CaptureWorkload.AscRelative => "asc",
        CaptureWorkload.AscEthernet => "asc-eth",
        CaptureWorkload.AscMixed => "asc-mixed",
        _ => "pcapng",
    };

    #endregion
}
