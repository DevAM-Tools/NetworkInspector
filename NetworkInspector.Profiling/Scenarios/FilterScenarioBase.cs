// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Shared setup and hot loop for the filter scenarios: build a stack, pre-parse a large pool of
/// synthetic packets through a <see cref="PacketIndex"/>, compile one expression, then evaluate
/// it once per fresh packet.
///
/// <para>
/// <b>Fresh packets.</b> Packets are fully prepared in <see cref="Setup"/> so parse cost is not
/// charged to the timed phase. Warm-up reuses a small batch (JIT only). The timed phase consumes
/// each pre-parsed packet at most once.
/// </para>
///
/// <para>
    /// <b>Index pruning.</b> Scenarios with <see cref="UseIndex"/> enabled skip packets via
    /// <see cref="IFilter.TryIsPresenceCandidate"/> on the live index (no bitmap copy), then
    /// pass the index into <see cref="IFilter.TryIsMatch"/>. Presence pruning only helps when a
    /// required protocol/field group is absent (e.g. <c>tcp</c> on UDP-only traffic), not when
    /// a value predicate simply fails.
/// </para>
///
/// <para>
/// <b>Stateful filters.</b> Flank expressions must observe packets in ascending id order and keep
/// tracker state across the timed pool. Warm-up still calls <see cref="IFilter.ResetState"/> each
/// batch (reused ids). The timed phase resets once in <see cref="BeginTimedPhase"/> and then runs
/// continuously.
/// </para>
/// </summary>
internal abstract class FilterScenarioBase : IProfilingScenario
{
    #region Fields

    /// <summary>Packets evaluated per warm-up <see cref="Run"/> call (reused).</summary>
    protected const int BatchSize = 10_000;

    /// <summary>
    /// Distinct pre-parsed packets reserved for the timed phase. Sized so a cold filter pass can
    /// run for several seconds without re-parsing on the hot path.
    /// </summary>
    protected const int TimedPoolSize = 100_000;

    private Stack? _Stack;
    private Packet[]? _WarmupPackets;
    private Packet[]? _TimedPackets;
    private PacketFilter? _Filter;
    private PacketIndex? _Index;
    private int _TimedCursor;
    private bool _TimedPhase;
    private bool _TimedExhausted;
    private long _TimedMatchCount;
    private long _TimedEvalCount;

    #endregion

    #region Scenario shape

    /// <inheritdoc/>
    public abstract string Name
    {
        get;
    }

    /// <inheritdoc/>
    public abstract string Description
    {
        get;
    }

    /// <summary>The expression compiled in <see cref="Setup"/>.</summary>
    protected abstract string Expression
    {
        get;
    }

    /// <summary>
    /// When <see langword="true"/>, filter tracker state is preserved across timed batches
    /// (required for <c>flank</c>). Warm-up still resets every batch because packet ids are reused.
    /// </summary>
    protected virtual bool IsStateful => false;

    /// <summary>
    /// When <see langword="true"/>, build a candidate bitmap and pass the <see cref="PacketIndex"/>
    /// into every evaluation. When <see langword="false"/>, evaluate every packet with a
    /// <see langword="null"/> index (classic path without prune / presence shortcuts).
    /// </summary>
    protected virtual bool UseIndex => true;

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "evaluations";

    /// <inheritdoc/>
    public bool IsWorkComplete => _TimedPhase && _TimedExhausted;

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Index = new PacketIndex(_Stack);

        Frame[] warmupFrames = CreateFrames(BatchSize, _Stack);
        _WarmupPackets = _ParseIndexed(warmupFrames, packetIdBase: 0);

        _TimedPackets = _ParseIndexed(CreateFrames(TimedPoolSize, _Stack), packetIdBase: BatchSize);

        FilterResult<PacketFilter> compiled = PacketFilter.Compile(Expression, _Stack);
        if (!compiled.TryGetValue(out PacketFilter? filter))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Failed to compile filter '{Expression}': {compiled.Error.Message}"));
        }

        _Filter = filter;

        _TimedCursor = 0;
        _TimedPhase = false;
        _TimedExhausted = false;
        _TimedMatchCount = 0;
        _TimedEvalCount = 0;
    }

    /// <inheritdoc/>
    public void BeginTimedPhase()
    {
        _TimedPhase = true;
        _TimedCursor = 0;
        _TimedExhausted = false;
        _TimedMatchCount = 0;
        _TimedEvalCount = 0;
        _Filter!.ResetState();
    }

    /// <inheritdoc/>
    public void Run()
    {
        if (_TimedPhase)
        {
            _RunTimed();
            return;
        }

        _RunWarmup();
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        if (_TimedPhase && _TimedEvalCount > 0)
        {
            Console.WriteLine(
                FormattableString.Invariant(
                    $"  Filter matches: {_TimedMatchCount:N0} / {_TimedEvalCount:N0} evaluations ({100.0 * _TimedMatchCount / _TimedEvalCount:F2}%)."));
        }

        _Stack?.Dispose();
        _Stack = null;
        _WarmupPackets = null;
        _TimedPackets = null;
        _Filter = null;
        _Index = null;
    }

    #endregion

    #region Frame factory

    /// <summary>
    /// Builds the synthetic frames used for warm-up or timed pools.
    /// Default: identical Eth/IPv6/UDP frames with source port 12345.
    /// </summary>
    protected virtual Frame[] CreateFrames(int count, Stack stack) =>
        FrameHelper.CreateSharedFrames(count, stack);

    #endregion

    #region Private helpers

    private Packet[] _ParseIndexed(Frame[] frames, int packetIdBase)
    {
        PacketIndex index = _Index!;
        Stack stack = _Stack!;
        Packet[] packets = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            packets[i] = Packet.ParseFrameIndexed(new PacketId(packetIdBase + i), stack, frames[i], index);
        }

        return packets;
    }

    private void _RunWarmup()
    {
        PacketFilter filter = _Filter!;
        Packet[] packets = _WarmupPackets!;
        PacketIndex? index = UseIndex ? _Index : null;

        // Reused warmup ids require a clean cache/flank tracker every batch.
        filter.ResetState();

        for (int i = 0; i < BatchSize; i++)
        {
            Packet packet = packets[i];
            if (_IsPruned(filter, index, packet))
            {
                continue;
            }

            if (!filter.TryIsMatch(packet, index, out _, out FilterError? failure))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"Filter '{Expression}' failed on warmup packet {i}: {failure.Message}"));
            }
        }
    }

    private void _RunTimed()
    {
        Packet[] packets = _TimedPackets!;
        int remaining = packets.Length - _TimedCursor;
        if (remaining <= 0)
        {
            _TimedExhausted = true;
            return;
        }

        int count = Math.Min(BatchSize, remaining);
        PacketFilter filter = _Filter!;
        PacketIndex? index = UseIndex ? _Index : null;
        int start = _TimedCursor;

        // Stateful filters must keep flank/cache state across batches; only BeginTimedPhase resets.
        if (!IsStateful)
        {
            filter.ResetState();
        }

        for (int i = 0; i < count; i++)
        {
            Packet packet = packets[start + i];
            if (_IsPruned(filter, index, packet))
            {
                continue;
            }

            if (!filter.TryIsMatch(packet, index, out bool matched, out FilterError? failure))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Filter '{Expression}' failed on timed packet {packet.Id.Value}: {failure.Message}"));
            }

            _TimedEvalCount++;
            if (matched)
            {
                _TimedMatchCount++;
            }
        }

        _TimedCursor = start + count;
        if (_TimedCursor >= packets.Length)
        {
            _TimedExhausted = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsPruned(PacketFilter filter, PacketIndex? index, Packet packet)
    {
        if (index is null)
        {
            return false;
        }

        return filter.TryIsPresenceCandidate(index, (uint)packet.Id.Value, out bool isCandidate)
            && !isCandidate;
    }

    #endregion
}
