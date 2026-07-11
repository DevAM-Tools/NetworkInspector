// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that runs the full session pipeline: a <see cref="RandomFrameSource"/>
/// generates random IPv6/UDP frames, the session parses them, and a custom
/// <see cref="ISessionListener"/> iterates every packet (pulling + MaterializeAll).
///
/// <para>
/// <b>Hot path:</b> RandomFrameSource -> SpinLock parse -> notify -> listener pull -> MaterializeAll.
/// This exercises the complete production data path including frame generation.
/// </para>
///
/// <para>
/// Compare with <see cref="RandomSourceParseScenario"/> which uses the identical
/// <see cref="RandomFrameSource"/> configuration but bypasses the session pipeline.
/// The difference isolates the overhead of Session + Listener threading.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class SessionListenerScenario : IProfilingScenario
{
    /// <summary>Number of frames generated per iteration.</summary>
    internal const int FrameCount = 10_000;

    /// <summary>Fixed PRNG seed so results are reproducible across iterations.</summary>
    internal const ulong Seed = 42;

    /// <summary>Minimum frame size in bytes.</summary>
    internal const int MinFrameSize = 128;

    /// <summary>Maximum frame size in bytes.</summary>
    internal const int MaxFrameSize = 1024;

    private Stack? _Stack;

    /// <inheritdoc/>
    public string Name => "session-listener";

    /// <inheritdoc/>
    public string Description =>
        FormattableString.Invariant(
            $"Full session pipeline: RandomFrameSource(UdpIPv6) -> parse -> listener iterate, {FrameCount:N0} frames per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup() => _Stack = StackHelper.CreateStack();

    /// <inheritdoc/>
    public void Run()
    {
        // Create a fresh session and source per iteration so the listener sees the full lifecycle.
        // The stack is reused across iterations; the RandomFrameSource generates fresh frames.
        using Session session = new(_Stack!);

        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = FrameCount,
            Seed = Seed,
            Mode = RandomFrameMode.UdpIPv6,
            MinFrameSize = MinFrameSize,
            MaxFrameSize = MaxFrameSize,
        });

        CountingListener listener = new();

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

        // WaitForCompletion waits for source threads to drain.
        // Shutdown then cancels listener threads and waits for them to finish;
        // the listener's DrainRemaining() processes the final NewPackets flag.
        session.WaitForCompletion();
        session.Shutdown();
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
    }

    /// <summary>
    /// Minimal <see cref="ISessionListener"/> that pulls and materialises every packet.
    /// </summary>
    private sealed class CountingListener : ISessionListener
    {
        /// <summary>Total packets processed so far (volatile read).</summary>
        internal long PacketsSeen => Volatile.Read(ref _PacketsSeen);
        private long _PacketsSeen;

        /// <inheritdoc/>
        public string UiName => "ProfilingCounter";

        /// <inheritdoc/>
        public void OnNewPackets(ISessionReader session, long fromIndex, long toIndexExclusive)
        {
            for (long i = fromIndex; i < toIndexExclusive; i++)
            {
                if (session.TryGetPacket((int)i, out Packet? packet))
                {
                    packet.MaterializeAll();
                }
            }

            Interlocked.Add(ref _PacketsSeen, toIndexExclusive - fromIndex);
        }
    }
}
