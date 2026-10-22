// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Helpers;

/// <summary>
/// Checks that <see cref="IRandomAccessFrameSource.FrameById"/> returns, for every frame id, exactly the frame the first
/// sequential read returned: same bytes, timestamp, interface, and link type.
/// </summary>
/// <remarks>This type is stateless and thread-safe.</remarks>
internal static class RandomAccessEquivalence
{
    #region Public API

    /// <summary>Starts <paramref name="source"/> with a fresh registry and reads every frame with <c>NextFrame</c>.</summary>
    internal static List<Frame> StartAndReadAll(IFrameSource source)
    {
        _ = SourceTestFixture.InitializeAndStartSource(source);
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>True when both frames carry the same id, bytes, timestamp, interface, and link type.</summary>
    internal static bool SameFrame(Frame expected, Frame actual) =>
        expected.Id.Value == actual.Id.Value
        && expected.Timestamp.AsNanos == actual.Timestamp.AsNanos
        && expected.InterfaceId.Equals(actual.InterfaceId)
        && expected.LinkType == actual.LinkType
        && expected.Data.Span.SequenceEqual(actual.Data.Span);

    /// <summary>
    /// Ranges for range reads over a source of <paramref name="frameCount"/> frames: starts at −1, 0, 1, 63, 64,
    /// <paramref name="frameCount"/> − 1, <paramref name="frameCount"/>, and the largest valid id, each with the lengths
    /// 0, 1, 63, 64, 65, and 200 (so ranges cross the chunk size, the end of the source, and the end of the id range).
    /// </summary>
    internal static List<(string Name, FrameId First, int Length)> BatchRanges(int frameCount)
    {
        List<(string, FrameId, int)> ranges = [];
        foreach (int first in new[] { -1, 0, 1, 63, 64, frameCount - 1, frameCount, ArrayIndexIdRange.MaxValue })
        {
            foreach (int length in new[] { 0, 1, 63, 64, 65, 200 })
            {
                ranges.Add((Invariant($"first-{first}-length-{length}"), new FrameId(first), length));
            }
        }

        return ranges;
    }

    /// <summary>
    /// Reads the range <paramref name="first"/> … <paramref name="first"/> + <paramref name="length"/> − 1 with
    /// <c>FramesById</c> and with one <c>FrameById</c> per id and returns the first difference as text (an entry that
    /// differs from the single read, a wrong return value, or a written entry outside the range), or null when all agree.
    /// </summary>
    internal static string? FindBatchMismatch(IRandomAccessFrameSource source, FrameId first, int length, string label)
    {
        // The range is a slice of a larger buffer whose outer entries hold a sentinel frame from the first entry's read.
        Frame?[] buffer = new Frame?[length + 2];
        Frame? sentinel = source.FrameById(new FrameId(0));
        buffer[0] = sentinel;
        buffer[^1] = sentinel;
        int found = source.FramesById(first, buffer.AsSpan(1, length));
        if (!_SameOrBothNull(sentinel, buffer[0]) || !_SameOrBothNull(sentinel, buffer[^1]))
        {
            return Invariant($"{label}: FramesById wrote outside its range.");
        }

        int expectedFound = 0;
        for (int i = 0; i < length; i++)
        {
            long id = (long)first.Value + i;
            Frame? expected = id >= 0 && id <= ArrayIndexIdRange.MaxValue
                ? source.FrameById(new FrameId((int)id))
                : null;
            expectedFound += expected is null ? 0 : 1;
            if (!_SameOrBothNull(expected, buffer[1 + i]))
            {
                return Invariant($"{label}: entry {i} (id {id}) differs from FrameById.");
            }
        }

        return found == expectedFound
            ? null
            : Invariant($"{label}: FramesById returned {found}, expected {expectedFound}.");
    }

    /// <summary>
    /// Requests every sequential frame by id in reverse order and returns the first mismatch as text, or null when all
    /// frames are equal. Also checks that ids the sequential read skipped stay unavailable.
    /// </summary>
    /// <param name="source">The started source after a complete sequential read.</param>
    /// <param name="sequential">Frames of that read, in order.</param>
    /// <param name="label">Case name for the message.</param>
    internal static string? FindMismatch(IRandomAccessFrameSource source, List<Frame> sequential, string label)
    {
        HashSet<int> returned = [];
        for (int i = sequential.Count - 1; i >= 0; i--)
        {
            Frame expected = sequential[i];
            returned.Add(expected.Id.Value);
            Frame? actual = source.FrameById(expected.Id);
            if (actual is null)
            {
                return $"{label}: FrameById({expected.Id.Value}) returned null.";
            }

            if (!SameFrame(expected, actual.Value))
            {
                return $"{label}: FrameById({expected.Id.Value}) differs from the sequential frame.";
            }
        }

        // After a complete sequential read every file source knows its frame count; null means the read ended early.
        if (source.EstimatedFrameCount is not int estimated)
        {
            return $"{label}: EstimatedFrameCount is null after the sequential read ended (stream ended before the file).";
        }

        // An id the sequential read skipped (its frame could not be built) must not appear by id either.
        for (int id = 0; id < estimated; id++)
        {
            if (!returned.Contains(id) && source.FrameById(new FrameId(id)) is not null)
            {
                return $"{label}: FrameById({id}) returned a frame the sequential read skipped.";
            }
        }

        return null;
    }

    #endregion

    #region Private helpers

    private static bool _SameOrBothNull(Frame? expected, Frame? actual) =>
        expected is { } e
            ? actual is { } a && SameFrame(e, a)
            : actual is null;

    #endregion
}
