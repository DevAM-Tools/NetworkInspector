// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Shared stream test doubles used across multiple test classes.
/// </summary>
internal static class TestStreams
{
    /// <summary>
    /// Write-only, non-seekable stream that simulates a broken underlying file or socket without touching the real filesystem.
    /// It fails in one or both of two ways, both with <see cref="IOException"/>: the write call with the number
    /// <c>failOnWriteNumber</c> (counted from 1) throws once and stores nothing while every other call succeeds, and every
    /// write that would push the cumulative byte count above <see cref="ThrowAfterByte"/> throws for good. Neither failure is
    /// armed by default. Not thread-safe: the write counters are plain fields, so one writer thread at a time.
    /// </summary>
    internal sealed class FailingStream : Stream
    {
        /// <summary>Number of the write call (1-based) that fails once; 0 or less never fails by call number.</summary>
        private readonly int _FailOnWriteNumber;

        /// <summary>Number of bytes accepted so far.</summary>
        private long _BytesWritten;

        /// <summary>Number of write calls seen so far, successful or not.</summary>
        private int _WriteCalls;

        /// <summary>Creates a stream that fails according to the given arguments and <see cref="ThrowAfterByte"/>.</summary>
        /// <param name="failOnWriteNumber">Number of the write call (1-based) that throws once; 0 arms no call-number failure.</param>
        internal FailingStream(int failOnWriteNumber = 0)
        {
            _FailOnWriteNumber = failOnWriteNumber;
        }

        /// <summary>Number of bytes accepted before the stream begins throwing; unlimited by default.</summary>
        internal long ThrowAfterByte
        {
            get; init;
        } = long.MaxValue;

        /// <inheritdoc/>
        public override bool CanRead => false;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => true;

        /// <inheritdoc/>
        public override long Length => _BytesWritten;

        /// <inheritdoc/>
        public override long Position
        {
            get => _BytesWritten;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            // No-op: the stream holds no internal buffer.
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            // One call is counted once whichever overload carried it, so a failing call number means the same for every writer.
            _WriteCalls++;
            if (_WriteCalls == _FailOnWriteNumber || _BytesWritten + buffer.Length > ThrowAfterByte)
            {
                throw new IOException("Simulated I/O failure");
            }

            _BytesWritten += buffer.Length;
        }
    }

    /// <summary>
    /// Memory-backed stream whose chosen write call (counted from 1) throws <see cref="IOException"/> once, without storing
    /// anything; all other writes succeed. Models a transient failure after which the export continues.
    /// </summary>
    internal sealed class FailOnceStream : Stream
    {
        private readonly MemoryStream _Inner = new();
        private int _WriteCalls;

        /// <summary>Number of the write call (1-based) that fails.</summary>
        internal int FailingWriteCall
        {
            get; init;
        }

        /// <summary>The bytes that were actually written.</summary>
        internal byte[] WrittenBytes => _Inner.ToArray();

        /// <inheritdoc/>
        public override bool CanRead => false;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => true;

        /// <inheritdoc/>
        public override long Length => _Inner.Length;

        /// <inheritdoc/>
        public override long Position
        {
            get => _Inner.Position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            // No-op: nothing is buffered here.
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _WriteCalls++;
            if (_WriteCalls == FailingWriteCall)
            {
                throw new IOException("Simulated transient I/O failure");
            }

            _Inner.Write(buffer);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _Inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
