// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// <see cref="Session.TryGetFrame"/> throughput of a session that ingested one memory-mapped PCAPNG source of IMIX
/// frames: the packet → frame map lookup plus the source's <see cref="IRandomAccessFrameSource.FrameById"/>. Every
/// <see cref="Run"/> requests every packet once in the configured order on each of the configured threads.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class SessionAccessScenario : IProfilingScenario
{
    #region Constants

    private const int _Count = 200_000;

    #endregion

    #region Fields

    private readonly AccessOrder _Order;
    private readonly int _Threads;
    private Stack? _Stack;
    private Session? _Session;
    private int[] _Ids = [];

    /// <summary>Sum of frame lengths, so the JIT cannot drop the calls.</summary>
    private long _Sink;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="order">Packet id order of every run.</param>
    /// <param name="threads">Concurrent reader threads (1 = the calling thread).</param>
    internal SessionAccessScenario(AccessOrder order, int threads = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        _Order = order;
        _Threads = threads;
    }

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => _Threads == 1
        ? Invariant($"access-session-{_Order.ToString().ToLowerInvariant()}")
        : Invariant($"access-session-{_Order.ToString().ToLowerInvariant()}-{_Threads}t");

    /// <inheritdoc/>
    public string Description =>
        Invariant($"Session.TryGetFrame over {_Count:N0} packets (mapped PCAPNG IMIX), {_Order} order, {_Threads} thread(s).");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => (long)_Count * _Threads;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        string path = CaptureWorkloads.CreateFile(CaptureWorkload.PcapngImix, _Count, _Stack, Invariant($"{Name}.pcapng"));

        // The session owns its source: Dispose shuts it down and disposes it.
        Session session = new(_Stack);
        IRandomAccessFrameSource source = CaptureWorkloads.Open(CaptureWorkload.PcapngImix, path, blfCacheBudget: 0);
        if (!session.TryAddFrameSource(source, out _, out SessionFailure? addFailure))
        {
            source.Dispose();
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: adding the source failed: {addFailure.Value.Message}"));
        }

        if (!session.TryStart(out SessionFailure? startFailure))
        {
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: session start failed: {startFailure.Value.Message}"));
        }

        _ = session.WaitForCompletion();
        if (session.PacketCount != _Count)
        {
            int packets = session.PacketCount;
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: ingested {packets} packets, expected {_Count}."));
        }

        _Session = session;
        _Ids = AccessOrders.Build(_Order, _Count);
    }

    /// <inheritdoc/>
    public void Run()
    {
        Session session = _Session!;
        _Sink += AccessOrders.RunOnThreads(_Threads, _Ids, id =>
        {
            if (!session.TryGetFrame(new PacketId(id), out Frame frame, out SessionFailure? failure))
            {
                throw new InvalidOperationException(Invariant($"Scenario {Name}: TryGetFrame({id}) failed: {failure.Value.Message}"));
            }

            return frame.Length;
        });
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Session?.Dispose();
        _Session = null;
        _Stack?.Dispose();
        _Stack = null;
        _Ids = [];
        GC.KeepAlive(_Sink);
        SampleCaptureFiles.Cleanup();
    }

    #endregion
}
