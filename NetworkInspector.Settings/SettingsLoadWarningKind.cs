// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Categorizes the type of issue encountered while loading persisted settings.
/// </summary>
public enum SettingsLoadWarningKind
{
    #region Warning kinds

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
    /// (e.g. value below minimum). A non-finite F64 never gets this far: it is reported as <see cref="TypeMismatch"/>.
    /// The value is ignored and the setting keeps its default or previously applied value.
    /// </summary>
    OutOfRange,

    /// <summary>
    /// The value in the JSON file could not be deserialized for the registered setting type
    /// (e.g. invalid base64 for a Bytes setting, an enum object that repeats a property name).
    /// The value is ignored and the setting keeps its default or previously applied value.
    /// </summary>
    DeserializationError,

    /// <summary>
    /// The root JSON node in a group file is not a JSON object (e.g. it is an array or a scalar).
    /// The file is skipped entirely and no settings in that group are changed.
    /// </summary>
    InvalidGroupFileShape,

    /// <summary>
    /// A group settings JSON file contains syntactically invalid JSON or repeats a property name; a file that nests deeper than
    /// <see cref="SettingsLimitValues.MaxJsonDepth"/> is <see cref="JsonTooDeep"/>.
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
    /// The component that owns the setting continues with an empty or default configuration.
    /// </summary>
    ExternalConfigUnavailable,

    /// <summary>
    /// A group settings file is larger than <see cref="SettingsLimitValues.MaxFileSize"/>. The message is the standard hit
    /// sentence with the limit and its key. The file is skipped entirely and no settings in that group are changed.
    /// </summary>
    FileTooLarge,

    /// <summary>
    /// A group settings file nests deeper than <see cref="SettingsLimitValues.MaxJsonDepth"/>. The message is the standard hit
    /// sentence with the limit and its key. The file is skipped entirely and no settings in that group are changed.
    /// </summary>
    JsonTooDeep,

    /// <summary>
    /// The settings folder holds more group files than <see cref="SettingsLimitValues.MaxGroupFiles"/>. The message is the standard
    /// hit sentence with the limit and its key. The files after the limit are not read; the files before it are loaded.
    /// </summary>
    TooManyGroupFiles,

    /// <summary>
    /// A key of a registered setting stands in the file of another group. The value is not applied (the order of the directory
    /// listing would decide which file wins); it stays with the file as an orphan, so a save keeps it.
    /// </summary>
    WrongGroupFile,

    #endregion
}
