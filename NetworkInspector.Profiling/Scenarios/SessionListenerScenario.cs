// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Full session pipeline: <see cref="RandomFrameSource"/> generates IPv6/UDP frames, the session
/// first-parses them, and a listener pulls every packet from the store.
/// </summary>
internal sealed class SessionListenerScenario : IProfilingScenario, IDisposable
{
    #region Constants

    /// <summary>Number of frames generated per iteration.</summary>
    internal const int FrameCount = 10_000;

    /// <summary>Fixed PRNG seed so results are reproducible across iterations.</summary>
    internal const ulong Seed = 42;

    /// <summary>Minimum frame size in bytes.</summary>
    internal const int MinFrameSize = 128;

    /// <summary>Maximum frame size in bytes.</summary>
    internal const int MaxFrameSize = 1024;

    #endregion

    #region Fields

    private readonly bool _Materialize;
    private Stack? _Stack;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a session-listener scenario.
    /// </summary>
    /// <param name="materialize">
    /// When <see langword="true"/>, the listener calls <see cref="Packet.MaterializeAll"/>
    /// after each pull. When <see langword="false"/>, it only pulls the sealed packet.
    /// </param>
    internal SessionListenerScenario(bool materialize)
    {
        _Materialize = materialize;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name
    {
        get
        {
            if (_Materialize)
            {
                return "session-listener-materialized";
            }

            return "session-listener";
        }
    }

    /// <inheritdoc/>
    public string Description => _Materialize
        ? FormattableString.Invariant(
            $"Session pipeline: RandomFrameSource(UdpIPv6) -> parse -> listener TryGetPacket + MaterializeAll, {FrameCount:N0} frames.")
        : FormattableString.Invariant(
            $"Session pipeline: RandomFrameSource(UdpIPv6) -> parse -> listener TryGetPacket (lazy), {FrameCount:N0} frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup() => _Stack = StackHelper.CreateStack();

    /// <inheritdoc/>
    public void Run()
    {
        using Session session = new(_Stack!);

        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = FrameCount,
            Seed = Seed,
            Mode = RandomFrameMode.UdpIPv6,
            MinFrameSize = MinFrameSize,
            MaxFrameSize = MaxFrameSize,
        });

        CountingListener listener = new(_Materialize);

        if (!session.TryAddFrameSource(source, out _))
        {
            throw new InvalidOperationException(
                "Failed to add frame source — session is not in the Idle phase.");
        }

        if (!session.TryAddListener(listener, out _))
        {
            throw new InvalidOperationException(
                "Failed to add listener — session may be shutting down.");
        }

        if (!session.TryStart())
        {
            throw new InvalidOperationException(
                "Failed to start session — session is not in the Idle phase.");
        }

        session.WaitForCompletion();
        session.Shutdown();
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Stack?.Dispose();
        _Stack = null;
    }

    #endregion

    #region Nested types

    /// <summary>
    /// Pulls every packet in the notified window. Optionally materializes the field tree.
    /// </summary>
    private sealed class CountingListener : ISessionListener
    {
        private readonly bool _Materialize;
        private long _PacketsSeen;

        /// <summary>Creates a listener that pulls packets and optionally materializes them.</summary>
        internal CountingListener(bool materialize)
        {
            _Materialize = materialize;
        }

        /// <summary>Total packets processed so far (Volatile.Read).</summary>
        internal long PacketsSeen => Volatile.Read(ref _PacketsSeen);

        /// <inheritdoc/>
        public string UiName => "ProfilingCounter";

        /// <inheritdoc/>
        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            for (int i = fromIndex; i < toIndexExclusive; i++)
            {
                if (!session.TryGetPacket(new PacketId(i), out Packet? packet) || packet is null)
                {
                    continue;
                }

                if (_Materialize)
                {
                    packet.MaterializeAll();
                }
            }

            Interlocked.Add(ref _PacketsSeen, toIndexExclusive - fromIndex);
        }
    }

    #endregion
}
