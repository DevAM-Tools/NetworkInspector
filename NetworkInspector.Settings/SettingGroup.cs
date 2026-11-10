// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Represents a group of related settings for UI organization.
/// Thread-safe: the settings are held in an immutable array that is replaced as a whole on registration (copy-on-write), so
/// every read is lock-free and sees a complete list.
/// </summary>
/// <remarks>
/// A group is created and filled by <see cref="SettingsManager"/>, which calls <see cref="AddSetting"/> under its own write
/// lock; the group has exactly one writer at a time. Readers (<see cref="SettingCount"/>, <see cref="Settings"/>) take no lock.
/// <para>Architecture: docs/architecture.md § Settings.</para>
/// </remarks>
/// <seealso cref="SettingsManager"/>
/// <seealso cref="ReadOnlySettingGroupView"/>
public sealed class SettingGroup : IReadOnlySettingGroup
{
    #region Group name constants

    /// <summary>Name used for the default group (empty string).</summary>
    public const string DefaultGroupName = "";

    /// <summary>Default UI name for the default group.</summary>
    public const string DefaultGroupUiName = "General";

    #endregion

    #region Group state

    /// <summary>
    /// The settings of the group in registration order, replaced as a whole on every registration.
    /// Invariant: written only by SettingsManager.RegisterSetting under the manager lock (one writer), read by any thread.
    /// Perf: copy-on-write. A registration copies the array once (startup, rare); a read is one Volatile.Read with no lock and no
    /// allocation. Rejected: ReaderWriterLockSlim (a disposable field, a suppression for CA1001, and a lock for sections of a few
    /// nanoseconds). Residual risk: a second concurrent writer would lose a registration; AddSetting says so and the manager is
    /// the only caller.
    /// </summary>
    private Setting[] _Settings = [];

    #endregion

    #region Construction

    /// <summary>Creates a new settings group.</summary>
    /// <param name="name">
    /// The group name: empty for the default group, otherwise a lower-case, dot-separated ASCII identifier other than <c>default</c>.
    /// </param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when the UI name is invalid.</exception>
    public SettingGroup(string name, string uiName, string? description = null)
    {
        if (!SettingNames.IsValidGroupName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!SettingNames.IsValidUiName(uiName))
        {
            throw InvalidUiNameSettingsException.For(uiName);
        }

        Name = name;
        UiName = uiName;
        Description = description;
    }

    /// <summary>Creates the default settings group.</summary>
    /// <returns>A group with the name <see cref="DefaultGroupName"/> and the UI name <see cref="DefaultGroupUiName"/>.</returns>
    public static SettingGroup Default() =>
        new(DefaultGroupName, DefaultGroupUiName);

    #endregion

    #region Group metadata and settings

    /// <summary>The unique machine-readable name (empty string for default group).</summary>
    public string Name { get; }

    /// <summary>The human-readable display name.</summary>
    public string UiName { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }

    /// <summary>Returns true if this is the default group.</summary>
    public bool IsDefaultGroup => Name.Length == 0;

    /// <summary>Returns the number of settings in this group. Lock-free.</summary>
    public int SettingCount => Volatile.Read(ref _Settings).Length;

    /// <summary>
    /// Adds a setting to this group. Single writer: <see cref="SettingsManager"/> calls it under its lock. Does not check
    /// for duplicates or for a matching group name: the manager does both before it calls this member.
    /// </summary>
    /// <param name="setting">The setting to add; it is appended, so the order of the list is the registration order.</param>
    internal void AddSetting(Setting setting)
    {
        Setting[] current = _Settings;
        Setting[] next = new Setting[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[^1] = setting;
        Volatile.Write(ref _Settings, next);
    }

    /// <summary>Gets a snapshot of all settings in this group as read-only struct views.</summary>
    public IReadOnlyList<ReadOnlySettingView> Settings =>
        ReadOnlySettingView.Wrap(CopySettings());

    /// <summary>
    /// Gets a zero-allocation struct view of this group.
    /// Do not assign the result to <see cref="IReadOnlySettingGroup"/> — that boxes.
    /// </summary>
    /// <returns>A view over this group.</returns>
    public ReadOnlySettingGroupView AsReadOnlyView() => new(this);

    /// <summary>
    /// Gets a read-only snapshot of the mutable setting instances for manager-internal use, in registration order. A later
    /// registration does not appear in it.
    /// </summary>
    /// <returns>A read-only wrapper over the published array: no copy, one small allocation.</returns>
    internal IReadOnlyList<Setting> CopySettings() => Array.AsReadOnly(Volatile.Read(ref _Settings));

    #endregion
}
