// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

    /// <summary>
    /// Gets the storage path used for JSON persistence, or <see langword="null"/> when
    /// no storage path is configured.
    /// </summary>
    string? StoragePath
    {
        get;
    }

    #endregion

    #region Querying

    /// <summary>
    /// Gets a setting by name. Returns <c>null</c> if not found.
    /// Keep the compile-time type as <see cref="ReadOnlySettingView"/>; assigning to
    /// <see cref="IReadOnlySetting"/> boxes.
    /// </summary>
    ReadOnlySettingView? GetSetting(string name);

    /// <summary>Gets all settings as read-only struct views (snapshot).</summary>
    IReadOnlyList<ReadOnlySettingView> AllSettings
    {
        get;
    }

    /// <summary>Gets all group names (snapshot).</summary>
    IReadOnlyList<string> AllGroups
    {
        get;
    }

    /// <summary>
    /// Gets a group by name. Returns <c>null</c> if not found.
    /// Keep the compile-time type as <see cref="ReadOnlySettingGroupView"/>; assigning to
    /// <see cref="IReadOnlySettingGroup"/> boxes.
    /// </summary>
    ReadOnlySettingGroupView? GetGroup(string name);

    /// <summary>Gets all settings in a group as read-only struct views (snapshot).</summary>
    IReadOnlyList<ReadOnlySettingView> GetSettingsInGroup(string groupName);

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

    /// <summary>
    /// Convenience: gets a byte array setting value by name.
    /// Returns a defensive copy of the stored bytes, or <see langword="null"/> when the
    /// name is unregistered or the setting is not <see cref="SettingType.Bytes"/>.
    /// </summary>
    byte[]? GetBytesSetting(string name);

    /// <summary>
    /// Convenience: gets an enum setting (name, numeric value) by name.
    /// Returns <see langword="null"/> when the name is unregistered or the setting is not
    /// <see cref="SettingType.Enum"/>.
    /// </summary>
    (string Name, ulong Value)? GetEnumSetting(string name);

    /// <summary>
    /// Convenience: gets a boolean array copy by name.
    /// Returns a defensive copy of the stored values, or <see langword="null"/> when the
    /// name is unregistered or the setting is not <see cref="SettingType.BoolArray"/>.
    /// </summary>
    bool[]? GetBoolArraySetting(string name);

    /// <summary>
    /// Convenience: gets a string array copy by name.
    /// Returns a defensive copy of the stored values, or <see langword="null"/> when the
    /// name is unregistered or the setting is not <see cref="SettingType.StringArray"/>.
    /// </summary>
    string[]? GetStringArraySetting(string name);

    /// <summary>
    /// Convenience: gets a double array copy by name.
    /// Returns a defensive copy of the stored values, or <see langword="null"/> when the
    /// name is unregistered or the setting is not <see cref="SettingType.F64Array"/>.
    /// </summary>
    double[]? GetF64ArraySetting(string name);

    /// <summary>
    /// Convenience: gets a ulong array copy by name.
    /// Returns a defensive copy of the stored values, or <see langword="null"/> when the
    /// name is unregistered or the setting is not <see cref="SettingType.U64Array"/>.
    /// </summary>
    ulong[]? GetU64ArraySetting(string name);

    /// <summary>
    /// Convenience: gets a long array copy by name.
    /// Returns a defensive copy of the stored values, or <see langword="null"/> when the
    /// name is unregistered or the setting is not <see cref="SettingType.I64Array"/>.
    /// </summary>
    long[]? GetI64ArraySetting(string name);
    #endregion
}
