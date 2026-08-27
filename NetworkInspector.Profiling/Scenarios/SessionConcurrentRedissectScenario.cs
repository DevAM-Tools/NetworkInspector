// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// One session ingest plus N listeners that re-parse every packet. Packet store and packet index
/// are off, so each listener pays a full re-parse while the source thread parses the batch.
/// Every listener re-parses into its own recycled packet. Each <see cref="Run"/> call is one
/// complete session lifecycle, same as <see cref="SessionListenerScenario"/> — the batch is large
/// enough that start/stop cost does not dominate the measured work.
/// </summary>
internal sealed class SessionConcurrentRedissectScenario : IProfilingScenario
{
    #region Fields

    /// <summary>
    /// Frames ingested per <see cref="Run"/> call. Larger than
    /// <see cref="SessionListenerScenario.FrameCount"/> so session start/stop is amortized
    /// against parse and redissect work.
    /// </summary>
    internal const int FrameCount = 100_000;

    private readonly int _ListenerCount;

    private Stack? _Stack;
    private Frame[]? _Frames;

    #endregion

    #region Lifecycle

    /// <summary>Creates a concurrent ingest/redissect session scenario.</summary>
    internal SessionConcurrentRedissectScenario(int listenerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(listenerCount, 1);
        _ListenerCount = listenerCount;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => FormattableString.Invariant($"session-concurrent-redissect-{_ListenerCount}");

    /// <inheritdoc/>
    public string Description =>
        FormattableString.Invariant(
            $"Session ingest + {_ListenerCount} redissect listener(s), store/index off, {FrameCount:N0} frames per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => (long)FrameCount * _ListenerCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(FrameCount, _Stack);
    }

    /// <inheritdoc/>
    public void Run()
    {
        using Session session = new(_Stack!, SessionOptions.RedissectOnly);
        MemoryFrameSource source = new(_Frames!);

        if (!session.TryAddFrameSource(source, out _))
        {
            throw new InvalidOperationException("Failed to add frame source.");
        }

        for (int i = 0; i < _ListenerCount; i++)
        {
            RedissectListener listener = new(FormattableString.Invariant($"Redissect{i}"));
            if (!session.TryAddListener(listener, out _))
            {
                throw new InvalidOperationException("Failed to add listener.");
            }
        }

        if (!session.TryStart())
        {
            throw new InvalidOperationException("Failed to start session.");
        }

        session.WaitForCompletion();
        session.Shutdown();
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
    }

    #endregion

    /// <summary>
    /// Re-parses every announced packet into one packet object that it keeps for its whole lifetime,
    /// which is what the recycling overload of
    /// <see cref="ISessionReader.TryGetPacket(PacketId, Packet, out Packet)"/> is for. Safe because a
    /// listener slot runs its callback on a single thread and this listener keeps no field references
    /// past the loop iteration.
    /// </summary>
    private sealed class RedissectListener : ISessionListener
    {
        private Packet? _Recycle;

        internal RedissectListener(string name) => UiName = name;

        /// <inheritdoc/>
        public string UiName { get; }

        /// <inheritdoc/>
        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            for (int i = fromIndex; i < toIndexExclusive; i++)
            {
                if (!session.TryGetPacket(new PacketId(i), _Recycle, out Packet? packet) || packet is null)
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant($"Redissect miss for PacketId {i}."));
                }

                _Recycle = packet;
            }
        }
    }
}
