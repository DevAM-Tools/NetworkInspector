// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Describes a non-fatal issue detected while loading persisted settings
/// via <see cref="SettingsManager.Load"/>.
/// <para>
/// Load warnings do not prevent the application from starting — invalid or
/// incompatible persisted values are silently skipped and the affected settings
/// keep their default or previously applied values.
/// </para>
/// </summary>
/// <param name="Kind">The category of the load issue.</param>
/// <param name="GroupName">
/// The group name associated with the warning (derived from the JSON file name,
/// or the setting's registered group).
/// </param>
/// <param name="SettingName">
/// The name of the setting that caused the warning, or an empty string when
/// the issue affects a whole group (e.g. <see cref="SettingsLoadWarningKind.InvalidGroupName"/>).
/// </param>
/// <param name="Message">Human-readable description of the issue.</param>
public readonly record struct SettingsLoadWarning(
    SettingsLoadWarningKind Kind,
    string GroupName,
    string SettingName,
    string Message)
{
    #region Formatting

    /// <inheritdoc/>
    public override string ToString() =>
        SettingName.Length == 0
            ? $"[{Kind}] Group '{GroupName}': {Message}"
            : $"[{Kind}] Setting '{SettingName}' (group '{GroupName}'): {Message}";

    #endregion
}
