// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Event arguments for the <see cref="SettingsManager.SettingsApplied"/> event.
/// <para>
/// Carries the names of all settings whose current value changed in one operation: <see cref="SettingsManager.ApplyChanges()"/>,
/// <see cref="SettingsManager.ApplyChanges(IEnumerable{string})"/>, or <see cref="SettingsManager.Unset"/>.
/// </para>
/// <para>
/// The list is not copied: the manager passes the same list it returns from <c>ApplyChanges</c> to the caller. Nothing
/// modifies it after the event is raised, and a handler must not modify it either. This type is not thread-safe. It is
/// intended for single-threaded event consumption (handlers run synchronously on the thread that applied the change).
/// </para>
/// </summary>
/// <param name="changedSettings">The names of the settings that changed.</param>
public sealed class SettingsAppliedEventArgs(IReadOnlyList<string> changedSettings) : EventArgs
{
    /// <summary>Gets the names of all settings that changed.</summary>
    public IReadOnlyList<string> ChangedSettings { get; } = changedSettings;

    /// <summary>Alias for <see cref="ChangedSettings"/>. Returns the names of all settings that changed.</summary>
    public IReadOnlyList<string> ChangedSettingNames => ChangedSettings;
}
