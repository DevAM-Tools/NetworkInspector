// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Represents a group of related settings for UI organization.
/// Thread-safe: uses <see cref="ReaderWriterLockSlim"/> to allow concurrent
/// readers while serializing writes (setting registration).
/// <para>
/// Disposal of the internal lock is handled by <see cref="SettingsManager"/> ownership.
/// </para>
/// </summary>
// Suppression approved: CA1001 — ReaderWriterLockSlim lifetime is owned by SettingsManager.DisposeResources(), not SettingGroup callers.
[SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "Disposal is handled internally by SettingsManager ownership.")]
public sealed class SettingGroup : IReadOnlySettingGroup
{
    #region Constants

    /// <summary>Name used for the default group (empty string).</summary>
    public const string DefaultGroupName = "";

    /// <summary>Default UI name for the default group.</summary>
    public const string DefaultGroupUiName = "General";

    #endregion

    #region Fields

    private readonly ReaderWriterLockSlim _Lock = new();
    private readonly List<Setting> _Settings = [];

    #endregion

    #region Constructors

    /// <summary>Creates a new settings group.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name or UI name is invalid.</exception>
    public SettingGroup(string name, string uiName, string? description = null)
    {
        if (!NameValidation.IsValidGroupName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!NameValidation.IsValidUiName(uiName))
        {
            throw InvalidNameSettingsException.ForUiName(uiName);
        }

        Name = name;
        UiName = uiName;
        Description = description;
    }

    /// <summary>Creates the default settings group.</summary>
    public static SettingGroup Default() =>
        new(DefaultGroupName, DefaultGroupUiName);

    #endregion

    #region Properties

    /// <summary>The unique machine-readable name (empty string for default group).</summary>
    public string Name { get; }

    /// <summary>The human-readable display name.</summary>
    public string UiName { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }

    /// <summary>Returns true if this is the default group.</summary>
    public bool IsDefaultGroup => Name.Length == 0;

    /// <summary>Returns the number of settings in this group.</summary>
    public int SettingCount
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return _Settings.Count;
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Adds a setting to this group.</summary>
    internal void AddSetting(Setting setting)
    {
        _Lock.EnterWriteLock();
        try
        {
            _Settings.Add(setting);
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    /// <summary>Gets a snapshot of all settings in this group as read-only struct views.</summary>
    public IReadOnlyList<ReadOnlySettingView> Settings =>
        ReadOnlySettingView.Wrap(CopySettings());

    /// <summary>
    /// Gets a zero-allocation struct view of this group.
    /// Do not assign the result to <see cref="IReadOnlySettingGroup"/> — that boxes.
    /// </summary>
    public ReadOnlySettingGroupView AsReadOnlyView() => new(this);

    /// <summary>Gets a snapshot of the mutable setting instances for manager-internal use.</summary>
    internal IReadOnlyList<Setting> CopySettings()
    {
        _Lock.EnterReadLock();
        try
        {
            return [.. _Settings];
        }
        finally
        {
            _Lock.ExitReadLock();
        }
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Iterates over settings without allocating a copy.
    /// <para><b>Thread-safety:</b> The callback is invoked while holding the read lock.
    /// Do not perform long-running operations inside the callback.</para>
    /// </summary>
    internal void ForEachSetting(Action<Setting> action)
    {
        _Lock.EnterReadLock();
        try
        {
            foreach (Setting setting in _Settings)
            {
                action(setting);
            }
        }
        finally
        {
            _Lock.ExitReadLock();
        }
    }

    /// <summary>Releases the internal <see cref="ReaderWriterLockSlim"/>.</summary>
    internal void DisposeResources() => _Lock.Dispose();

    #endregion
}
