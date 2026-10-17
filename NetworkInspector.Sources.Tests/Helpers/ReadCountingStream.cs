// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Helpers;

/// <summary>
/// A read-only, non-seekable stream over a byte array that counts read calls. It is neither a
/// <see cref="MemoryStream"/> nor a <see cref="BufferedStream"/>, so stream sources treat it like a network or pipe
/// stream; tests use the counter to prove the source buffers small reads.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class ReadCountingStream : Stream
{
    private readonly byte[] _Data;
    private int _Position;

    /// <summary>Creates a stream that returns <paramref name="data"/> once.</summary>
    internal ReadCountingStream(byte[] data)
    {
        _Data = data;
    }

    /// <summary>Number of <c>Read</c> calls the stream received (all overloads).</summary>
    internal int ReadCalls
    {
        get; private set;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        int n = Math.Min(buffer.Length, _Data.Length - _Position);
        _Data.AsSpan(_Position, n).CopyTo(buffer);
        _Position += n;
        return n;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
