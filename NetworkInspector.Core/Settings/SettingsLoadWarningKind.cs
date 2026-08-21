// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Categorizes the type of issue encountered while loading persisted settings.
/// </summary>
public enum SettingsLoadWarningKind
{
    #region Enum Values

    /// <summary>
    /// The group name derived from a JSON file name is not a valid group name
    /// (must be lowercase dot-separated identifier, e.g. "my.group").
    /// The file is skipped entirely.
    /// </summary>
    InvalidGroupName,

    /// <summary>
    /// The value in the JSON file has a type that does not match the registered setting type.
    /// The value is ignored and the setting keeps its default or previously applied value.
    /// </summary>
    TypeMismatch,

    /// <summary>
    /// The value in the JSON file was parsed but failed range or constraint validation
    /// (e.g. value below minimum, or non-finite F64).
    /// The value is ignored and the setting keeps its default or previously applied value.
    /// </summary>
    OutOfRange,

    /// <summary>
    /// The value in the JSON file could not be deserialized for the registered setting type
    /// (e.g. invalid base64 for a Bytes setting, malformed enum string).
    /// The value is ignored and the setting keeps its default or previously applied value.
    /// </summary>
    DeserializationError,

    /// <summary>
    /// The root JSON node in a group file is not a JSON object (e.g. it is an array or a scalar).
    /// The file is skipped entirely and no settings in that group are changed.
    /// </summary>
    InvalidGroupFileShape,

    /// <summary>
    /// A group settings JSON file contains syntactically invalid JSON.
    /// The file is skipped entirely and no settings in that group are changed.
    /// </summary>
    InvalidGroupFileSyntax,

    /// <summary>
    /// An enum setting was loaded with a numeric value that is allowed but a non-canonical
    /// <c>name</c> in the persisted JSON object. The canonical name from
    /// <see cref="EnumSettingMetadata"/> is applied instead.
    /// </summary>
    EnumNameMismatch,

    /// <summary>
    /// An external JSON configuration file referenced by a string setting could not be loaded.
    /// Covers: file not found, malformed JSON, I/O errors, and access-denied failures.
    /// The owning protocol continues with an empty / default configuration.
    /// </summary>
    ExternalConfigUnavailable,

    #endregion
}
