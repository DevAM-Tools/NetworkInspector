// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
    Frame? FrameById(FrameId id);

    #endregion
}
