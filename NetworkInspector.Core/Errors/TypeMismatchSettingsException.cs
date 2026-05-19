// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown when a setting value's type does not match the expected setting type
/// (e.g., assigning a string value to a boolean setting).
/// </summary>
public sealed class TypeMismatchSettingsException(SettingType expected, SettingType actual)
    : SettingsException($"Type mismatch: expected {expected}, got {actual}")
{
    #region Properties

    /// <summary>The expected setting type.</summary>
    public SettingType ExpectedType
    {
        get;
    } = expected;

    /// <summary>The actual setting type that was provided.</summary>
    public SettingType ActualType
    {
        get;
    } = actual;

    #endregion

    #region Factory Methods

    /// <summary>Creates a type-mismatch exception for the given types.</summary>
    internal static TypeMismatchSettingsException For(SettingType expected, SettingType actual) =>
        new(expected, actual);

    #endregion
}