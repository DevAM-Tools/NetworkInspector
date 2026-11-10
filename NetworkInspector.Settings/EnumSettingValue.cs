// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Represents an enum value with its name and numeric representation.
/// Used for defining allowed values in enum settings.
/// An immutable value type; note that <c>default(EnumSettingValue)</c> has a <see langword="null"/> <see cref="Name"/>
/// (the constructor rejects null, but a default struct bypasses it), so always create instances through the constructor.
/// </summary>
public readonly record struct EnumSettingValue
{
    #region Construction

    /// <summary>Creates a new enum setting value.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    /// <param name="name">The name; not <see langword="null"/>.</param>
    /// <param name="numericValue">The number.</param>
    public EnumSettingValue(string name, ulong numericValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        NumericValue = numericValue;
    }

    #endregion

    #region Name and number

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
