// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Full session pipeline: <see cref="RandomFrameSource"/> generates IPv6/UDP frames, the session
/// skip-parses them, and a listener re-parses every packet with a recycle instance.
/// <see cref="PrepareIteration"/> constructs the session; <see cref="Run"/> waits for completion.
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
    private Session? _Session;

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
            $"PrepareIteration: Session+RandomFrameSource; Run: WaitForCompletion + MaterializeAll, {FrameCount:N0} frames.")
        : FormattableString.Invariant(
            $"PrepareIteration: Session+RandomFrameSource; Run: WaitForCompletion + TryGetPacket recycle, {FrameCount:N0} frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup() => _Stack = StackHelper.CreateStack();

    /// <inheritdoc/>
    public Action? PrepareIteration => _PrepareIteration;

    private void _PrepareIteration()
    {
        _Session = new Session(_Stack!);
        RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = FrameCount,
            Seed = Seed,
            Mode = RandomFrameMode.UdpIPv6,
            MinFrameSize = MinFrameSize,
            MaxFrameSize = MaxFrameSize,
        });

        CountingListener listener = new(_Materialize);

        if (!_Session.TryAddFrameSource(source, out _))
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException(
                "Failed to add frame source — session is not in the Idle phase.");
        }

        if (!_Session.TryAddListener(listener, out _))
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException(
                "Failed to add listener — session may be shutting down.");
        }

        if (!_Session.TryStart())
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException(
                "Failed to start session — session is not in the Idle phase.");
        }
    }

    /// <inheritdoc/>
    public void Run()
    {
        Session session = _Session!;
        session.WaitForCompletion();
        session.Shutdown();
        session.Dispose();
        _Session = null;
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Session?.Shutdown();
        _Session?.Dispose();
        _Session = null;
        _Stack?.Dispose();
        _Stack = null;
    }

    #endregion

    #region Nested types

    /// <summary>
    /// Pulls every packet in the notified window into one recycle instance. Optionally materializes.
    /// </summary>
    private sealed class CountingListener : ISessionListener
    {
        private readonly bool _Materialize;
        private long _PacketsSeen;
        private Packet? _Recycle;

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
                if (!session.TryGetPacket(new PacketId(i), _Recycle, out Packet? packet) || packet is null)
                {
                    continue;
                }

                _Recycle = packet;
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
