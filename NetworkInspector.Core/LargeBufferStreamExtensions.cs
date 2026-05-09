// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core;

/// <summary>
/// Extension methods for reading from and writing to <see cref="Stream"/> using
/// <see cref="LargeBuffer"/> and <see cref="LargeBufferElement"/>.
/// <para>
/// Synchronous methods operate directly on the buffer's <see cref="Span{Byte}"/> window.
/// Asynchronous methods use <see cref="ArrayPool{Byte}"/> as an intermediate to bridge
/// the gap between <c>Span&lt;byte&gt;</c> (not usable in async) and
/// <c>Memory&lt;byte&gt;</c> (required by <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>).
/// </para>
/// </summary>
public static class LargeBufferStreamExtensions
{
    /// <summary>Default intermediate buffer size for async operations (64 KiB).</summary>
    private const int DefaultAsyncBufferSize = 64 * 1024;

    #region LargeBuffer — Sync

    /// <summary>
    /// Reads bytes from the stream directly into the <see cref="LargeBuffer"/> at the
    /// specified offset. Returns the number of bytes actually read.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="buffer">The target buffer.</param>
    /// <param name="offset">Byte offset in the buffer to start writing at.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <returns>The number of bytes read (may be less than <paramref name="count"/>).</returns>
    public static int ReadInto(this Stream stream, LargeBuffer buffer, long offset, int count)
    {
        Span<byte> span = buffer.AsSpan(offset, count);
        return stream.Read(span);
    }

    /// <summary>
    /// Writes bytes from the <see cref="LargeBuffer"/> at the specified offset into the stream.
    /// </summary>
    /// <param name="stream">The target stream.</param>
    /// <param name="buffer">The source buffer.</param>
    /// <param name="offset">Byte offset in the buffer to start reading from.</param>
    /// <param name="count">Number of bytes to write.</param>
    public static void WriteFrom(this Stream stream, LargeBuffer buffer, long offset, int count)
    {
        ReadOnlySpan<byte> span = buffer.AsReadOnlySpan(offset, count);
        stream.Write(span);
    }

    #endregion

    #region LargeBuffer — Async

    /// <summary>
    /// Asynchronously reads bytes from the stream into the <see cref="LargeBuffer"/> at
    /// the specified offset. Uses a pooled intermediate buffer because <c>Span&lt;byte&gt;</c>
    /// cannot be used across <c>await</c> boundaries.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="buffer">The target buffer.</param>
    /// <param name="offset">Byte offset in the buffer to start writing at.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of bytes read.</returns>
    public static async ValueTask<int> ReadIntoAsync(
        this Stream stream,
        LargeBuffer buffer,
        long offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        // Rent a pooled buffer for the async bridge
        int chunkSize = Math.Min(count, DefaultAsyncBufferSize);
        byte[] temp = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            int totalRead = 0;
            long currentOffset = offset;
            int remaining = count;

            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, chunkSize);
                int bytesRead = await stream.ReadAsync(
                    temp.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break; // End of stream
                }

                // Copy from pooled buffer into the LargeBuffer
                buffer.CopyFrom(temp.AsSpan(0, bytesRead), currentOffset);
                currentOffset += bytesRead;
                totalRead += bytesRead;
                remaining -= bytesRead;
            }

            return totalRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    /// <summary>
    /// Asynchronously writes bytes from the <see cref="LargeBuffer"/> at the specified
    /// offset into the stream. Uses a pooled intermediate buffer.
    /// </summary>
    /// <param name="stream">The target stream.</param>
    /// <param name="buffer">The source buffer.</param>
    /// <param name="offset">Byte offset in the buffer to start reading from.</param>
    /// <param name="count">Number of bytes to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async ValueTask WriteFromAsync(
        this Stream stream,
        LargeBuffer buffer,
        long offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        int chunkSize = Math.Min(count, DefaultAsyncBufferSize);
        byte[] temp = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            long currentOffset = offset;
            int remaining = count;

            while (remaining > 0)
            {
                int toWrite = Math.Min(remaining, chunkSize);
                buffer.CopyTo(currentOffset, temp.AsSpan(0, toWrite));

                await stream.WriteAsync(
                    temp.AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);

                currentOffset += toWrite;
                remaining -= toWrite;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    #endregion
}
