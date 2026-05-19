// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Event arguments for the <see cref="SettingsManager.SettingsApplied"/> event.
/// <para>
/// Carries the names of all settings that were changed during
/// <see cref="SettingsManager.ApplyChanges"/>.
/// </para>
/// This type is not thread-safe. It is intended for single-threaded event consumption.
/// </summary>
/// <param name="changedSettings">The names of the settings that changed.</param>
public sealed class SettingsAppliedEventArgs(IReadOnlyList<string> changedSettings) : EventArgs
{
    /// <summary>Gets the names of all settings that changed.</summary>
    public IReadOnlyList<string> ChangedSettings { get; } = changedSettings;

    /// <summary>Alias for <see cref="ChangedSettings"/>. Returns the names of all settings that changed.</summary>
    public IReadOnlyList<string> ChangedSettingNames => ChangedSettings;
}