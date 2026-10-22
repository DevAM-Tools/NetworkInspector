// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Where the object of one BLF frame lives.
/// </summary>
/// <param name="ContainerOffset">
/// File offset of the LOG_CONTAINER whose decompressed bytes hold the object start, or of the raw (file-level) object.
/// </param>
/// <param name="ObjectOffset">Offset of the object inside the container's own decompressed bytes; −1 for a raw object.</param>
/// <param name="ObjectLength">
/// Object length for an object that continues into the next container (it cannot be read from a split header);
/// −1 when the length is read from the object header.
/// </param>
internal readonly record struct BlfFrameLocation(long ContainerOffset, int ObjectOffset, int ObjectLength);

/// <summary>
/// A reader's cache of the container row of its previous frame (<see cref="BlfFrameIndex.TryGetLocation(int, ref BlfRowCursor, out BlfFrameLocation)"/>).
/// </summary>
/// <remarks>Plain value owned by one reader; not shared between threads.</remarks>
internal struct BlfRowCursor
{
    /// <summary>Row index, or −1 when the cursor holds no row.</summary>
    internal int Row;

    /// <summary>Virtual start of the row.</summary>
    internal long Start;

    /// <summary>Virtual start of the next row; <see cref="long.MaxValue"/> while the row was the last published one.</summary>
    internal long End;

    /// <summary>File offset of the row's container.</summary>
    internal long FileOffset;

    /// <summary>A cursor without a row.</summary>
    internal static BlfRowCursor Empty => new() { Row = -1 };
}

/// <summary>
/// Frame id → virtual position. Every decoded container gets a row (virtual start, file offset); the virtual start is
/// the running sum of the decompressed lengths of all decoded containers, so the position of a container object is
/// <c>row virtual start + inner offset</c> and positions ascend in frame order, also across container boundaries.
/// Objects split across containers keep their length; raw (file-level) objects carry their file offset with
/// <see cref="RawFlag"/>. Object type, channel, length, and timestamp are re-read from the object header by the
/// scanner's parser.
/// </summary>
/// <remarks>
/// <para>
/// Constant-size objects (classic CAN) give a constant position step, so the positions cost about 0.4 B per frame
/// (<see cref="PackedLongSequence"/>). Rows and spanning entries grow with containers, not with frames.
/// </para>
/// <para>
/// <b>Thread-safety:</b> single writer (the scanner); any number of readers. Rows and spanning entries are appended
/// before the position that refers to them, and each sequence publishes its count after its value, so a published
/// position always resolves.
/// </para>
/// </remarks>
internal sealed class BlfFrameIndex
{
    #region Constants

    /// <summary>Marks a raw object position: <c>RawFlag | fileOffset</c> (file offsets stay below 2^62).</summary>
    internal const long RawFlag = 1L << 62;

    #endregion

    #region Fields

    private readonly PackedLongSequence _Positions = new();
    private readonly PackedLongSequence _RowVirtualStarts = new();
    private readonly PackedLongSequence _RowFileOffsets = new();
    private readonly PackedLongSequence _SpanningFrames = new();
    private readonly PackedLongSequence _SpanningLengths = new();
    private long _VirtualCursor;

    #endregion

    #region Properties

    /// <summary>Number of indexed frames. Any thread.</summary>
    internal int Count => _Positions.Count;

    /// <summary>Bytes the index uses. Writer thread (tests, diagnostics).</summary>
    internal long UsedBytes => _Positions.UsedBytes + _RowVirtualStarts.UsedBytes + _RowFileOffsets.UsedBytes
        + _SpanningFrames.UsedBytes + _SpanningLengths.UsedBytes;

    #endregion

    #region Public API — writer

    /// <summary>Registers a decoded container (its own bytes, without a carry prefix) and returns its row. Single writer.</summary>
    /// <exception cref="InvalidOperationException">The decompressed bytes of all containers reach 2^62 (the raw flag).</exception>
    internal int AddContainer(long fileOffset, int decompressedLength)
    {
        // Virtual positions must stay below the raw flag; unreachable in practice (4 EiB of decoded containers).
        if (_VirtualCursor + decompressedLength >= RawFlag)
        {
            throw new InvalidOperationException("BLF frame index: decompressed container bytes exceed the virtual position range.");
        }

        int row = _RowVirtualStarts.Count;

        // File offset first: a reader that finds the row by its virtual start also finds its file offset.
        _RowFileOffsets.Append(fileOffset);
        _RowVirtualStarts.Append(_VirtualCursor);
        _VirtualCursor += decompressedLength;
        return row;
    }

    /// <summary>Indexes an object that lies completely inside container <paramref name="row"/>. Single writer.</summary>
    /// <exception cref="InvalidOperationException">The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.</exception>
    internal void PushContainerObject(int row, int innerOffset)
    {
        _ = _RowVirtualStarts.TryGet(row, out long start);
        _PushPosition(start + innerOffset);
    }

    /// <summary>
    /// Indexes an object that starts in container <paramref name="row"/> and continues in the next container. Single
    /// writer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.</exception>
    internal void PushSpanningObject(int row, int innerOffset, int objectLength)
    {
        // The spanning entry is published before the position, so a reader that sees the frame also sees its length.
        _SpanningFrames.Append(_Positions.Count);
        _SpanningLengths.Append(objectLength);
        PushContainerObject(row, innerOffset);
    }

    /// <summary>Indexes a raw (file-level) object at <paramref name="fileOffset"/>. Single writer.</summary>
    /// <exception cref="InvalidOperationException">The next frame index would exceed <see cref="ArrayIndexIdRange.MaxValue"/>.</exception>
    internal void PushRawObject(long fileOffset) => _PushPosition(RawFlag | fileOffset);

    /// <summary>Seals all sequences once the scan ended. Idempotent. Single writer.</summary>
    internal void Complete()
    {
        _Positions.Complete();
        _RowVirtualStarts.Complete();
        _RowFileOffsets.Complete();
        _SpanningFrames.Complete();
        _SpanningLengths.Complete();
    }

    #endregion

    #region Public API — readers

    /// <summary>Resolves frame <paramref name="frameIndex"/>. Any thread; false for an id not indexed yet.</summary>
    internal bool TryGetLocation(int frameIndex, out BlfFrameLocation location)
    {
        // Uncached on purpose: equal-size objects give width-0 groups, whose direct decode is cheaper than the
        // per-thread decoded-group cache (measured 15–21 % slower sequential FrameById with it).
        location = default;
        if (!_Positions.TryGet(frameIndex, out long position))
        {
            return false;
        }

        if ((position & RawFlag) != 0)
        {
            location = new BlfFrameLocation(position & ~RawFlag, -1, -1);
            return true;
        }

        // Random access: one binary search, no cursor bookkeeping.
        if (!_TryFindRow(position, out _, out long fileOffset, out long virtualStart))
        {
            return false;
        }

        location = _Locate(frameIndex, position, fileOffset, virtualStart);
        return true;
    }

    /// <summary>
    /// Resolves frame <paramref name="frameIndex"/> using <paramref name="cursor"/>, the container row of the caller's
    /// previous frame: a position inside that row's virtual range resolves without any row lookup; otherwise the row is
    /// searched and the cursor moves to it. A sequential reader keeps one cursor. Any thread with its own cursor.
    /// </summary>
    internal bool TryGetLocation(int frameIndex, ref BlfRowCursor cursor, out BlfFrameLocation location)
    {
        location = default;
        if (!_Positions.TryGet(frameIndex, out long position))
        {
            return false;
        }

        if ((position & RawFlag) != 0)
        {
            location = new BlfFrameLocation(position & ~RawFlag, -1, -1);
            return true;
        }

        if (!_IsInCursorRow(position, ref cursor) && !_TryMoveCursor(position, ref cursor))
        {
            return false;
        }

        location = _Locate(frameIndex, position, cursor.FileOffset, cursor.Start);
        return true;
    }

    #endregion

    #region Private helpers

    /// <summary>Builds the location of a container object from its row; adds the stored length of a spanning object.</summary>
    private BlfFrameLocation _Locate(int frameIndex, long position, long fileOffset, long virtualStart)
    {
        int length = _TryFindSpanningLength(frameIndex, out int spanningLength)
            ? spanningLength
            : -1;
        return new BlfFrameLocation(fileOffset, checked((int)(position - virtualStart)), length);
    }

    private void _PushPosition(long position)
    {
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(_Positions.Count, "frame");
        _Positions.Append(position);
    }

    /// <summary>
    /// True when <paramref name="position"/> lies in the cursor's row. A cursor row that was the last published row has
    /// an open end; once a next row exists its start closes the range. Positions only refer to published rows, so a
    /// position at or above the start of the newest row belongs to it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _IsInCursorRow(long position, ref BlfRowCursor cursor)
    {
        if (cursor.Row < 0 || position < cursor.Start)
        {
            return false;
        }

        // Closed range: the next row's start is known.
        if (cursor.End != long.MaxValue)
        {
            return position < cursor.End;
        }

        // Open range: still the newest row, or a next row appeared and closes it now.
        if (!_RowVirtualStarts.TryGet(cursor.Row + 1, out long next))
        {
            return true;
        }

        cursor.End = next;
        return position < next;
    }

    /// <summary>Moves <paramref name="cursor"/> to the row that holds <paramref name="position"/>.</summary>
    private bool _TryMoveCursor(long position, ref BlfRowCursor cursor)
    {
        if (!_TryFindRow(position, out int row, out long fileOffset, out long virtualStart))
        {
            return false;
        }

        cursor.Row = row;
        cursor.Start = virtualStart;
        cursor.End = _RowVirtualStarts.TryGet(row + 1, out long next)
            ? next
            : long.MaxValue;
        cursor.FileOffset = fileOffset;
        return true;
    }

    /// <summary>
    /// Greatest row whose virtual start is ≤ <paramref name="position"/> (binary search over the published rows). Empty
    /// containers share their virtual start with the next row; the greatest match is the row that holds bytes.
    /// </summary>
    private bool _TryFindRow(long position, out int row, out long fileOffset, out long virtualStart)
    {
        fileOffset = 0;
        virtualStart = 0;
        int low = 0;
        int high = _RowVirtualStarts.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            _ = _RowVirtualStarts.TryGet(middle, out long start);
            if (start <= position)
            {
                found = middle;
                virtualStart = start;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        row = found;
        return found >= 0 && _RowFileOffsets.TryGet(found, out fileOffset);
    }

    /// <summary>Binary search of <paramref name="frameIndex"/> in the spanning frame list; most files have none.</summary>
    private bool _TryFindSpanningLength(int frameIndex, out int length)
    {
        length = -1;
        int low = 0;
        int high = _SpanningFrames.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            _ = _SpanningFrames.TryGet(middle, out long frame);
            if (frame == frameIndex)
            {
                if (!_SpanningLengths.TryGet(middle, out long value))
                {
                    return false;
                }

                length = (int)value;
                return true;
            }

            if (frame < frameIndex)
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

    #endregion
}
