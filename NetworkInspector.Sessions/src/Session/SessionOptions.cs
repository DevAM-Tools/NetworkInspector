// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Construction-time options for <see cref="Session"/>.
/// </summary>
/// <remarks>
/// Immutable after construction. Thread-safety is not applicable.
/// </remarks>
public sealed class SessionOptions
{
    #region Presets

    /// <summary>
    /// Default options: packet index on, ingest skip-parses, frames cached only for
    /// sources that are not <see cref="IRandomAccessFrameSource"/>.
    /// </summary>
    public static SessionOptions Default { get; } = new();

    /// <summary>
    /// No packet index — listeners re-parse in parallel without roaring-bitmap overhead.
    /// Frames from stream sources are still cached so <see cref="ISessionReader.TryGetPacket(PacketId, out Packet?)"/> works.
    /// </summary>
    public static SessionOptions RedissectOnly { get; } = new()
    {
        IndexPackets = false,
    };

    #endregion

    #region Properties

    /// <summary>
    /// When <see langword="true"/> (default), the first parse populates the session packet index.
    /// When <see langword="false"/>, <see cref="ISessionReader.PacketIndex"/> stays
    /// <see langword="null"/> and packets are parsed via the plain <c>ParseFrame</c> path.
    /// </summary>
    public bool IndexPackets { get; init; } = true;

    /// <summary>
    /// When non-null, the session builds a value cache for these fields (or all fields)
    /// and fills it during the first parse of each frame. Field and group names are checked with
    /// <see cref="NameValidation.IsValidName"/> at construction and Restart, then resolved on the
    /// current stack. Replaced on <see cref="Session.Restart"/>.
    /// </summary>
    public ValueCacheRequest? ValueCache { get; init; }

    /// <summary>
    /// Optional push subscriber for <see cref="ValueCache"/>. Requires <see cref="ValueCache"/> to be set.
    /// Callbacks run on a dedicated slot thread like <see cref="ISessionListener"/>.
    /// </summary>
    public IValueCacheListener? ValueCacheListener { get; init; }

    #endregion
}
