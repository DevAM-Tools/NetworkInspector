// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered frame interface (e.g., eth0, wlan0).</summary>
public sealed record FrameInterfaceInfo
{
    #region Lifecycle

    /// <summary>Creates interface metadata; freezes optional property bags.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="uiName"/> is null.</exception>
    /// <exception cref="InvalidUiNameRegistrationException"><paramref name="uiName"/> is empty or contains control characters.</exception>
    public FrameInterfaceInfo(
        FrameInterfaceId id,
        FrameSourceId sourceId,
        string uiName,
        string? description,
        LinkType? linkType,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(uiName);
        if (!NameValidation.IsValidUiName(uiName))
        {
            throw InvalidUiNameRegistrationException.For(uiName);
        }

        Id = id;
        SourceId = sourceId;
        UiName = uiName;
        Description = description;
        LinkType = linkType;
        Properties = properties?.ToFrozenDictionary() ?? FrozenDictionary<string, object>.Empty;
    }

    #endregion

    #region Properties

    /// <summary>Unique interface identifier.</summary>
    public FrameInterfaceId Id { get; }

    /// <summary>
    /// The frame source that owns this interface.
    /// <see cref="FrameSourceId.Invalid"/> if no source was specified during registration.
    /// </summary>
    public FrameSourceId SourceId { get; }

    /// <summary>Human-readable interface name (e.g., "eth0", "wlan0").</summary>
    public string UiName { get; }

    /// <summary>Optional description text.</summary>
    public string? Description { get; }

    /// <summary>Optional link-layer header type for this interface.</summary>
    public LinkType? LinkType { get; }

    /// <summary>
    /// Source-specific metadata as frozen key-value pairs.
    /// Always non-null; empty when no metadata was provided.
    /// </summary>
    public FrozenDictionary<string, object> Properties { get; }

    #endregion
}
