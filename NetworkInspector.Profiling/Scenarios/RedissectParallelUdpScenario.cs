// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Parallel re-parse of already parsed UDP frames on a shared stack whose protocols carry the
/// recorded replay state. Each worker re-parses the full batch, so the reported throughput is the
/// cumulative rate (1 thread = baseline, 2 threads ≈ 2× when scaling).
/// <para>
/// Worker threads are started in <see cref="Setup"/> and parked on a barrier, so
/// <see cref="Run"/> only releases one iteration and waits for it — thread creation is not
/// charged to warm-up or the timed phase.
/// </para>
/// </summary>
internal sealed class RedissectParallelUdpScenario : IProfilingScenario, IDisposable
{
    #region Fields

    private const int _BatchSize = 10_000;

    private readonly int _ThreadCount;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Packet[]? _RecyclePackets;
    private Thread[]? _Workers;
    private Barrier? _Barrier;
    private CountdownEvent? _WorkersReady;

    /// <summary>1 = <see cref="Cleanup"/> asked workers to exit. Written by the runner thread, read by workers.</summary>
    private volatile int _Stop;

    /// <summary>First worker exception, or <see langword="null"/>. Published via <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>.</summary>
    private volatile Exception? _Fault;

    #endregion

    #region Lifecycle

    /// <summary>Creates a parallel redissect scenario with the given worker thread count.</summary>
    internal RedissectParallelUdpScenario(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        _ThreadCount = threadCount;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => FormattableString.Invariant($"redissect-parallel-udp-{_ThreadCount}");

    /// <inheritdoc/>
    public string Description =>
        FormattableString.Invariant(
            $"Parallel ParseFrame re-parse, {_ThreadCount} threads each re-parse {_BatchSize:N0} packets (cumulative).");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => (long)_BatchSize * _ThreadCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_BatchSize, _Stack);
        _RecyclePackets = new Packet[_ThreadCount];
        _Stop = 0;
        _Fault = null;

        for (int i = 0; i < _BatchSize; i++)
        {
            Packet.ParseFrame(new PacketId(i), _Stack, _Frames[i]);
        }

        for (int t = 0; t < _ThreadCount; t++)
        {
            _RecyclePackets[t] = Packet.ParseFrame(new PacketId(0), _Stack, _Frames[0]);
        }

        // Main thread + N workers rendezvous at the start and end of every Run().
        _Barrier = new Barrier(_ThreadCount + 1);
        _WorkersReady = new CountdownEvent(_ThreadCount);
        _Workers = new Thread[_ThreadCount];
        for (int t = 0; t < _ThreadCount; t++)
        {
            int threadIndex = t;
            Thread worker = new(() => _WorkerLoop(threadIndex))
            {
                IsBackground = true,
                Name = FormattableString.Invariant($"redissect-udp-{threadIndex}"),
            };
            _Workers[t] = worker;
            worker.Start();
        }

        _WorkersReady.Wait();
    }

    /// <inheritdoc/>
    public void Run()
    {
        Barrier barrier = _Barrier!;
        barrier.SignalAndWait();
        barrier.SignalAndWait();

        Exception? fault = _Fault;
        if (fault is not null)
        {
            throw new InvalidOperationException("Redissect worker failed.", fault);
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stop = 1;
        _Barrier?.SignalAndWait();

        Thread[]? workers = _Workers;
        if (workers is not null)
        {
            for (int t = 0; t < workers.Length; t++)
            {
                workers[t].Join();
            }
        }

        _Barrier?.Dispose();
        _Barrier = null;
        _WorkersReady?.Dispose();
        _WorkersReady = null;
        _Workers = null;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
        _RecyclePackets = null;
        _Stop = 0;
        _Fault = null;
    }

    /// <summary>Releases worker synchronization primitives. Delegates to <see cref="Cleanup"/>.</summary>
    public void Dispose() => Cleanup();

    #endregion

    #region Private helpers

    /// <summary>
    /// Parks until <see cref="Run"/> or <see cref="Cleanup"/> arrives at the barrier, then either
    /// re-parses one batch or exits. Culture is set once; the thread lives for the whole scenario.
    /// </summary>
    private void _WorkerLoop(int threadIndex)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        _WorkersReady!.Signal();

        Barrier barrier = _Barrier!;
        while (true)
        {
            barrier.SignalAndWait();
            if (_Stop != 0)
            {
                return;
            }

            try
            {
                _RedissectBatch(threadIndex);
            }
            catch (Exception ex)
            {
                _ = Interlocked.CompareExchange(ref _Fault, ex, null);
            }

            barrier.SignalAndWait();
        }
    }

    /// <summary>Re-parses the full batch into this worker's recycled packet.</summary>
    private void _RedissectBatch(int threadIndex)
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet packet = _RecyclePackets![threadIndex];
        for (int i = 0; i < _BatchSize; i++)
        {
            RecycleError? error = Packet.TryParseFrame(packet, new PacketId(i), stack, frames[i]);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"Re-parse failed for packet {i}: {error}"));
            }
        }
    }

    #endregion
}
