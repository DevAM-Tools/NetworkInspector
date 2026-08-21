// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Represents an enum value with its name and numeric representation.
/// Used for defining allowed values in enum settings.
/// </summary>
public readonly record struct EnumSettingValue
{
    #region Constructors

    /// <summary>Creates a new enum setting value.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    public EnumSettingValue(string name, ulong numericValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        NumericValue = numericValue;
    }

    #endregion

    #region Properties

    /// <summary>The name of the enum value.</summary>
    public string Name { get; }

    /// <summary>The numeric value of the enum.</summary>
    public ulong NumericValue { get; }

    #endregion

    #region Formatting

    /// <inheritdoc />
    public override string ToString() => Name;

    #endregion
}
