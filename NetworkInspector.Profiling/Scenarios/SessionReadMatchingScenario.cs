// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>Which traffic and filter a matching-read scenario uses.</summary>
internal enum MatchingReadKind
{
    /// <summary>Shared UDP frames, <c>udp.port == 12345</c>: every packet matches.</summary>
    Match = 0,

    /// <summary>Shared UDP frames, <c>tcp.port == 80</c>: the index rejects every packet.</summary>
    Pruned = 1,

    /// <summary>Flank-pattern UDP frames, <c>udp.srcport &gt;= 200</c>: 2 % match, the index cannot reject.</summary>
    Sparse = 2,
}

/// <summary>
/// Ingest of 100 000 UDP/IPv6 frames with a packet index, while one listener pulls matching packets with
/// <see cref="PacketReadMode.Matching"/>. The match variant evaluates and re-parses every packet; the pruned variant
/// lets the index reject every packet without a re-parse; the sparse variant evaluates every packet but returns few.
/// <see cref="PrepareIteration"/> builds the session; <see cref="Run"/> waits for ingest and the listener.
/// </summary>
internal sealed class SessionReadMatchingScenario : IProfilingScenario, IDisposable
{
    #region Constants

    /// <summary>Frames ingested per iteration.</summary>
    internal const int FrameCount = 100_000;

    /// <summary>Every 50th sparse frame carries a source port of at least 200, so 2 % match.</summary>
    private const int _SparseSpikePeriod = 50;

    /// <summary>Buffer slots per matching read.</summary>
    private const int _BufferSize = 256;

    /// <summary>Upper bound for the listener to finish; only trips on a hang.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(60);

    #endregion

    #region Fields

    private readonly MatchingReadKind _Kind;
    private FrameInterfaceRegistry? _Registry;
    private Stack? _Stack;
    private Frame[]? _Frames;
    private Session? _Session;
    private MatchingReadListener? _Listener;

    #endregion

    #region Construction

    /// <summary>Creates one matching-read scenario.</summary>
    internal SessionReadMatchingScenario(MatchingReadKind kind) => _Kind = kind;

    #endregion

    #region Scenario shape

    /// <inheritdoc/>
    public string Name => _Kind switch
    {
        MatchingReadKind.Pruned => "session-read-matching-pruned",
        MatchingReadKind.Sparse => "session-read-matching-sparse",
        _ => "session-read-matching",
    };

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"PrepareIteration: Session+index+listener \"{_Expression}\"; Run: ingest + Matching pulls, {FrameCount:N0} frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public Action? PrepareIteration => _PrepareIteration;

    private string _Expression => _Kind switch
    {
        MatchingReadKind.Pruned => "tcp.port == 80",
        MatchingReadKind.Sparse => "udp.srcport >= 200",
        _ => "udp.port == 12345",
    };

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public void Setup()
    {
        // Frames are bound to one registry; every iteration's stack is built on it (see _PrepareIteration).
        _Registry = new();
        _Stack = StackHelper.CreateStack(_Registry);
        _Frames = _Kind == MatchingReadKind.Sparse
            ? SampleFrames.CreateFlankUdpFrames(FrameCount, _Stack, spikePeriod: _SparseSpikePeriod)
            : SampleFrames.CreateSharedFrames(FrameCount, _Stack);
    }

    private void _PrepareIteration()
    {
        // A fresh stack per iteration: the ids restart at 0, and on a reused stack every ingest parse would be a
        // replay that records no index, so the pruned variant would prune nothing and the others would prune
        // everything. Building the stack is excluded from the timed Run.
        _Stack?.Dispose();
        _Stack = StackHelper.CreateStack(_Registry!);

        // Default options keep the packet index on; the listener filter is compiled by the session.
        _Session = new Session(_Stack);
        _Listener = new MatchingReadListener(_BufferSize);
        if (!_Session.TryAddFrameSource(new MemoryFrameSource(_Frames!), out _, out _)
            || !_Session.TryAddListener(_Listener, _Expression, out ListenerInfo? info, out _, out _))
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException("Failed to set up the matching-read session.");
        }

        _Listener.Id = info.Id;
        if (!_Session.TryStart(out _))
        {
            _Session.Dispose();
            _Session = null;
            throw new InvalidOperationException("Failed to start the matching-read session.");
        }
    }

    /// <inheritdoc/>
    public void Run()
    {
        Session session = _Session!;
        session.WaitForCompletion();
        if (!_Listener!.Done.Wait(_Timeout))
        {
            throw new TimeoutException("Matching listener did not finish.");
        }

        if (_Listener.Failure is not null)
        {
            throw new InvalidOperationException(_Listener.Failure);
        }

        session.Shutdown();
        session.Dispose();
        _Session = null;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        Console.WriteLine(FormattableString.Invariant($"  Matched packets in last iteration: {_Listener?.Matched:N0}."));
        Dispose();
    }

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

    #region Nested types

    /// <summary>
    /// Pulls matching packets for every notified window. <see cref="Matched"/> counts returned rows; re-scans between
    /// two notifications can return a row twice, so the printed number is a plausibility check, not an exact count.
    /// </summary>
    private sealed class MatchingReadListener(int bufferSize) : ISessionListener
    {
        private readonly PacketRef[] _Buffer = new PacketRef[bufferSize];

        internal ListenerId Id { get; set; }

        internal ManualResetEventSlim Done { get; } = new(initialState: false);

        internal long Matched { get; private set; }

        internal string? Failure { get; private set; }

        public string UiName => "MatchingRead";

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            // A short batch means the scan reached the session's PacketCount (≥ toIndexExclusive); a full batch
            // continues after the last returned id. Recycled packets stay in the buffer slots.
            int next = fromIndex;
            while (next < toIndexExclusive)
            {
                if (!session.TryReadPackets(Id, next, _Buffer, PacketReadMode.Matching, out int count, out _,
                        out FilterError? failure, out SessionFailure? sessionFailure))
                {
                    Failure ??= failure?.Message ?? sessionFailure?.Message;
                    return;
                }

                Matched += count;
                if (count < _Buffer.Length)
                {
                    break;
                }

                next = _Buffer[count - 1].Id.Value + 1;
            }
        }

        public void OnAllSourcesCompleted(ISessionReader session) => Done.Set();
    }

    #endregion
}
