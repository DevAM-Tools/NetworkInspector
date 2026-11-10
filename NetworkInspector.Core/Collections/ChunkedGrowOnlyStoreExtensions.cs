// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Dense-store helpers for <see cref="ChunkedGrowOnlyStore{T}"/> reference entries.
/// </summary>
public static class ChunkedGrowOnlyStoreExtensions
{
    /// <summary>
    /// Reads a contiguous range into <paramref name="buffer"/>.
    /// Negative <paramref name="fromIndex"/> offsets produce <see langword="null"/> entries in the
    /// prefix of <paramref name="buffer"/> only; indices past
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/> stop the copy.
    /// Each slot is one lock-free <see cref="ChunkedGrowOnlyStore{T}.Get"/>, so a concurrent writer may be
    /// observed part-way through the range: slots below the published count at the time of their read are returned.
    /// </summary>
    /// <typeparam name="T">Reference entry type; unset slots read as <see langword="null"/>.</typeparam>
    /// <param name="store">The store to read.</param>
    /// <param name="fromIndex">Index of the first slot; may be negative to pad the start of the buffer with holes.</param>
    /// <param name="buffer">Destination; the method never writes beyond its length.</param>
    /// <returns>Number of slots written (may include <see langword="null"/> holes).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public static int ReadRange<T>(this ChunkedGrowOnlyStore<T> store, int fromIndex, Span<T?> buffer)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(store);

        int count = 0;
        int start = fromIndex;
        int bufferOffset = 0;

        // Negative start: the leading part of the buffer corresponds to indices below zero, which do not exist, so
        // it is filled with null holes and the copy starts at index 0 behind them.
        if (start < 0)
        {
            // -int.MinValue overflows in int. The prefix is entirely holes for any
            // buffer this method can fill.
            long magnitude = -(long)start;
            int holes = magnitude >= buffer.Length
                ? buffer.Length
                : (int)magnitude;
            for (int h = 0; h < holes; h++)
            {
                buffer[h] = null;
            }

            count = holes;
            bufferOffset = holes;
            start = 0;
            if (bufferOffset == buffer.Length)
            {
                return count;
            }
        }

        // A start above MaxValue has no slots at all. Without this check MaxValue - start is negative, the unsigned compare
        // below never breaks, the buffer is filled with null holes and reported as read (and start + offset could overflow).
        if (start > Ids.ArrayIndexIdRange.MaxValue)
        {
            return count;
        }

        // Copy the remaining buffer. For start <= MaxValue the unsigned compare stops before start + offset can exceed
        // MaxValue (and so before the int addition below could overflow).
        for (int i = bufferOffset; i < buffer.Length; i++)
        {
            int offset = i - bufferOffset;
            if ((uint)offset > (uint)(Ids.ArrayIndexIdRange.MaxValue - start))
            {
                break;
            }

            int idx = start + offset;
            buffer[i] = store.Get(idx);
            count++;
        }

        return count;
    }
}
