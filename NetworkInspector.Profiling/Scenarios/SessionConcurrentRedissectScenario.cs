// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// One session ingest plus N listeners that re-parse every packet without a packet index.
/// <see cref="PrepareIteration"/> constructs the session; <see cref="Run"/> waits for ingest and redissect overlap.
/// Work units are <c>FrameCount * listenerCount</c> for the timed wait, not construction.
/// </summary>
internal sealed class SessionConcurrentRedissectScenario : IProfilingScenario, IDisposable
{
    #region Fields

    /// <summary>
    /// Frames ingested per iteration.
    /// </summary>
    internal const int FrameCount = 100_000;

    private readonly int _ListenerCount;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Session? _Session;

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
            $"PrepareIteration: Session RedissectOnly; Run: ingest + {_ListenerCount} redissect listener(s), {FrameCount:N0} frames.");

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
    public Action? PrepareIteration => _PrepareIteration;

    private void _PrepareIteration()
    {
        _Session = new Session(_Stack!, SessionOptions.RedissectOnly);
        MemoryFrameSource source = new(_Frames!);

        if (!_Session.TryAddFrameSource(source, out _))
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException("Failed to add frame source.");
        }

        for (int i = 0; i < _ListenerCount; i++)
        {
            RedissectListener listener = new(FormattableString.Invariant($"Redissect{i}"));
            if (!_Session.TryAddListener(listener, out _))
            {
                _Session.Dispose();
                _Session = null;
                throw new InvalidOperationException("Failed to add listener.");
            }
        }

        if (!_Session.TryStart())
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException("Failed to start session.");
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
        _Frames = null;
    }

    #endregion

    /// <summary>
    /// Re-parses every announced packet into one packet object that it keeps for its whole lifetime.
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
