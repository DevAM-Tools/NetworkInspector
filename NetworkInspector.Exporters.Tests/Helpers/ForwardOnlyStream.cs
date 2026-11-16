// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// A write-only stream over a <see cref="MemoryStream"/> that refuses everything a forward-only sink (a pipe, an HTTP body, a
/// network share opened for append) cannot do: no seek, no read, no length, no position. Tests use it to prove that a writer
/// never needs more than forward writes. Not thread-safe.
/// </summary>
internal sealed class ForwardOnlyStream : Stream
{
    /// <summary>Receives the written bytes; read them with <see cref="ToArray"/>.</summary>
    private readonly MemoryStream _Inner = new();

    /// <summary>Whether <see cref="Dispose(bool)"/> ran; a write after it throws.</summary>
    private bool _Disposed;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_Disposed;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: a forward-only stream has no length.</exception>
    public override long Length => throw new NotSupportedException("Length of a forward-only stream.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: a forward-only stream reports no position and accepts none.</exception>
    public override long Position
    {
        get => throw new NotSupportedException("Position of a forward-only stream.");
        set => throw new NotSupportedException("Position of a forward-only stream.");
    }

    /// <summary>Whether the writer disposed the stream.</summary>
    internal bool IsDisposed => _Disposed;

    /// <summary>The bytes written so far; valid also after dispose.</summary>
    /// <returns>A copy of the written bytes.</returns>
    internal byte[] ToArray() => _Inner.ToArray();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The stream was disposed.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        _Inner.Write(buffer);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Nothing is buffered: every write goes straight to the inner stream.
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: a forward-only stream cannot be read.</exception>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Read of a forward-only stream.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: a forward-only stream cannot seek.</exception>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Seek of a forward-only stream.");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: a forward-only stream cannot change its length.</exception>
    public override void SetLength(long value) =>
        throw new NotSupportedException("SetLength of a forward-only stream.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        _Disposed = true;
        base.Dispose(disposing);
    }
}
