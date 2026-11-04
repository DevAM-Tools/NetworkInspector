// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>Location of one byte payload inside a <see cref="BatchByteArena"/>.</summary>
/// <param name="Chunk">Index of the chunk that holds the payload.</param>
/// <param name="Offset">Offset of the payload in that chunk.</param>
/// <param name="Length">Payload length in bytes; <c>0</c> for an empty payload (then <paramref name="Chunk"/> is not used).</param>
internal readonly record struct ArenaRef(int Chunk, int Offset, int Length);

/// <summary>Position of a <see cref="BatchByteArena"/> as returned by <see cref="BatchByteArena.Mark"/>.</summary>
/// <param name="ChunkCount">Number of chunks at the time of the mark.</param>
/// <param name="Used">Bytes used in the last chunk at the time of the mark.</param>
internal readonly record struct ArenaMark(int ChunkCount, int Used);

/// <summary>
/// Append-only byte arena for one export batch: payloads (frame bytes and <see cref="FieldType.Bytes"/> values) are copied
/// into 1 MiB chunks rented from <see cref="ArrayPool{T}.Shared"/>, so accumulating them allocates nothing once the pool is warm
/// (a <c>byte[]</c> per payload would be garbage that survives until the flush). A payload larger than a chunk gets a rented
/// chunk of its own.
/// <para>
/// <b>Lifetime model — batch arena.</b> Every payload of a batch dies at the same moment: when the batch has been written,
/// <see cref="Reset"/> forgets all of them at once and the chunks go back to the pool (all but the first full-size chunk, which
/// the next batch reuses); <see cref="Mark"/> and <see cref="TruncateTo"/> undo the payloads of a packet that could not be added
/// completely. There is exactly one owner (the batch) and nothing may keep a span after <see cref="Reset"/>.
/// </para>
/// <para>
/// <b>Why not the Core slab allocator:</b> <c>NetworkInspector.Core.Fields.SlabAllocator&lt;T&gt;</c> follows the other model
/// (GC regions): many independent owners share one array, nothing is ever reset, and the garbage collector frees a slab when
/// the last owner dies. That is right for field tables and frames, whose owners (packets, session frame stores) live for
/// unrelated periods, and wrong for a batch: it can neither return memory to a pool nor roll back a half-added packet.
/// Merging both models into one generic core would put reset, rollback, and pool branches (or a policy indirection) on the
/// measured hot path of field storage for no shared lifetime rule, so the code base keeps exactly one implementation per model:
/// this type for batch lifetimes, <c>SlabAllocator&lt;T&gt;</c> (with the <c>FrameSlabAllocator</c> policy wrapper in Sources)
/// for GC regions. A new allocation need must pick one of the two, not add a third.
/// </para>
/// <para>
/// A payload is copied because the packet buffer behind it may be recycled after <c>OnPacket</c> returns, while the batch
/// keeps the rows until the flush.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one <see cref="ColumnarPacketBatch"/>, which is used by one thread at a
/// time. The hand-off of a full batch to a writer thread must publish it with a full barrier, so that the writer sees every
/// byte the filling thread copied in.
/// </para>
/// </summary>
internal sealed class BatchByteArena : IDisposable
{
    #region Configuration and state

    // Sizing choice, not a limit: large enough that a batch of typical frames (tens of KiB each at most) needs few chunks, and
    // small enough that a chunk wastes little when a batch is short. The shared pool reuses arrays of this size, so every chunk
    // goes back to the pool for the next batch (Sync: PooledBufferTests.TheSharedPoolReusesAnArrayOfThisSizeOnTheSameThread).

    /// <summary>Size in bytes of a regular chunk (1 MiB); a payload larger than this gets a chunk of its own.</summary>
    private const int _ChunkSize = 1 << 20;

    /// <summary>The rented chunks in allocation order; the last one receives the next payload.</summary>
    private readonly List<byte[]> _Chunks = new(16);

    /// <summary>Bytes used in the last chunk. An oversized chunk is marked as full so the next payload starts a new chunk.</summary>
    private int _Used;

    #endregion

    #region Appending and reading

    /// <summary>Copies <paramref name="payload"/> into the arena.</summary>
    /// <param name="payload">Bytes to store; may be empty.</param>
    /// <returns>The location of the stored copy.</returns>
    internal ArenaRef Append(ReadOnlySpan<byte> payload)
    {
        // An empty payload needs no chunk; Get answers it with an empty span.
        if (payload.IsEmpty)
        {
            return default;
        }

        // The last chunk is used while it has room; otherwise a new chunk is rented. A payload that exceeds the regular chunk
        // size is rented exactly (the pool rounds up) and counts as full, so later small payloads do not append behind it.
        bool needsChunk = _Chunks.Count == 0 || _Chunks[^1].Length - _Used < payload.Length;
        if (needsChunk)
        {
            byte[] chunk = ArrayPool<byte>.Shared.Rent(Math.Max(_ChunkSize, payload.Length));
            _Chunks.Add(chunk);
            _Used = 0;
        }

        byte[] target = _Chunks[^1];
        int offset = _Used;
        payload.CopyTo(target.AsSpan(offset));
        _Used = offset + payload.Length;

        // Rare oversized chunk: nothing else may be appended behind it.
        if (target.Length != _ChunkSize)
        {
            _Used = target.Length;
        }

        return new ArenaRef(_Chunks.Count - 1, offset, payload.Length);
    }

    /// <summary>Returns the stored bytes of <paramref name="reference"/>.</summary>
    /// <param name="reference">A location returned by <see cref="Append"/> since the last <see cref="Reset"/>.</param>
    /// <returns>The stored bytes; empty for an empty payload.</returns>
    /// <remarks>
    /// The span is writable only because the DuckDB appender API takes <c>Span&lt;byte&gt;</c>; callers must not write.
    /// It is valid until the next <see cref="Reset"/>, <see cref="TruncateTo"/> or <see cref="Dispose"/>.
    /// </remarks>
    internal Span<byte> Get(ArenaRef reference) =>
        reference.Length == 0
            ? Span<byte>.Empty
            : _Chunks[reference.Chunk].AsSpan(reference.Offset, reference.Length);

    /// <summary>The stored bytes of <paramref name="reference"/> as read-only memory over the arena chunk (no copy).</summary>
    /// <param name="reference">A location returned by <see cref="Append"/> since the last <see cref="Reset"/>.</param>
    /// <returns>
    /// The stored bytes; empty for an empty payload. Valid until the next <see cref="Reset"/>, <see cref="TruncateTo"/>, or
    /// <see cref="Dispose"/>. The memory is array-backed, so wrapping it into a <see cref="FieldValueData"/> allocates nothing.
    /// </returns>
    internal ReadOnlyMemory<byte> GetMemory(ArenaRef reference) =>
        reference.Length == 0
            ? ReadOnlyMemory<byte>.Empty
            : _Chunks[reference.Chunk].AsMemory(reference.Offset, reference.Length);

    #endregion

    #region Rollback and release

    /// <summary>Remembers the current end of the arena for <see cref="TruncateTo"/>.</summary>
    /// <returns>The current position.</returns>
    internal ArenaMark Mark() => new(_Chunks.Count, _Used);

    /// <summary>Drops everything appended after <paramref name="mark"/> and returns the chunks rented since then to the pool.</summary>
    /// <param name="mark">A position returned by <see cref="Mark"/> on this arena.</param>
    internal void TruncateTo(ArenaMark mark)
    {
        while (_Chunks.Count > mark.ChunkCount)
        {
            ArrayPool<byte>.Shared.Return(_Chunks[^1]);
            _Chunks.RemoveAt(_Chunks.Count - 1);
        }

        _Used = mark.Used;
    }

    /// <summary>Forgets all payloads for the next batch. The first regular chunk is kept for reuse; the others go back to the pool.</summary>
    internal void Reset()
    {
        bool keepFirst = _Chunks.Count > 0 && _Chunks[0].Length == _ChunkSize;
        int keep = keepFirst
            ? 1
            : 0;
        while (_Chunks.Count > keep)
        {
            ArrayPool<byte>.Shared.Return(_Chunks[^1]);
            _Chunks.RemoveAt(_Chunks.Count - 1);
        }

        _Used = 0;
    }

    /// <summary>Returns every chunk to the pool. The arena is empty afterwards and may be used again.</summary>
    public void Dispose()
    {
        foreach (byte[] chunk in _Chunks)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        _Chunks.Clear();
        _Used = 0;
    }

    #endregion
}
