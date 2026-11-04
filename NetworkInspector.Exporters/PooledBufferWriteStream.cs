// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Write-only <see cref="Stream"/> adapter that appends into a <see cref="PooledBuffer"/>.
/// Used for zlib/deflate staging without allocating a <see cref="MemoryStream"/> per flush: a compression stream
/// needs a <see cref="Stream"/> to write its output to, and this adapter lets that output land directly in an
/// already pooled buffer instead of a per-flush <see cref="MemoryStream"/> plus a copy out of it.
/// <para>
/// Thread safety: not thread-safe; it inherits the single-writer contract of <see cref="PooledBuffer"/>.
/// The stream does not own the buffer: disposing it neither clears nor returns the buffer, the owner does that.
/// </para>
/// </summary>
internal sealed class PooledBufferWriteStream : Stream
{
    /// <summary>The buffer that receives every written byte; owned by the caller of the constructor, never returned by this stream.</summary>
    private readonly PooledBuffer _Buffer;

    /// <summary>Creates a stream that writes into <paramref name="buffer"/>. Parameter validation is omitted: callers are internal.</summary>
    /// <param name="buffer">The buffer that receives all written bytes; must not be <see langword="null"/>.</param>
    internal PooledBufferWriteStream(PooledBuffer buffer)
    {
        _Buffer = buffer;
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => _Buffer.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _Buffer.Length;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Intentionally empty: every write goes straight into the in-memory buffer, so there is nothing to flush.
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        _Buffer.Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    /// <remarks>
    /// Compression streams such as <see cref="System.IO.Compression.ZLibStream"/> write their output through this
    /// span overload, so it is the one that matters; the array-based overload exists for callers of the older
    /// <see cref="Stream"/> API and forwards to the same buffer.
    /// </remarks>
    public override void Write(ReadOnlySpan<byte> buffer) =>
        _Buffer.Write(buffer);
}
