// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Stateful;

/// <summary>
/// Bounded per-filter state for one <c>flank(…)</c> expression.
/// <para>
/// Pairwise modes compare the current sample with the previous one (one stored value). Armed
/// modes (<c>from:</c> plus <c>to:</c> and/or <c>by:</c>) keep two in-window <c>from:</c>
/// candidates — Arm (oldest) and Next (second-oldest) — so intermediates do not cancel the
/// start, and an expired Arm can promote Next. State stays O(1) regardless of capture size.
/// </para>
/// <para>
/// <b>Storage policy.</b> After every evaluated packet that produced a sample, the sample
/// replaces the pairwise last slot. Armed candidates are offered only when the current sample
/// matches <c>from:</c> <b>and the packet did not fire</b>. Both candidate slots clear on fire
/// so a stay-in-<c>to:</c> does not re-fire, and <c>from:</c> must be seen on a later packet.
/// </para>
/// <para>
/// <b>Gate.</b> When a <c>when:</c> gate is present and evaluates false, the tracker is neither
/// read nor updated: the packet is invisible to this flank.
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe. Filter evaluation is single-threaded per
/// instance; the tracker is never shared across threads.
/// </para>
/// </summary>
internal sealed class FlankRuntime
{
    #region Fields

    // Pairwise slot: the previous sample (value, timestamp in nanoseconds, packet id). Valid only when _HasLast is set.
    private FieldValueData _Last;
    private long _LastNanos;
    private int _LastPacketId;
    private bool _HasLast;

    // Armed-mode "Arm" candidate: the oldest in-window sample that matched from:. Valid only when _Armed is set;
    // the stored values are ignored (and may be stale) while the flag is false, which is why clearing only resets flags.
    private bool _Armed;
    private FieldValueData _ArmValue;
    private long _ArmNanos;
    private int _ArmPacketId;

    // Armed-mode "Next" candidate: the second-oldest in-window from: sample, kept so it can replace Arm when Arm
    // expires. Valid only when _HasNext is set.
    private bool _HasNext;
    private FieldValueData _NextValue;
    private long _NextNanos;
    private int _NextPacketId;

    #endregion

    #region Construction

    /// <summary>Creates a tracker for one flank expression.</summary>
    /// <param name="accessor">Reads the tracked field from a packet.</param>
    /// <param name="from">Optional predecessor-region endpoint.</param>
    /// <param name="to">Optional arrival-region endpoint.</param>
    /// <param name="by">Optional numeric-delta predicate.</param>
    /// <param name="isAnyChange">Whether any value change fires; the parser sets it when no condition is given.</param>
    /// <param name="window">The mandatory proximity window. Parameters are not validated: the parser enforces the argument rules.</param>
    public FlankRuntime(
        ValueAccessor accessor,
        FlankEndpoint? from,
        FlankEndpoint? to,
        FlankDelta? by,
        bool isAnyChange,
        FlankWindow window)
    {
        Accessor = accessor;
        From = from;
        To = to;
        By = by;
        IsAnyChange = isAnyChange;
        Window = window;
    }

    #endregion

    #region Properties

    /// <summary>Reads the tracked field from the packet.</summary>
    public ValueAccessor Accessor { get; }

    /// <summary>Optional predecessor-region endpoint.</summary>
    public FlankEndpoint? From { get; }

    /// <summary>Optional arrival-region endpoint.</summary>
    public FlankEndpoint? To { get; }

    /// <summary>Optional numeric-delta predicate; <see langword="null"/> when unused.</summary>
    public FlankDelta? By { get; }

    /// <summary>Whether any value change fires the flank.</summary>
    public bool IsAnyChange { get; }

    /// <summary>The mandatory proximity window.</summary>
    public FlankWindow Window { get; }

    /// <summary>
    /// The compiled <c>when:</c> gate, or <see langword="null"/>. Assigned by the code generator after construction
    /// (the gate is compiled separately from the tracker); the tree evaluator checks it before feeding the tracker.
    /// </summary>
    public FilterEvalFn? When { get; set; }

    /// <summary>
    /// Armed latch: <c>from:</c> plus <c>to:</c> and/or <c>by:</c>.
    /// <c>from:</c> alone is pairwise departure, not armed.
    /// </summary>
    public bool IsArmedMode => From is not null && (To is not null || By is not null);

    #endregion

    #region State

    /// <summary>Forgets the stored sample and both armed-candidate slots.</summary>
    public void Reset()
    {
        _Last = default;
        _LastNanos = 0;
        _LastPacketId = 0;
        _HasLast = false;
        _ClearArmAndNext();
        _ArmValue = default;
        _ArmNanos = 0;
        _ArmPacketId = 0;
        _NextValue = default;
        _NextNanos = 0;
        _NextPacketId = 0;
    }

    /// <summary>
    /// Feeds one sample and reports whether it completes a transition.
    /// Armed mode does not Offer on a packet that just fired; <c>from:</c> must arrive later.
    /// </summary>
    /// <param name="current">The sample taken from the current packet.</param>
    /// <param name="nanos">The packet timestamp in nanoseconds.</param>
    /// <param name="packetId">The packet id, used by packet-count windows.</param>
    /// <returns>True when this sample completes the configured transition within the window.</returns>
    public bool Advance(in FieldValueData current, long nanos, int packetId)
    {
        bool fired;
        if (IsArmedMode)
        {
            // Armed mode: first drop candidates that fell out of the window (promoting Next if Arm expired), then try
            // to complete the transition against the surviving Arm.
            _ExpireAndPromote(nanos, packetId, out bool promoted);

            fired = _Armed
                && _CanFireWindow(_ArmNanos, _ArmPacketId, nanos, packetId)
                && _TryFireArmed(current);

            // After a fire both candidates are dropped and the firing sample is not offered as a new from: candidate,
            // so staying inside to: does not fire again; a fresh from: sample must arrive on a later packet.
            if (fired)
            {
                _ClearArmAndNext();
            }
            else if (From is FlankEndpoint from && _Matches(from, current))
            {
                _OfferCandidate(current, nanos, packetId, promoted);
            }
        }
        else
        {
            // Pairwise mode: compare against the single previous sample (nothing to compare on the first sample).
            fired = _HasLast
                && _CanFireWindow(_LastNanos, _LastPacketId, nanos, packetId)
                && _IsPairwiseTransition(current);
        }

        // The pairwise slot is updated for every sample, whether or not the packet fired (storage policy in the class
        // remarks). Armed mode never reads it; the update is a few stores, so it is not made conditional.
        _Last = current;
        _LastNanos = nanos;
        _LastPacketId = packetId;
        _HasLast = true;
        return fired;
    }

    #endregion

    #region Window

    /// <summary>
    /// Packet-count distance. PacketIds are presented in ascending order inside
    /// <c>0..ArrayIndexIdRange.MaxValue</c>, so the difference fits in <see cref="int"/>.
    /// </summary>
    private static int _PacketDistance(int refId, int nowId) => nowId - refId;

    /// <summary>
    /// Time distance in nanoseconds. Unchecked: capture clocks are not proven to stay inside a
    /// range where <c>now − ref</c> cannot wrap; a wrap looks like a future reference (keep, do
    /// not fire), the same outcome as a backwards clock (see <see cref="_IsTooOld"/> and <see cref="_CanFireWindow"/>).
    /// </summary>
    private static long _TimeElapsed(long refNanos, long nowNanos) => nowNanos - refNanos;

    /// <summary>
    /// True only when the reference is strictly older than <see cref="FlankWindow"/>. A negative
    /// elapsed time is a future reference (backwards clock) and is not treated as expiry.
    /// </summary>
    private bool _IsTooOld(long refNanos, int refId, long nowNanos, int nowId)
    {
        if (Window.IsPacketCount)
        {
            return _PacketDistance(refId, nowId) > Window.PacketCount;
        }

        return _TimeElapsed(refNanos, nowNanos) > Window.Nanoseconds;
    }

    /// <summary>
    /// True when the reference is in the past-or-present and still inside <see cref="FlankWindow"/>.
    /// Negative elapsed time never fires.
    /// </summary>
    private bool _CanFireWindow(long refNanos, int refId, long nowNanos, int nowId)
    {
        if (Window.IsPacketCount)
        {
            int distance = _PacketDistance(refId, nowId);
            return distance >= 0 && distance <= Window.PacketCount;
        }

        long elapsed = _TimeElapsed(refNanos, nowNanos);
        return elapsed >= 0 && elapsed <= Window.Nanoseconds;
    }

    #endregion

    #region Armed latch

    /// <summary>
    /// Drops an expired Next first, then promotes Next into Arm while Arm is strictly too old.
    /// <paramref name="promoted"/> is true when this packet consumed Next into Arm. Packet-window
    /// Offer uses that flag so the current sample does not immediately refill the Next slot just freed.
    /// <para>
    /// Next is checked first on purpose: a promoted Next is then known to be inside the window, so the loop ends after
    /// one promotion instead of promoting an expired candidate.
    /// </para>
    /// </summary>
    private void _ExpireAndPromote(long nowNanos, int nowId, out bool promoted)
    {
        promoted = false;
        if (_HasNext && _IsTooOld(_NextNanos, _NextPacketId, nowNanos, nowId))
        {
            _HasNext = false;
        }

        while (_Armed && _IsTooOld(_ArmNanos, _ArmPacketId, nowNanos, nowId))
        {
            if (_HasNext)
            {
                _ArmValue = _NextValue;
                _ArmNanos = _NextNanos;
                _ArmPacketId = _NextPacketId;
                _HasNext = false;
                promoted = true;
            }
            else
            {
                _Armed = false;
                break;
            }
        }
    }

    /// <summary>
    /// Armed fire predicate: <c>to:</c> if present, and <c>by:</c> against Arm if present.
    /// Does not apply the pairwise “last was outside <c>to:</c>” guard: Armed mode compares the current sample
    /// with the stored <c>from:</c> sample (Arm), not with the immediately preceding sample, so that guard has no role.
    /// </summary>
    private bool _TryFireArmed(in FieldValueData current)
    {
        bool toOk = To is not FlankEndpoint to || _Matches(to, current);
        bool byOk = By is null || _DeltaMatches(current, _ArmValue);
        return toOk && byOk;
    }

    /// <summary>
    /// Packet windows ignore a later <c>from:</c> when both slots were already filled: a promote
    /// that consumes Next does not open a slot for the current packet. Time windows still
    /// rank by timestamp (Arm is always the earliest candidate, Next the second earliest), including after a
    /// promote; this tolerates timestamps that are not monotonic in packet order.
    /// </summary>
    private void _OfferCandidate(in FieldValueData current, long nanos, int packetId, bool promoted)
    {
        if (Window.IsPacketCount)
        {
            if (!_Armed)
            {
                _SetArm(current, nanos, packetId);
                return;
            }

            if (!_HasNext && !promoted)
            {
                _SetNext(current, nanos, packetId);
            }

            return;
        }

        if (!_Armed)
        {
            _SetArm(current, nanos, packetId);
            return;
        }

        if (!_HasNext)
        {
            if (nanos < _ArmNanos)
            {
                _SetNext(_ArmValue, _ArmNanos, _ArmPacketId);
                _SetArm(current, nanos, packetId);
            }
            else
            {
                _SetNext(current, nanos, packetId);
            }

            return;
        }

        if (nanos < _ArmNanos)
        {
            _SetNext(_ArmValue, _ArmNanos, _ArmPacketId);
            _SetArm(current, nanos, packetId);
            return;
        }

        if (nanos < _NextNanos)
        {
            _SetNext(current, nanos, packetId);
        }
    }

    /// <summary>Writes Arm and sets the armed flag.</summary>
    private void _SetArm(in FieldValueData value, long nanos, int packetId)
    {
        _ArmValue = value;
        _ArmNanos = nanos;
        _ArmPacketId = packetId;
        _Armed = true;
    }

    /// <summary>Writes Next and sets the next-candidate flag.</summary>
    private void _SetNext(in FieldValueData value, long nanos, int packetId)
    {
        _NextValue = value;
        _NextNanos = nanos;
        _NextPacketId = packetId;
        _HasNext = true;
    }

    /// <summary>Clears Arm and Next flags. Stored values are ignored while flags are false.</summary>
    private void _ClearArmAndNext()
    {
        _Armed = false;
        _HasNext = false;
    }

    #endregion

    #region Pairwise

    /// <summary>
    /// Decides whether the pair (previous, current) forms an edge in pairwise mode.
    /// <para>
    /// An open endpoint is completed by the requirement that the other side must <b>not</b>
    /// already satisfy the given one; otherwise a value sitting inside the region would report a
    /// transition on every packet.
    /// </para>
    /// </summary>
    private bool _IsPairwiseTransition(in FieldValueData current)
    {
        if (IsAnyChange)
        {
            return FilterCompare.Compare(_Last, current) != 0;
        }

        if (By is not null)
        {
            return _DeltaMatches(current, _Last);
        }

        if (To is FlankEndpoint arrival)
        {
            return _Matches(arrival, current) && !_Matches(arrival, _Last);
        }

        FlankEndpoint departure = From!.Value;
        return _Matches(departure, _Last) && !_Matches(departure, current);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Compares <c>current − reference</c> against <see cref="By"/>.
    /// Caller guarantees <see cref="By"/> is set (armed or pairwise delta mode).
    /// </summary>
    private bool _DeltaMatches(in FieldValueData current, in FieldValueData reference)
    {
        FlankDelta by = By!.Value;
        if (!_TrySubtractAsI64(current, reference, out long delta))
        {
            return false;
        }

        return FilterCompare.Apply(FieldValueData.NewI64(delta), by.Op, by.Value);
    }

    /// <summary>
    /// Signed subtract for <c>by:</c>. Returns false (no fire) when either side is not an i64-fit
    /// integer or the difference overflows <see cref="long"/>.
    /// </summary>
    private static bool _TrySubtractAsI64(
        in FieldValueData current,
        in FieldValueData reference,
        out long delta)
    {
        delta = 0;
        if (!_TryToInt64(current, out long cur) || !_TryToInt64(reference, out long refer))
        {
            return false;
        }

        // Overflow guard for cur - refer without widening: with a positive subtrahend the result underflows when
        // cur < MinValue + refer; with a negative one it overflows when cur > MaxValue + refer. The bound
        // expressions themselves cannot overflow because refer has the opposite sign of the limit it is added to.
        if (refer > 0 && cur < long.MinValue + refer)
        {
            return false;
        }

        if (refer < 0 && cur > long.MaxValue + refer)
        {
            return false;
        }

        delta = cur - refer;
        return true;
    }

    /// <summary>
    /// Reads I64 as-is, or U64 when the value is ≤ <see cref="long.MaxValue"/>. Otherwise false.
    /// </summary>
    private static bool _TryToInt64(in FieldValueData value, out long number)
    {
        if (value.TryGetAsI64(out number))
        {
            return true;
        }

        if (value.TryGetAsU64(out ulong unsigned))
        {
            if (unsigned > long.MaxValue)
            {
                number = 0;
                return false;
            }

            number = (long)unsigned;
            return true;
        }

        number = 0;
        return false;
    }

    /// <summary>True when <paramref name="value"/> satisfies the endpoint comparison.</summary>
    private static bool _Matches(in FlankEndpoint endpoint, in FieldValueData value) =>
        FilterCompare.Apply(value, endpoint.Op, endpoint.Value);

    #endregion
}
