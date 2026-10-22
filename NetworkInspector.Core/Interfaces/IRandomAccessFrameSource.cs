// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// A frame source that supports random access by <see cref="FrameId"/>.
/// <para>
/// Extends <see cref="IFrameSource"/> with the ability to re-read frames by their
/// unique identifier. This enables:
/// <list type="bullet">
///   <item>Re-parsing packets that were evicted from the packet cache.</item>
///   <item>Detailed analysis, export, or UI display of specific packets.</item>
/// </list>
/// </para>
/// <para>
/// Sources implementing this interface can optionally skip the session's frame cache,
/// since they can efficiently re-read frames on demand.
/// </para>
/// </summary>
public interface IRandomAccessFrameSource : IFrameSource
{
    #region Methods

    /// <summary>
    /// Gets a frame by its unique identifier.
    /// <para>
    /// This method enables re-reading frames for detailed analysis, export,
    /// or UI display when packets are evicted from the packet cache.
    /// </para>
    /// </summary>
    /// <param name="id">The unique frame identifier assigned during streaming.</param>
    /// <returns>
    /// The <see cref="Frame"/> if it exists and can be retrieved; otherwise <c>null</c>.
    /// </returns>
    /// <remarks>
    /// This method must be thread-safe. It may be called concurrently from
    /// multiple threads. Implementations must use internal synchronization
    /// if mutable state is required.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The source has not been started. Implementation-specific: a source may instead return no frames before
    /// <see cref="IFrameSource.Start"/> (the PCAPNG source does); each source documents its choice.
    /// </exception>
    /// <param name="cancellationToken">Token that can be used to cancel the read operation.</param>
    Frame? FrameById(FrameId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a contiguous range of frames in one call: <paramref name="frames"/>[i] receives the frame of id
    /// <paramref name="first"/> + i, or <c>null</c> where <see cref="FrameById"/> returns <c>null</c> for that id (not
    /// indexed yet, outside the valid id range, bytes that no longer form the frame). The length of the range is
    /// <paramref name="frames"/>.Length.
    /// <para>
    /// A range lets a source read its frames one after another: sources that re-read a capture file walk their index and
    /// the file forward and request the bytes of the next frames from memory early, which is faster than one
    /// <see cref="FrameById"/> per id. The default implementation calls <see cref="FrameById"/> per id.
    /// </para>
    /// </summary>
    /// <param name="first">Id of the frame that goes into <paramref name="frames"/>[0].</param>
    /// <param name="frames">
    /// Receives one entry per id of the range; its length is the number of ids. Every entry is written. The buffer
    /// belongs to this call until it returns.
    /// </param>
    /// <param name="cancellationToken">Checked before the first id and at least once per 64 ids.</param>
    /// <returns>The number of entries that received a frame.</returns>
    /// <remarks>
    /// Thread-safe like <see cref="FrameById"/>. When the source is disposed during the call, entries already written
    /// stay, and the call either throws <see cref="ObjectDisposedException"/> or leaves <c>null</c> in the entries it
    /// could not read. On cancellation, entries already written stay. An empty <paramref name="frames"/> reads nothing
    /// and returns 0.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The source was disposed before or during the call.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">
    /// The source has not been started. Implementation-specific: a source may instead return no frames before
    /// <see cref="IFrameSource.Start"/> (the PCAPNG source does); each source documents its choice.
    /// </exception>
    int FramesById(FrameId first, Span<Frame?> frames, CancellationToken cancellationToken = default)
    {
        int found = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            // Cancellation at least once per 64 ids, even when FrameById itself does not check the token.
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Ids past the valid range have no frame; the sum is formed in long so it cannot wrap.
            long id = (long)first.Value + i;
            Frame? frame = id >= 0 && id <= ArrayIndexIdRange.MaxValue
                ? FrameById(new FrameId((int)id), cancellationToken)
                : null;
            frames[i] = frame;
            if (frame is not null)
            {
                found++;
            }
        }

        return found;
    }

    #endregion
}
