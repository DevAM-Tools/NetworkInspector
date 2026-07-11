// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Shared stream test doubles used across multiple test classes.
/// </summary>
internal static class TestStreams
{
    /// <summary>
    /// Stream that throws <see cref="IOException"/> once the cumulative number of bytes written
    /// exceeds <see cref="FailingStream.ThrowAfterByte"/>. Used to simulate a broken underlying
    /// file or socket without touching the real filesystem.
    /// </summary>
    internal sealed class FailingStream : Stream
    {
        private long _BytesWritten;

        /// <summary>Number of bytes accepted before the stream begins throwing.</summary>
        internal long ThrowAfterByte
        {
            get; init;
        }

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
        public override void Write(byte[] buffer, int offset, int count)
        {
            _CheckThrow(count);
            _BytesWritten += count;
        }

        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _CheckThrow(buffer.Length);
            _BytesWritten += buffer.Length;
        }

        /// <summary>Throws <see cref="IOException"/> when the cumulative byte count would exceed the threshold.</summary>
        private void _CheckThrow(int count)
        {
            if (_BytesWritten + count > ThrowAfterByte)
            {
                throw new IOException("Simulated I/O failure");
            }
        }
    }
}
