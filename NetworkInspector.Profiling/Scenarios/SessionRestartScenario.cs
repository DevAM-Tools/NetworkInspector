// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The restart itself: a finished session over a 200 000-frame in-memory source is restarted onto a fresh profiling stack once
/// per <see cref="Run"/>, so the measured time is the swap plus the re-parse of every packet (the work unit is the re-parsed
/// frame). No baseline exists for this scenario: it was added with "a restart is a generation change" and records the cost of
/// the restart for future comparisons. It is deliberately not part of the A/B set of that change, which proves that the
/// <em>read</em> path stayed within noise.
/// </summary>
/// <remarks>
/// <b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially. The stack factory builds a
/// new stack per run (building is part of every restart, so it is part of the measurement); the packet index is off so that the
/// measurement is the re-parse and not the growth of the index.
/// </remarks>
internal sealed class SessionRestartScenario : IProfilingScenario
{
    #region Constants

    /// <summary>Frames of the capture; the work done by one restart.</summary>
    private const int _FrameCount = 200_000;

    #endregion

    #region Fields

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Session? _Session;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => "session-restart-reparse";

    /// <inheritdoc/>
    public string Description =>
        Invariant($"Session.TryRestart onto a fresh profiling stack over {_FrameCount:N0} in-memory frames (swap + re-parse).");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = SampleFrames.CreateSharedFrames(_FrameCount, _Stack);

        // The session ingests once; every Run restarts it. The ingest is not measured.
        Session session = new(_Stack, new SessionOptions { IndexPackets = false });
        if (!session.TryAddFrameSource(new MemoryFrameSource(_Frames), out _, out SessionFailure? addFailure))
        {
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: adding the source failed: {addFailure!.Value.Message}"));
        }

        if (!session.TryStart(out SessionFailure? startFailure))
        {
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: session start failed: {startFailure!.Value.Message}"));
        }

        _ = session.WaitForCompletion();
        if (session.PacketCount != _FrameCount)
        {
            int packets = session.PacketCount;
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: ingested {packets} packets, expected {_FrameCount}."));
        }

        _Session = session;
    }

    /// <inheritdoc/>
    public void Run()
    {
        Session session = _Session!;
        if (!session.TryRestart(registry => StackHelper.CreateStack(registry), out RestartReport? report, out SessionFailure? failure))
        {
            throw new InvalidOperationException(Invariant($"{Name}: TryRestart failed: {failure!.Value.Message}"));
        }

        if (report!.ReparsedPackets != _FrameCount)
        {
            throw new InvalidOperationException(
                Invariant($"{Name}: re-parsed {report.ReparsedPackets} packets, expected {_FrameCount}."));
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Session?.Dispose();
        _Session = null;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
    }

    #endregion
}
