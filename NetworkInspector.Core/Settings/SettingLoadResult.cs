// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Result of loading a persisted value for a setting: at registration (a value that was loaded before the setting existed, or
/// preloaded) and as the outcome of converting one persisted JSON node to a <see cref="SettingValue"/>. In both cases a value
/// that is refused is skipped and the setting keeps its default, so a damaged file never blocks startup.
/// </summary>
public enum SettingLoadResult : byte
{
    #region Enum Values

    /// <summary>No persisted value was found; the setting uses its default value.</summary>
    NoPersistedValue = 0,

    /// <summary>A persisted value was found and successfully loaded.</summary>
    Success = 1,

    /// <summary>A persisted value was found but is incompatible with the setting type (wrong JSON shape, or an enum number that is not allowed).</summary>
    TypeMismatch = 2,

    /// <summary>A persisted value has the right shape but its content cannot be decoded (for example invalid base64 for Bytes).</summary>
    DeserializationError = 3,

    /// <summary>A persisted value was found but violates the constraints of the setting (for example a bound).</summary>
    OutOfRange = 4,

    #endregion
}
