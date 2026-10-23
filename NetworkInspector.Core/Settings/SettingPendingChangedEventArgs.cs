// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Event arguments for <see cref="SettingsManager.PendingChanged"/>: the pending value of one setting changed (it was set,
/// reset, or returned to its default). Immutable.
/// </summary>
/// <param name="settingName">Name of the setting whose pending value changed.</param>
public sealed class SettingPendingChangedEventArgs(string settingName) : EventArgs
{
    /// <summary>Gets the name of the setting whose pending value changed.</summary>
    public string SettingName { get; } = settingName;
}
