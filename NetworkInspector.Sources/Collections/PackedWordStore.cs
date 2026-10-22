// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Collections;

/// <summary>
/// Append-only <see cref="ulong"/> words in 64 KiB chunks (below the large-object threshold) addressed by a
/// <see cref="long"/> word index. A reservation never straddles a chunk, so readers always get one span.
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> single writer (<see cref="Reserve"/>, <see cref="GetWritable"/>); any number of readers of
/// words the owner has published. The owner publishes with a volatile write after filling the words (release); a
/// reader that acquired that publish sees the chunk and its words. The store itself publishes nothing.
/// Capacity: 262 144 chunks of 8 192 words (about 2.1 billion words, the <see cref="ChunkedOuterArray{TChunk}"/> limit);
/// <see cref="Reserve"/> throws <see cref="ArgumentOutOfRangeException"/> from the outer array beyond it. Only
/// adversarial 64-bit values over about 2^31 entries reach that limit.
/// </remarks>
internal sealed class PackedWordStore
{
    #region Constants

    /// <summary>Log₂ of the words per chunk.</summary>
    internal const int ChunkShift = 13;

    /// <summary>Words per chunk: 8 192 words = 64 KiB.</summary>
    internal const int ChunkWords = 1 << ChunkShift;

    private const long _ChunkMask = ChunkWords - 1;

    #endregion

    #region Fields

    private static readonly Func<ulong[]> _NewChunk = static () => new ulong[ChunkWords];
    private readonly ChunkedOuterArray<ulong[]> _Chunks = new(ChunkShift);
    private long _Used;

    #endregion

    #region Properties

    /// <summary>Words reserved so far, including the unused tails of skipped chunk ends. Writer thread.</summary>
    internal long UsedWords => _Used;

    #endregion

    #region Public API

    /// <summary>Reserves <paramref name="count"/> consecutive words inside one chunk and returns the first index.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is not in 1 … <see cref="ChunkWords"/>, or the store already holds the maximum number of
    /// chunks of the outer array.
    /// </exception>
    internal long Reserve(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, ChunkWords);

        // Skip to the next chunk when the reservation would straddle; the skipped tail stays unused.
        long start = _Used;
        int inChunk = (int)(start & _ChunkMask);
        if (inChunk + count > ChunkWords)
        {
            start += ChunkWords - inChunk;
        }

        // start < 2^31 words here, so the chunk index fits an int; the outer array rejects a chunk beyond its limit.
        int chunkIndex = (int)(start >> ChunkShift);
        _ = _Chunks.GetOrAllocateChunk(chunkIndex, _NewChunk);
        _Used = start + count;
        return start;
    }

    /// <summary>Writable view of reserved words. Writer thread only.</summary>
    /// <remarks>Caller guarantees the range came from one <see cref="Reserve"/> call.</remarks>
    internal Span<ulong> GetWritable(long start, int count) =>
        _Chunks.GetChunk((int)(start >> ChunkShift))!.AsSpan((int)(start & _ChunkMask), count);

    /// <summary>Read-only view of published words. Any thread, after the owner's publish.</summary>
    /// <remarks>Caller guarantees the range lies inside one published reservation.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<ulong> Get(long start, int count) =>
        _Chunks.GetChunk((int)(start >> ChunkShift))!.AsSpan((int)(start & _ChunkMask), count);

    #endregion
}
