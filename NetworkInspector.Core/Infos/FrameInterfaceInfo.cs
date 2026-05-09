// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered frame interface (e.g., eth0, wlan0).</summary>
public sealed class FrameInterfaceInfo(
    FrameInterfaceId id,
    FrameSourceId sourceId,
    string uiName,
    string? description,
    LinkType? linkType,
    IReadOnlyDictionary<string, object>? properties = null)
{
    #region Properties

    /// <summary>Unique interface identifier.</summary>
    public FrameInterfaceId Id { get; } = id;

    /// <summary>
    /// The frame source that owns this interface.
    /// <see cref="FrameSourceId.Invalid"/> if no source was specified during registration.
    /// </summary>
    public FrameSourceId SourceId { get; } = sourceId;

    /// <summary>Human-readable interface name (e.g., "eth0", "wlan0").</summary>
    public string UiName { get; } = uiName;

    /// <summary>Optional description text.</summary>
    public string? Description { get; } = description;

    /// <summary>Optional link-layer header type for this interface.</summary>
    public LinkType? LinkType { get; } = linkType;

    /// <summary>
    /// Source-specific metadata as frozen key-value pairs.
    /// Always non-null; empty when no metadata was provided.
    /// </summary>
    public FrozenDictionary<string, object> Properties
    {
        get;
    } =
        properties?.ToFrozenDictionary() ?? FrozenDictionary<string, object>.Empty;

    #endregion
}
