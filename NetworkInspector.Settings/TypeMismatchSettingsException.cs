// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Thrown when a setting value's type does not match the expected setting type
/// (e.g., assigning a string value to a boolean setting).
/// </summary>
/// <param name="expected">The type the setting is declared with.</param>
/// <param name="actual">The type of the value that was supplied.</param>
public sealed class TypeMismatchSettingsException(SettingType expected, SettingType actual)
    : SettingsException(Invariant($"Type mismatch: expected {expected}, got {actual}"))
{
    #region Expected and actual type

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

    #region Creating the exception

    /// <summary>Creates a type-mismatch exception for the given types.</summary>
    /// <param name="expected">The declared setting type.</param>
    /// <param name="actual">The supplied value type.</param>
    internal static TypeMismatchSettingsException For(SettingType expected, SettingType actual) =>
        new(expected, actual);

    #endregion
}
