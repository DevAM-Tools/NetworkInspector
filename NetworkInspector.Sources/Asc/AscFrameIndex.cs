// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Frame id → byte offset of the frame line (both backends). The line type and an absolute line time are re-derived
/// from the line itself; the relative clock, which non-frame lines also move, is stored as nanoseconds. Clocks that do
/// not fit <see cref="long"/> nanoseconds, and absolute-mode lines whose own timestamp did not fold (they carry the
/// running clock of earlier lines), are stored as overrides.
/// </summary>
/// <remarks>
/// <para>
/// Absolute mode: <see cref="AscTimestamp.TryFold"/> returns the line's own time and ignores the running clock, so a
/// folded line re-derives its clock from its bytes alone and costs no clock storage. Relative mode: the stored clock
/// for steadily spaced lines costs about 1.5 B per frame (<see cref="PackedLongSequence"/>).
/// </para>
/// <para>
/// Read windows (disk backend): one byte per group of 64 frames holds the shift of a power-of-two window that covers
/// the record of every frame in the group — the line through its line end, and for Ethernet the continuation lines
/// plus the whole line that ends the packet. One positional read of that window therefore always holds everything
/// the parser looks at, at 1/64 B per frame. A per-frame length would cost more than the whole offset index (about
/// 0.4 B per frame); a per-file maximum would make every CAN read as large as the longest Ethernet record.
/// </para>
/// <para>
/// <b>Thread-safety:</b> built completely at open (ASC sources always scan in full) by one thread and read-only
/// afterwards; the source publishes it through a readonly field before any reader can call <c>FrameById</c>.
/// </para>
/// </remarks>
internal sealed class AscFrameIndex
{
    #region Constants

    /// <summary>Smallest read window: 64 bytes (shorter windows save nothing on a positional read).</summary>
    internal const int MinWindowShift = 6;

    /// <summary>
    /// Largest read window: 128 KiB. Covers the longest indexed disk line (<see cref="AscSourceOptions.MaxLineLength"/>
    /// plus its line end) and every Ethernet record (<see cref="Format.AscEthernetParser.MaxRecordBytes"/>).
    /// </summary>
    internal const int MaxWindowShift = 17;

    #endregion

    #region Fields

    private readonly PackedLongSequence _LineOffsets = new();

    /// <summary>Read-window shift per group of <see cref="PackedLongSequence.GroupSize"/> frames; null when not tracked.</summary>
    private byte[]? _WindowShifts;

    /// <summary>Relative clock per frame in nanoseconds (0 where an override applies); null in absolute mode.</summary>
    private readonly PackedLongSequence? _ClockNanos;

    /// <summary>Clocks that cannot be re-derived or stored as nanoseconds, in ascending frame order. Normally empty.</summary>
    private readonly List<(int FrameIndex, AscTimestamp Clock)> _ClockOverrides = [];

    #endregion

    #region Constructors

    /// <summary>Creates an empty index.</summary>
    /// <param name="relativeClock">True when the header says <c>timestamps relative</c>.</param>
    /// <param name="trackReadWindows">
    /// True for the disk backend, which re-reads records from the file; the in-memory backend slices its buffer and needs
    /// no windows.
    /// </param>
    internal AscFrameIndex(bool relativeClock, bool trackReadWindows = false)
    {
        _ClockNanos = relativeClock ? new PackedLongSequence() : null;
        _WindowShifts = trackReadWindows ? new byte[16] : null;
    }

    #endregion

    #region Properties

    /// <summary>Number of indexed frames.</summary>
    internal int Count => _LineOffsets.Count;

    /// <summary>
    /// Bytes the index uses (sequences, read windows, one 24 B tuple per clock override, without list slack). Builder
    /// thread (tests, diagnostics).
    /// </summary>
    internal long UsedBytes =>
        _LineOffsets.UsedBytes + (_ClockNanos?.UsedBytes ?? 0) + (_WindowShifts?.Length ?? 0)
        + (_ClockOverrides.Count * (long)Unsafe.SizeOf<(int FrameIndex, AscTimestamp Clock)>());

    #endregion

    #region Public API

    /// <summary>Adds a frame line. Builder thread.</summary>
    /// <param name="lineOffset">Byte offset of the line start in the file (or the in-memory buffer).</param>
    /// <param name="clock">The clock the scanner assigned to the frame.</param>
    /// <param name="folded">The scanner's <see cref="AscTimestamp.TryFold"/> result for that line.</param>
    /// <exception cref="InvalidOperationException">The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.</exception>
    internal void Add(long lineOffset, AscTimestamp clock, bool folded)
    {
        int frameIndex = _LineOffsets.Count;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(frameIndex, "frame");

        if (_ClockNanos is not null)
        {
            // Relative: the running sum; an unrepresentable sum is kept exactly as an override.
            if (_TryToNanos(clock, out long nanos))
            {
                _ClockNanos.Append(nanos);
            }
            else
            {
                _ClockNanos.Append(0);
                _ClockOverrides.Add((frameIndex, clock));
            }
        }
        else if (!folded)
        {
            // Absolute, but the line's own time did not fold: the scanner used the clock of an earlier line.
            _ClockOverrides.Add((frameIndex, clock));
        }

        _LineOffsets.Append(lineOffset);
    }

    /// <summary>
    /// Records that the record of <paramref name="frameIndex"/> spans <paramref name="bytes"/> bytes from its line offset
    /// (the line end included). Raises the read window of the frame's group to the next power of two, at least
    /// 2^<see cref="MinWindowShift"/>; spans beyond 2^<see cref="MaxWindowShift"/> are clamped, because no reader looks
    /// further. No-op when windows are not tracked. Builder thread.
    /// </summary>
    /// <param name="frameIndex">An index already passed to <see cref="Add"/>.</param>
    /// <param name="bytes">Record span in bytes; values below 1 count as 1.</param>
    internal void RequireReadBytes(int frameIndex, long bytes)
    {
        if (_WindowShifts is null)
        {
            return;
        }

        // Ceiling log2 of the clamped span: 1 → 0, 2 → 1, 65 → 7, 2^17 → 17. The clamp keeps the uint cast exact.
        uint span = (uint)Math.Clamp(bytes, 1L, 1L << MaxWindowShift);
        int shift = Math.Max(MinWindowShift, 32 - BitOperations.LeadingZeroCount(span - 1));

        int group = frameIndex >> PackedLongSequence.GroupShift;
        if (group >= _WindowShifts.Length)
        {
            Array.Resize(ref _WindowShifts, Math.Max(group + 1, _WindowShifts.Length * 2));
        }

        if (shift > _WindowShifts[group])
        {
            _WindowShifts[group] = (byte)shift;
        }
    }

    /// <summary>
    /// Read window of <paramref name="frameIndex"/> in bytes: a power of two that covers the frame's record. The largest
    /// window when windows are not tracked. Any thread after the build; the caller passes an index below
    /// <see cref="Count"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadWindow(int frameIndex)
    {
        byte[]? shifts = _WindowShifts;
        int group = frameIndex >> PackedLongSequence.GroupShift;
        return shifts is null || (uint)group >= (uint)shifts.Length
            ? 1 << MaxWindowShift
            : 1 << shifts[group];
    }

    /// <summary>Seals the sequences and trims the read windows to the group count. Called once at the end of the open-time scan.</summary>
    internal void Complete()
    {
        _LineOffsets.Complete();
        _ClockNanos?.Complete();
        if (_WindowShifts is not null)
        {
            int groups = (_LineOffsets.Count + PackedLongSequence.GroupSize - 1) >> PackedLongSequence.GroupShift;
            Array.Resize(ref _WindowShifts, groups);
        }
    }

    /// <summary>Byte offset of the line of frame <paramref name="frameIndex"/>; false for an id outside the index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetLineOffset(int frameIndex, out long lineOffset) =>
        _LineOffsets.TryGetCached(frameIndex, out lineOffset);

    /// <summary>
    /// The clock of frame <paramref name="frameIndex"/>: an override if present; else the stored relative nanoseconds;
    /// else (absolute mode) the line's own time, folded exactly as the scanner folded it.
    /// </summary>
    /// <param name="frameIndex">Frame id.</param>
    /// <param name="trimmedLine">The trimmed frame line, re-derived from the indexed offset.</param>
    /// <param name="clock">The frame clock when the method returns true.</param>
    /// <returns>False when the line no longer starts with a timestamp (the bytes changed).</returns>
    internal bool TryGetClock(int frameIndex, ReadOnlySpan<byte> trimmedLine, out AscTimestamp clock)
    {
        if (_TryFindOverride(frameIndex, out clock))
        {
            return true;
        }

        if (_ClockNanos is not null)
        {
            if (!_ClockNanos.TryGetCached(frameIndex, out long nanos))
            {
                return false;
            }

            clock = _FromNanos(nanos);
            return true;
        }

        // Absolute: TryFold replaces the running clock with the line time, so a local start value gives the same clock.
        AscTimestamp running = default;
        return AscTimestamp.TryFold(ref running, relative: false, trimmedLine, out clock);
    }

    #endregion

    #region Private helpers

    /// <summary>Binary search in the ascending override list.</summary>
    private bool _TryFindOverride(int frameIndex, out AscTimestamp clock)
    {
        clock = default;
        int low = 0;
        int high = _ClockOverrides.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int candidate = _ClockOverrides[middle].FrameIndex;
            if (candidate == frameIndex)
            {
                clock = _ClockOverrides[middle].Clock;
                return true;
            }

            if (candidate < frameIndex)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    /// <summary><c>WholeSeconds · 10^9 + Nanoseconds</c> with an overflow check (negative clocks allowed).</summary>
    private static bool _TryToNanos(AscTimestamp clock, out long nanos)
    {
        nanos = 0;
        if (clock.WholeSeconds > (long.MaxValue / 1_000_000_000L) - 1 || clock.WholeSeconds < (long.MinValue / 1_000_000_000L) + 1)
        {
            return false;
        }

        nanos = (clock.WholeSeconds * 1_000_000_000L) + clock.Nanoseconds;
        return true;
    }

    /// <summary>Inverse of <see cref="_TryToNanos"/>: floor division keeps the fraction in 0..999 999 999.</summary>
    private static AscTimestamp _FromNanos(long nanos)
    {
        long seconds = Math.DivRem(nanos, 1_000_000_000L, out long fraction);
        if (fraction < 0)
        {
            seconds--;
            fraction += 1_000_000_000L;
        }

        return new AscTimestamp(seconds, (int)fraction);
    }

    #endregion
}
