// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Read-only view of a setting group.
/// <para>
/// Exposes group metadata and setting enumeration without allowing mutation
/// (no <see cref="SettingGroup.AddSetting"/>).
/// Returned by <see cref="IReadOnlySettingsManager"/> so consumers cannot
/// modify groups through the read-only surface.
/// </para>
/// </summary>
public interface IReadOnlySettingGroup
{
    /// <summary>The unique machine-readable name (empty string for default group).</summary>
    string Name
    {
        get;
    }

    /// <summary>The human-readable display name.</summary>
    string UiName
    {
        get;
    }

    /// <summary>Optional description.</summary>
    string? Description
    {
        get;
    }

    /// <summary>Returns true if this is the default group.</summary>
    bool IsDefaultGroup
    {
        get;
    }

    /// <summary>Returns the number of settings in this group.</summary>
    int SettingCount
    {
        get;
    }

    /// <summary>Gets a snapshot of all settings in this group.</summary>
    IReadOnlyList<Setting> Settings
    {
        get;
    }
}
