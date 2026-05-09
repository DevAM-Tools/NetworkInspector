// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Result of loading a persisted value for a setting during registration.
/// </summary>
public enum SettingLoadResult : byte
{
    #region Enum Values

    /// <summary>No persisted value was found; the setting uses its default value.</summary>
    NoPersistedValue = 0,

    /// <summary>A persisted value was found and successfully loaded.</summary>
    Success = 1,

    /// <summary>A persisted value was found but is incompatible with the setting type.</summary>
    TypeMismatch = 2,

    /// <summary>A persisted value was found but failed to deserialize.</summary>
    DeserializationError = 3,

    /// <summary>A persisted value was found but was outside the valid range.</summary>
    OutOfRange = 4,

    #endregion
}
