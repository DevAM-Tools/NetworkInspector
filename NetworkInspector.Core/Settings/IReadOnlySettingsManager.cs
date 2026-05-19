// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Read-only view of the settings manager.
/// <para>
/// Exposes setting queries, typed accessors, group lookups, and snapshots
/// without allowing registration, mutation, or persistence operations.
/// Exposed by <see cref="IStack"/> so consumers can query settings
/// without coupling to <see cref="SettingsManager"/>.
/// </para>
/// </summary>
public interface IReadOnlySettingsManager
{
    #region Counts

    /// <summary>Returns the number of registered settings.</summary>
    int SettingCount
    {
        get;
    }

    /// <summary>Returns the number of registered groups.</summary>
    int GroupCount
    {
        get;
    }

    #endregion

    #region Querying

    /// <summary>Gets a setting by name. Returns <c>null</c> if not found.</summary>
    IReadOnlySetting? GetSetting(string name);

    /// <summary>Gets all settings (snapshot).</summary>
    IReadOnlyList<IReadOnlySetting> AllSettings
    {
        get;
    }

    /// <summary>Gets all group names (snapshot).</summary>
    IReadOnlyList<string> AllGroups
    {
        get;
    }

    /// <summary>Gets a group by name. Returns <c>null</c> if not found.</summary>
    IReadOnlySettingGroup? GetGroup(string name);

    /// <summary>Gets all settings in a group (snapshot).</summary>
    IReadOnlyList<IReadOnlySetting> GetSettingsInGroup(string groupName);

    #endregion

    #region Typed Accessors

    /// <summary>Convenience: gets a boolean setting value by name.</summary>
    bool? GetBoolSetting(string name);

    /// <summary>Convenience: gets a string setting value by name.</summary>
    string? GetStringSetting(string name);

    /// <summary>Convenience: gets a double setting value by name.</summary>
    double? GetF64Setting(string name);

    /// <summary>Convenience: gets a ulong setting value by name.</summary>
    ulong? GetU64Setting(string name);

    /// <summary>Convenience: gets a long setting value by name.</summary>
    long? GetI64Setting(string name);
    #endregion
}