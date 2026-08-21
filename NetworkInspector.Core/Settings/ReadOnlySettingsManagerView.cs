// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Zero-allocation read-only view over a <see cref="SettingsManager"/>.
/// <para>
/// When the compile-time type is this struct, member calls can inline to the owner.
/// Consume it through generic methods constrained to <see cref="IReadOnlySettingsManager"/>
/// so the JIT does not box.
/// </para>
/// <para>
/// Warning: do not cast this struct to <see cref="IReadOnlySettingsManager"/>, store it in that
/// interface type, or pass it to a non-generic parameter of that type. Those conversions box.
/// Prefer <see cref="SettingsManager.ReadOnly"/> or <see cref="IStack.Settings"/> and keep the
/// compile-time type as this struct (or use a generic constraint).
/// </para>
/// </summary>
public readonly struct ReadOnlySettingsManagerView : IReadOnlySettingsManager
{
    #region Fields

    private readonly SettingsManager _Owner;

    #endregion

    #region Lifecycle

    /// <summary>Creates a view over <paramref name="owner"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public ReadOnlySettingsManagerView(SettingsManager owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _Owner = owner;
    }

    #endregion

    #region Counts

    /// <inheritdoc/>
    public int SettingCount => _Owner.SettingCount;

    /// <inheritdoc/>
    public int GroupCount => _Owner.GroupCount;

    /// <inheritdoc/>
    public string? StoragePath => _Owner.StoragePath;

    #endregion

    #region Querying

    /// <inheritdoc/>
    public ReadOnlySettingView? GetSetting(string name)
    {
        Setting? setting = _Owner.GetSetting(name);
        if (setting is null)
        {
            return null;
        }

        return setting.AsReadOnlyView();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ReadOnlySettingView> AllSettings => _Owner.ReadOnlyAllSettings;

    /// <inheritdoc/>
    public IReadOnlyList<string> AllGroups => _Owner.AllGroups;

    /// <inheritdoc/>
    public ReadOnlySettingGroupView? GetGroup(string name)
    {
        SettingGroup? group = _Owner.GetGroup(name);
        if (group is null)
        {
            return null;
        }

        return group.AsReadOnlyView();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ReadOnlySettingView> GetSettingsInGroup(string groupName) =>
        _Owner.GetSettingsInGroupAsReadOnly(groupName);

    #endregion

    #region Typed Accessors

    /// <inheritdoc/>
    public bool? GetBoolSetting(string name) => _Owner.GetBoolSetting(name);

    /// <inheritdoc/>
    public string? GetStringSetting(string name) => _Owner.GetStringSetting(name);

    /// <inheritdoc/>
    public double? GetF64Setting(string name) => _Owner.GetF64Setting(name);

    /// <inheritdoc/>
    public ulong? GetU64Setting(string name) => _Owner.GetU64Setting(name);

    /// <inheritdoc/>
    public long? GetI64Setting(string name) => _Owner.GetI64Setting(name);

    /// <inheritdoc/>
    public byte[]? GetBytesSetting(string name) => _Owner.GetBytesSetting(name);

    /// <inheritdoc/>
    public (string Name, ulong Value)? GetEnumSetting(string name) => _Owner.GetEnumSetting(name);

    #endregion
}
