// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Per-source options for <see cref="ISession.TryAddFrameSource(IFrameSource, FrameSourceAddOptions, out FrameSourceInfo)"/>.
/// </summary>
/// <remarks>
/// Immutable value type. Thread-safety is not applicable.
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
