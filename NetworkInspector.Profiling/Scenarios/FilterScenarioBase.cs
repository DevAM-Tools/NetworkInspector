// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Shared setup and hot loop for the filter scenarios: build a stack, pre-parse a large pool of
/// synthetic packets through a <see cref="PacketIndex"/>, compile one expression, then evaluate
/// it once per fresh packet.
///
/// <para>
/// <b>Fresh packets.</b> Packets are fully prepared in <see cref="Setup"/> so parse cost is not
/// charged to the timed phase. Warm-up reuses a small batch (JIT only). The timed phase evaluates
/// every pool packet once per pass; when a pass is complete, <see cref="PrepareIteration"/> (excluded
/// from the measured time) re-parses the packets that were evaluated — evaluation can materialize their
/// lazy containers — and starts the next pass, so the whole timed phase measures fresh packets. Packets
/// the index pruned were never touched and are not re-parsed.
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
/// batch (reused ids). The timed phase resets in <see cref="BeginTimedPhase"/> and at the start of
/// every further pass (ids restart there), and runs continuously within a pass.
/// </para>
/// </summary>
internal abstract class FilterScenarioBase : IProfilingScenario
{
    #region Fields

    /// <summary>Packets evaluated per warm-up <see cref="Run"/> call (reused).</summary>
    protected const int BatchSize = 10_000;

    /// <summary>
    /// Distinct pre-parsed packets of the timed pool. The timed phase runs as many passes over the pool as fit into its
    /// duration, so the pool size only sets how often packets are re-parsed between passes.
    /// </summary>
    protected const int TimedPoolSize = 100_000;

    private Stack? _Stack;
    private Packet[]? _WarmupPackets;
    private Packet[]? _TimedPackets;
    private Frame[]? _TimedFrames;
    private PacketFilter? _Filter;
    private PacketIndex? _Index;
    private int _TimedCursor;
    private bool _TimedPhase;

    /// <summary>Pool slots evaluated in the current pass; only these are re-parsed before the next pass.</summary>
    private int[]? _Touched;
    private int _TouchedCount;
    private long _TimedMatchCount;
    private long _TimedEvalCount;

    /// <summary>Timed-pool packets that still have unpopulated lazy fields after their parse.</summary>
    private long _PendingLazyCount;

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
    public Action? PrepareIteration => _PrepareIteration;

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Index = new PacketIndex(_Stack);
        _PendingLazyCount = 0;

        Frame[] warmupFrames = CreateFrames(BatchSize, _Stack);
        _WarmupPackets = _ParseIndexed(warmupFrames, packetIdBase: 0);

        _TimedFrames = CreateFrames(TimedPoolSize, _Stack);
        _TimedPackets = _ParseIndexed(_TimedFrames, packetIdBase: BatchSize);
        _Touched = new int[TimedPoolSize];
        _TouchedCount = 0;

        FilterResult<PacketFilter> compiled = PacketFilter.Compile(Expression, _Stack);
        if (!compiled.TryGetValue(out PacketFilter? filter))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Failed to compile filter '{Expression}': {compiled.Error.Message}"));
        }

        _Filter = filter;

        _TimedCursor = 0;
        _TimedPhase = false;
        _TimedMatchCount = 0;
        _TimedEvalCount = 0;
    }

    /// <inheritdoc/>
    public void BeginTimedPhase()
    {
        _TimedPhase = true;
        _TimedCursor = 0;
        _TouchedCount = 0;
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
            Console.WriteLine(
                FormattableString.Invariant(
                    $"  Packets with pending lazy fields: {_PendingLazyCount:N0} / {TimedPoolSize:N0}."));
        }

        _Stack?.Dispose();
        _Stack = null;
        _WarmupPackets = null;
        _TimedPackets = null;
        _TimedFrames = null;
        _Touched = null;
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
        SampleFrames.CreateSharedFrames(count, stack);

    #endregion

    #region Private helpers

    private Packet[] _ParseIndexed(Frame[] frames, int packetIdBase)
    {
        PacketIndex index = _Index!;
        Stack stack = _Stack!;
        Packet[] packets = new Packet[frames.Length];

        // Index is fixed for this pool. Build options once so setup does not copy them per frame.
        ParseOptions options = new(index: index);
        for (int i = 0; i < frames.Length; i++)
        {
            PacketId id = new(packetIdBase + i);
            if (!Packet.TryParse(id, stack, frames[i], in options, out Packet? packet, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packets[i] = packet!;

            // Evidence for the evaluator: a lookup can only be resumed cheaply when no lazy container is pending.
            if (packetIdBase != 0 && packet!.HasUnpopulatedLazyFields)
            {
                _PendingLazyCount++;
            }
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

    /// <summary>
    /// Runs before every <see cref="Run"/>, outside the measured time. At the end of a timed pass it re-parses the
    /// evaluated packets (fresh lazy state, same ids, so the re-parse is a replay) and starts the next pass; a stateful
    /// filter is reset because the ids start again. Otherwise it does nothing.
    /// </summary>
    private void _PrepareIteration()
    {
        Packet[] packets = _TimedPackets!;
        if (!_TimedPhase || _TimedCursor < packets.Length)
        {
            return;
        }

        Stack stack = _Stack!;
        Frame[] frames = _TimedFrames!;
        int[] touched = _Touched!;
        ParseOptions options = new(index: _Index);
        for (int t = 0; t < _TouchedCount; t++)
        {
            int slot = touched[t];
            if (!Packet.TryParse(packets[slot], new PacketId(BatchSize + slot), stack, frames[slot], in options, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }

        _TouchedCount = 0;
        _TimedCursor = 0;
        if (IsStateful)
        {
            _Filter!.ResetState();
        }
    }

    private void _RunTimed()
    {
        Packet[] packets = _TimedPackets!;
        int remaining = packets.Length - _TimedCursor;
        if (remaining <= 0)
        {
            return;
        }

        int count = Math.Min(BatchSize, remaining);
        PacketFilter filter = _Filter!;
        PacketIndex? index = UseIndex ? _Index : null;
        int start = _TimedCursor;
        int[] touched = _Touched!;

        // Stateful filters must keep flank/cache state across batches; only a new pass resets.
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

            touched[_TouchedCount++] = start + i;
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
