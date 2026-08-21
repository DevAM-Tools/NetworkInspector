// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Write-only <see cref="Stream"/> adapter that appends into a <see cref="PooledBuffer"/>.
/// Used for zlib/deflate staging without allocating a <see cref="MemoryStream"/> per flush.
/// </summary>
internal sealed class PooledBufferWriteStream : Stream
{
    private readonly PooledBuffer _Buffer;

    /// <summary>Creates a stream that writes into <paramref name="buffer"/>.</summary>
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
    public override void Write(ReadOnlySpan<byte> buffer) =>
        _Buffer.Write(buffer);
}
