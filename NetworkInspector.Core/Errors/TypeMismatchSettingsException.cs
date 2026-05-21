// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
