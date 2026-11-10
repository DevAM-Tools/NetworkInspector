// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// The <c>--set key=value</c> overrides of one run: the loader limits that must be known before the first settings file is read,
/// and the application of one override to a registered setting. An override is applied like <c>ni settings set</c> applies a value
/// (same parser, same validation, same messages) but is never saved. Stateless; thread-safe.
/// </summary>
internal static class CliSettingOverrides
{
    #region Loader limits before the first file

    /// <summary>
    /// Reads the overrides of the five loader limits of the settings library from the options. They have to be parsed before any
    /// manager exists, because the manager reads them while it loads its first file.
    /// </summary>
    /// <param name="options">The settings options of the command.</param>
    /// <param name="limits">The values for the manager: the secure defaults, changed by the overrides.</param>
    /// <param name="any">Whether at least one override names a loader limit.</param>
    /// <param name="error">The reason with the key and the allowed range when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every override of a loader limit is valid.</returns>
    internal static bool TryReadLoaderLimits(CliSettingsOptions options, out SettingsLimitValues limits, out bool any,
        [NotNullWhen(false)] out string? error)
    {
        limits = SettingsLimitValues.Default;
        any = false;
        error = null;
        foreach (KeyValuePair<string, string> pair in options.Overrides)
        {
            LimitDefinition? definition = SettingsLimits.All.FirstOrDefault(candidate => string.Equals(candidate.Key, pair.Key, StringComparison.Ordinal));
            if (definition is null)
            {
                continue;
            }

            // The refusal of the definition names the key, what is wrong, and what to write instead.
            if (!definition.TryParse(pair.Value, out LimitValue value, out string? problem))
            {
                error = problem;
                return false;
            }

            any = true;
            limits = ReferenceEquals(definition, SettingsLimits.MaxFileSize)
                ? limits with { MaxFileSize = value }
                : ReferenceEquals(definition, SettingsLimits.MaxJsonDepth)
                    ? limits with { MaxJsonDepth = value }
                    : ReferenceEquals(definition, SettingsLimits.FolderLockTimeout)
                        ? limits with { FolderLockTimeout = value }
                        : ReferenceEquals(definition, SettingsLimits.MaxGroupFiles)
                            ? limits with { MaxGroupFiles = value }
                            : limits with { MaxTextLength = value };
        }

        return true;
    }

    #endregion

    #region One override

    /// <summary>Parses the text of an override for a setting, validates it, and makes it the current value; nothing is saved.</summary>
    /// <param name="manager">The manager that owns the setting; its text length limit applies to the text.</param>
    /// <param name="setting">The registered setting.</param>
    /// <param name="text">The text after the <c>=</c>.</param>
    /// <param name="value">The parsed value when the method returns <see langword="true"/>.</param>
    /// <param name="error">The problem (key, reason, range) when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the value is now current.</returns>
    internal static bool TryApply(SettingsManager manager, Setting setting, string text, out SettingValue value,
        [NotNullWhen(false)] out string? error)
    {
        if (!SettingValueText.TryParse(setting, text, manager.Limits.MaxTextLength, out value, out SettingProblem? problem))
        {
            error = problem.ToString();
            return false;
        }

        if (!setting.TrySetPendingValue(value, out SettingProblem? setProblem))
        {
            error = setProblem.ToString();
            return false;
        }

        manager.ApplyChanges([setting.Name]);
        error = null;
        return true;
    }

    #endregion
}
