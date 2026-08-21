// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Test <see cref="ISessionListener"/> that records all callbacks for assertion.
/// Thread-safe: counters use <see cref="Interlocked"/> operations.
/// </summary>
internal sealed class TestSessionListener : ISessionListener
{
    private volatile int _TotalPacketsSeen;
    private int _SourcesChangedCount;
    private int _AllSourcesCompletedCount;
    private int _JobsChangedCount;
    private int _PhaseChangedCount;
    private int _StackChangedCount;
    private int _ShuttingDownCount;
    private int _UnsubscribedCount;

    // Stores the last phase seen by OnPhaseChanged.
    private int _LastPhase = -1;

    /// <inheritdoc/>
    public string UiName => "TestListener";

    /// <summary>Total number of packets observed via <see cref="OnNewPackets"/>.</summary>
    internal int TotalPacketsSeen => _TotalPacketsSeen;

    /// <summary>Number of <see cref="OnSourcesChanged"/> calls.</summary>
    internal int SourcesChangedCount => Volatile.Read(ref _SourcesChangedCount);

    /// <summary>Number of <see cref="OnAllSourcesCompleted"/> calls.</summary>
    internal int AllSourcesCompletedCount => Volatile.Read(ref _AllSourcesCompletedCount);

    /// <summary>Number of <see cref="OnJobsChanged"/> calls.</summary>
    internal int JobsChangedCount => Volatile.Read(ref _JobsChangedCount);

    /// <summary>Number of <see cref="OnPhaseChanged"/> calls.</summary>
    internal int PhaseChangedCount => Volatile.Read(ref _PhaseChangedCount);

    /// <summary>Number of <see cref="OnStackChanged"/> calls.</summary>
    internal int StackChangedCount => Volatile.Read(ref _StackChangedCount);

    /// <summary>Number of <see cref="OnShuttingDown"/> calls.</summary>
    internal int ShuttingDownCount => Volatile.Read(ref _ShuttingDownCount);

    /// <summary>Number of <see cref="OnUnsubscribed"/> calls.</summary>
    internal int UnsubscribedCount => Volatile.Read(ref _UnsubscribedCount);

    /// <summary>Last phase reported by <see cref="OnPhaseChanged"/>.</summary>
    internal SessionPhase? LastPhase
    {
        get
        {
            int v = Volatile.Read(ref _LastPhase);
            if (v < 0)
            {
                return null;
            }

            return (SessionPhase)v;
        }
    }

    /// <inheritdoc/>
    public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
    {
        int count = toIndexExclusive - fromIndex;
        Interlocked.Add(ref _TotalPacketsSeen, count);
    }

    /// <inheritdoc/>
    public void OnSourcesChanged(ISessionReader session)
        => Interlocked.Increment(ref _SourcesChangedCount);

    /// <inheritdoc/>
    public void OnAllSourcesCompleted(ISessionReader session)
        => Interlocked.Increment(ref _AllSourcesCompletedCount);

    /// <inheritdoc/>
    public void OnJobsChanged(ISessionReader session)
        => Interlocked.Increment(ref _JobsChangedCount);

    /// <inheritdoc/>
    public void OnPhaseChanged(SessionPhase phase)
    {
        Interlocked.Exchange(ref _LastPhase, (int)phase);
        Interlocked.Increment(ref _PhaseChangedCount);
    }

    /// <inheritdoc/>
    public void OnStackChanged(ISessionReader session)
        => Interlocked.Increment(ref _StackChangedCount);

    /// <inheritdoc/>
    public void OnShuttingDown()
        => Interlocked.Increment(ref _ShuttingDownCount);

    /// <inheritdoc/>
    public void OnUnsubscribed()
        => Interlocked.Increment(ref _UnsubscribedCount);
}
