// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Skip-parse pre-check of one listener filter: the equivalent observer, its parse options, a recycled skip-mode
/// packet, and an adaptive switch.
/// <para>
/// <b>Adaptive switch.</b> A pre-check costs a skip parse; it pays off only when most packets are rejected. After each
/// window of <see cref="Window"/> pre-checks, a window in which more than half of the packets could match turns the
/// pre-check off for a backoff, then it probes again. The backoff starts at <see cref="InitialBackoff"/> ids, doubles
/// after every further dense window up to <see cref="MaxBackoff"/>, and returns to <see cref="InitialBackoff"/> after
/// a sparse window. A match-all filter therefore pays a vanishing share of extra skip parses, while a sparse filter
/// keeps the pre-check on. Verdicts never depend on the switch.
/// </para>
/// <para><b>Thread-safety:</b> not thread-safe; used only under the owning slot's <c>FilterGate</c>.</para>
/// </summary>
internal sealed class MatchingPrecheck
{
    #region Constants

    /// <summary>
    /// Pre-checks per decision window. A window of 64 pre-checks decides whether more than half could match. Constant
    /// (tuning value, not configurable).
    /// </summary>
    internal const int Window = 64;

    /// <summary>
    /// Ids read without a pre-check after the first dense window (and after any dense window following a sparse one).
    /// Constant (tuning value, not configurable).
    /// </summary>
    internal const int InitialBackoff = 4096;

    /// <summary>
    /// Upper bound of the doubling backoff; bounds how long a filter that turns sparse waits for a new probe. Constant
    /// (tuning value, not configurable).
    /// </summary>
    internal const int MaxBackoff = 1 << 20;

    #endregion

    #region Fields

    private readonly ParseOptions _Options;
    private int _Checked;
    private int _MaybeMatches;
    private int _SkipRemaining;
    private int _NextBackoff = InitialBackoff;

    #endregion

    #region Construction

    /// <summary>Creates the pre-check of <paramref name="owner"/> with its equivalent <paramref name="observer"/>.</summary>
    internal MatchingPrecheck(PacketFilter owner, FilterObserver observer)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(observer);
        Owner = owner;
        Observer = observer;
        // Not the observer's own watch: the owner-expanding watch makes the skip parse open every lazy container the
        // field-tree filter would materialize, so a rejection here is a rejection there.
        ParseObserver[] observers = [new() { Observer = observer, Watch = observer.CreateOwnerExpandingWatch() }];
        _Options = new ParseOptions(FieldTreeMode.Skip, observers: observers);
    }

    #endregion

    #region Properties

    /// <summary>The filter whose verdicts this pre-check reproduces.</summary>
    internal PacketFilter Owner { get; }

    /// <summary>The tree-free observer with the verdicts of <see cref="Owner"/>.</summary>
    internal FilterObserver Observer { get; }

    /// <summary>Skip-mode parse options with <see cref="Observer"/>, by reference (not copied per id).</summary>
    internal ref readonly ParseOptions Options => ref _Options;

    /// <summary>The skip-mode packet reused across pre-check parses.</summary>
    internal Packet? Recycle { get; set; }

    /// <summary>Whether the next id should be pre-checked (false during a backoff).</summary>
    internal bool IsActive => _SkipRemaining == 0;

    #endregion

    #region Adaptive switch

    /// <summary>Counts one pre-check result and closes the window when it is full.</summary>
    internal void Record(bool maybeMatch)
    {
        _Checked++;
        if (maybeMatch)
        {
            _MaybeMatches++;
        }

        if (_Checked < Window)
        {
            return;
        }

        // More than half could match: the skip parse costs more than it saves for a while, and longer each time this
        // repeats. A sparse window restores the short backoff. MaxBackoff * 2 cannot overflow an int.
        if (_MaybeMatches * 2 > Window)
        {
            _SkipRemaining = _NextBackoff;
            _NextBackoff = Math.Min(_NextBackoff * 2, MaxBackoff);
        }
        else
        {
            _NextBackoff = InitialBackoff;
        }

        _Checked = 0;
        _MaybeMatches = 0;
    }

    /// <summary>Counts one id read without a pre-check while the switch is off.</summary>
    internal void OnSkippedId()
    {
        if (_SkipRemaining > 0)
        {
            _SkipRemaining--;
        }
    }

    #endregion
}
