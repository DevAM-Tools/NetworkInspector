// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Maintains round-robin iteration state over a fixed set of frame sources.
/// Sources that become exhausted are removed from rotation; the iterator always
/// advances to the next non-exhausted source so callers never need to check
/// activity state inline.
/// </summary>
/// <remarks>
/// Thread-safety: single-threaded use only; the CLI processes sources on one thread.
/// </remarks>
internal sealed class RoundRobinSourceIterator
{
    #region Fields

    private readonly IFrameSource[] _Sources;

    /// <summary>
    /// Tracks which source slots are still active (not exhausted).
    /// Index corresponds to position in <see cref="_Sources"/>.
    /// </summary>
    private readonly bool[] _Active;

    private int _ActiveCount;
    private int _CurrentIndex;

    #endregion

    #region Public API

    /// <summary>Initialises the iterator over <paramref name="sources"/>.</summary>
    /// <param name="sources">Non-empty list of sources to iterate in round-robin order.</param>
    internal RoundRobinSourceIterator(List<IFrameSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _Sources = [.. sources];
        _Active = new bool[_Sources.Length];
        Array.Fill(_Active, true);
        _ActiveCount = _Sources.Length;
        _CurrentIndex = 0;
    }

    /// <summary>Whether any source remains active (has not been exhausted).</summary>
    internal bool HasActive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ActiveCount > 0;
    }

    /// <summary>
    /// Returns the current active source.
    /// Only valid when <see cref="HasActive"/> is <c>true</c>.
    /// </summary>
    internal IFrameSource Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Sources[_CurrentIndex];
    }

    /// <summary>
    /// Marks the current source as exhausted and advances to the next active source.
    /// Must only be called when <see cref="HasActive"/> is <c>true</c>.
    /// After this call <see cref="Current"/> points to the next active source (if any).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkCurrentExhaustedAndAdvance()
    {
        _Active[_CurrentIndex] = false;
        _ActiveCount--;
        if (_ActiveCount > 0)
        {
            _MoveToNextActive();
        }
    }

    /// <summary>
    /// Advances to the next active source in round-robin order.
    /// Must only be called when <see cref="HasActive"/> is <c>true</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Advance() => _MoveToNextActive();

    #endregion

    #region Private Helpers

    /// <summary>
    /// Scans forward (wrapping) from the current position until an active slot is found.
    /// The scan is bounded by <c>_Sources.Length</c> steps; unreachable when
    /// <see cref="_ActiveCount"/> is greater than zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _MoveToNextActive()
    {
        int count = _Sources.Length;
        for (int i = 0; i < count; i++)
        {
            _CurrentIndex = (_CurrentIndex + 1) % count;
            if (_Active[_CurrentIndex])
            {
                return;
            }
        }
    }

    #endregion
}
