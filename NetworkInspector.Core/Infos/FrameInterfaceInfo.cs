// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered frame interface (e.g., eth0, wlan0).</summary>
public sealed record FrameInterfaceInfo
{
    #region Lifecycle

    /// <summary>Creates interface metadata; freezes optional property bags.</summary>
    /// <param name="id">The unique interface identifier.</param>
    /// <param name="sourceId">The owning frame source, or <see cref="FrameSourceId.Invalid"/> if none was specified.</param>
    /// <param name="uiName">Human-readable interface name; must be non-empty and free of control characters.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="linkType">Optional link-layer header type.</param>
    /// <param name="properties">
    /// Optional source-specific metadata. Copied into a <see cref="FrozenDictionary{TKey, TValue}"/>, so later changes to
    /// the caller's dictionary do not show up here; the values themselves are not copied.
    /// </param>
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
        // Interface names come from capture files and sources (untrusted input). The name is shown as single-line text,
        // so an empty name and any control character (line breaks and tabs included) are rejected here at registration
        // instead of being sanitized by every consumer later. Sources that read names from files check IsValidUiName
        // themselves and substitute a generated name before they get here.
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

        // Freeze a private copy: the info is shared by reference between threads, so it must not alias a dictionary the
        // caller can still mutate. A frozen dictionary is read-optimized and allocation-free to read; the shared Empty
        // instance avoids an allocation when no properties were supplied.
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
