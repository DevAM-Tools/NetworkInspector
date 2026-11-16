// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Forwards every member to a caller's stream and leaves that stream open when it is disposed. An exporter disposes the entry
/// stream it opened; for a stream the caller owns, that dispose must only flush. Forwarding <see cref="Position"/> and
/// <see cref="Seek"/> keeps the BLF header patch working on a seekable caller stream that started at a non-zero position.
/// <para>
/// <b>Thread safety:</b> not thread-safe, like the stream it wraps.
/// </para>
/// </summary>
/// <seealso cref="StreamExportTarget"/>
internal sealed class NonClosingStream : Stream
{
    #region Configuration and state

    /// <summary>The caller's stream; never disposed here.</summary>
    private readonly Stream _Inner;

    /// <summary>Whether <see cref="Dispose(bool)"/> ran; every later member call throws.</summary>
    private bool _Disposed;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The caller's stream.</param>
    internal NonClosingStream(Stream inner) => _Inner = inner;

    #endregion

    #region Forwarded members

    /// <inheritdoc/>
    public override bool CanRead => !_Disposed && _Inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => !_Disposed && _Inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => !_Disposed && _Inner.CanWrite;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_Disposed, this);
            return _Inner.Length;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_Disposed, this);
            return _Inner.Position;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_Disposed, this);
            _Inner.Position = value;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        return _Inner.Read(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        _Inner.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        _Inner.Write(buffer);
    }

    /// <inheritdoc/>
    public override System.Threading.Tasks.Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        return _Inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    /// <inheritdoc/>
    public override System.Threading.Tasks.ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        return _Inner.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        _Inner.Flush();
    }

    /// <inheritdoc/>
    public override System.Threading.Tasks.Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        return _Inner.FlushAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        return _Inner.Seek(offset, origin);
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        _Inner.SetLength(value);
    }

    #endregion

    #region Disposal

    /// <inheritdoc/>
    /// <remarks>
    /// Flushes the caller's stream so the bytes the exporter wrote reach it, then ends this entry. The caller's stream stays
    /// open: its owner closes it. A second dispose does nothing.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_Disposed)
        {
            _Disposed = true;
            if (_Inner.CanWrite)
            {
                _Inner.Flush();
            }
        }

        base.Dispose(disposing);
    }

    #endregion
}
