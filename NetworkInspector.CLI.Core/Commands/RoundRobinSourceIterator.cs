// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Maintains round-robin iteration state over a fixed set of frame sources.
/// Sources that become exhausted are removed from rotation; the iterator always
/// advances to the next non-exhausted source so callers never need to check
/// activity state inline.
/// </summary>
/// <remarks>
/// Order: one frame per source per turn, in the order the sources were given. Several inputs are therefore interleaved frame by
/// frame; they are neither concatenated nor merged by timestamp (this is documented behavior of the CLI).
/// Performance: the loops of the commands call <see cref="Current"/> and <see cref="Advance"/> once per frame, so the type
/// allocates only in its constructor (three small arrays) and its members are tiny and marked for inlining.
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

    /// <summary>
    /// Tracks which source slots failed to read since they last delivered a frame (see <see cref="RecordReadFailureAndAdvance"/>).
    /// Index corresponds to position in <see cref="_Sources"/>.
    /// </summary>
    private readonly bool[] _FailedSinceFrame;

    private int _ActiveCount;
    private int _CurrentIndex;

    #endregion

    #region Public API

    /// <summary>Initialises the iterator over <paramref name="sources"/>.</summary>
    /// <param name="sources">The sources to iterate in round-robin order; an empty list gives an iterator without active sources.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    internal RoundRobinSourceIterator(List<IFrameSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // A snapshot: the caller keeps its list (it disposes the sources from it), and later changes to the list must not move
        // the rotation.
        _Sources = [.. sources];
        _Active = new bool[_Sources.Length];
        Array.Fill(_Active, true);
        _FailedSinceFrame = new bool[_Sources.Length];
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
    /// Advances to the next active source in round-robin order. The loops call it after a frame was handled, filtered out, or
    /// skipped in tolerant mode, so every source gets one turn per round even when its frames are rejected.
    /// Must only be called when <see cref="HasActive"/> is <c>true</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Advance() => _MoveToNextActive();

    /// <summary>
    /// Notes that the current source delivered a frame, so a later read failure of it counts as a first failure again. The loops
    /// call it for every frame they read. Must only be called when <see cref="HasActive"/> is <c>true</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordFrameRead() => _FailedSinceFrame[_CurrentIndex] = false;

    /// <summary>
    /// Handles a read failure of the current source in tolerant mode and advances to the next active source. The first failure
    /// since the source last delivered a frame is skipped: the source stays in the rotation and is asked again. A failure
    /// right after a failure (no frame in between) is treated as a source that cannot move past the problem: it leaves the
    /// rotation like an exhausted source. Without this rule a source that throws on every read would keep the loop running
    /// forever, because the loop would skip the same failure again and again.
    /// Must only be called when <see cref="HasActive"/> is <c>true</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the source left the rotation because it failed again without delivering a frame;
    /// <see langword="false"/> when only the failed read is skipped.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool RecordReadFailureAndAdvance()
    {
        if (_FailedSinceFrame[_CurrentIndex])
        {
            MarkCurrentExhaustedAndAdvance();
            return true;
        }

        _FailedSinceFrame[_CurrentIndex] = true;
        _MoveToNextActive();
        return false;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Scans forward (wrapping) from the current position until an active slot is found.
    /// The scan is bounded by <c>_Sources.Length</c> steps, so it ends even when no source is active (the position then returns
    /// to where it started). While <see cref="_ActiveCount"/> is greater than zero an active slot is always found within the bound.
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
