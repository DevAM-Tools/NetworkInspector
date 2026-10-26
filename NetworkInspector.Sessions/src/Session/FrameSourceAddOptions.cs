// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Per-source options for <see cref="ISession.TryAddFrameSource(IFrameSource, FrameSourceAddOptions, out FrameSourceInfo?, out SessionFailure?)"/>.
/// </summary>
/// <remarks>
/// Immutable value type. Thread-safety is not applicable.
/// <para>
/// Why these options exist: the session keeps no parsed packets, so every later read (<c>TryGetPacket</c>,
/// <c>ReadPackets</c>, <c>Restart</c>) re-parses the captured frame and therefore needs random access to it. A pure
/// stream source cannot be re-read, so the session always retains its frames in a <see cref="CachedFrameSource"/>.
/// A random-access source can re-read by itself, so by default nothing is retained for it; the cache is opt-in.
/// </para>
/// <para>
/// The all-zero value is the default configuration (<see cref="Default"/> is <see langword="default"/>), so a new
/// option added here must keep "false / unset" as its default.
/// </para>
/// </remarks>
public readonly struct FrameSourceAddOptions
{
    #region Presets

    /// <summary>Default: cache non-random-access sources only.</summary>
    public static FrameSourceAddOptions Default => default;

    #endregion

    #region Properties

    /// <summary>
    /// When <see langword="true"/>, wrap an <see cref="IRandomAccessFrameSource"/> in
    /// <see cref="CachedFrameSource"/>. Ignored for sources that are not random-access
    /// (those are always wrapped) and for a source that is already a
    /// <see cref="CachedFrameSource"/>.
    /// The wrapper holds each inner <see cref="Frame"/>; it does not copy payload bytes.
    /// </summary>
    public bool CacheRandomAccess { get; init; }

    #endregion
}
