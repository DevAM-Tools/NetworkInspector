// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Represents an enum value with its name and numeric representation.
/// Used for defining allowed values in enum settings.
/// </summary>
/// <remarks>Creates a new enum setting value.</remarks>
public readonly struct EnumSettingValue : IEquatable<EnumSettingValue>
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
    public string Name
    {
        get;
    }

    /// <summary>The numeric value of the enum.</summary>
    public ulong NumericValue
    {
        get;
    }

    #endregion

    #region Equality

    /// <inheritdoc />
    public bool Equals(EnumSettingValue other) =>
        Name == other.Name && NumericValue == other.NumericValue;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is EnumSettingValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Name, NumericValue);

    /// <inheritdoc />
    public override string ToString() => Name;

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if both values are equal.</summary>
    public static bool operator ==(EnumSettingValue left, EnumSettingValue right) => left.Equals(right);

    /// <summary>Returns <see langword="true"/> if the values are not equal.</summary>
    public static bool operator !=(EnumSettingValue left, EnumSettingValue right) => !left.Equals(right);

    #endregion
}
