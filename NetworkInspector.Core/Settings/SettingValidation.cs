// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// The one validation of setting values against type, bounds, allowed enumeration values, and finiteness. The
/// <see cref="Setting"/> mutators, the text codec (<see cref="SettingValueText"/>), and the JSON codec
/// (<see cref="SettingValueJson"/>) all call it, so a value is refused with the same <see cref="SettingProblem"/> wherever it
/// comes from. Stateless; thread-safe.
/// </summary>
internal static class SettingValidation
{
    #region Constants

    /// <summary>Separator of the two bounds in range messages (<c>allowed 1 – 65535</c>): a spaced en dash.</summary>
    private const string _RangeSeparator = " – ";

    #endregion

    #region Internal API

    /// <summary>Checks <paramref name="value"/> for <paramref name="setting"/> and describes the first problem.</summary>
    /// <param name="setting">The setting whose type and constraints apply.</param>
    /// <param name="value">The value to check.</param>
    /// <returns>The problem, or <see langword="null"/> when the value is valid.</returns>
    internal static SettingProblem? Validate(IReadOnlySetting setting, SettingValue value)
    {
        if (setting.Type != value.Type)
        {
            return new SettingProblem(SettingProblemKind.WrongType, setting.Name, $"expected {setting.Type}, got {value.Type}");
        }

        return setting.Type switch
        {
            SettingType.F64 when value.TryGetAsF64(out double f64v) => ValidateF64(setting, f64v),
            SettingType.U64 when value.TryGetAsU64(out ulong u64v) => ValidateU64(setting, u64v),
            SettingType.I64 when value.TryGetAsI64(out long i64v) => ValidateI64(setting, i64v),
            SettingType.Enum => ValidateEnum(setting, value),
            SettingType.F64Array when value.TryGetAsF64Array(out double[] f64a) =>
                _ValidateElements(f64a, element => ValidateF64(setting, element)),
            SettingType.U64Array when value.TryGetAsU64Array(out ulong[] u64a) =>
                _ValidateElements(u64a, element => ValidateU64(setting, element)),
            SettingType.I64Array when value.TryGetAsI64Array(out long[] i64a) =>
                _ValidateElements(i64a, element => ValidateI64(setting, element)),
            _ => null,
        };
    }

    /// <summary>Checks one floating-point number: finite, then inside the bounds.</summary>
    internal static SettingProblem? ValidateF64(IReadOnlySetting setting, double v)
    {
        if (!double.IsFinite(v))
        {
            return new SettingProblem(SettingProblemKind.NotFinite, setting.Name,
                string.Create(CultureInfo.InvariantCulture, $"'{v}' is not a finite number"));
        }

        bool belowMin = setting.MinValue is not null && setting.MinValue.Value.TryGetAsF64(out double minF64) && v < minF64;
        bool aboveMax = setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsF64(out double maxF64) && v > maxF64;
        if (!belowMin && !aboveMax)
        {
            return null;
        }

        return RangeProblem(setting, belowMin
            ? SettingProblemKind.BelowMinimum
            : SettingProblemKind.AboveMaximum);
    }

    /// <summary>Checks one unsigned number against the bounds.</summary>
    internal static SettingProblem? ValidateU64(IReadOnlySetting setting, ulong v)
    {
        bool belowMin = setting.MinValue is not null && setting.MinValue.Value.TryGetAsU64(out ulong minU64) && v < minU64;
        bool aboveMax = setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsU64(out ulong maxU64) && v > maxU64;
        if (!belowMin && !aboveMax)
        {
            return null;
        }

        return RangeProblem(setting, belowMin
            ? SettingProblemKind.BelowMinimum
            : SettingProblemKind.AboveMaximum);
    }

    /// <summary>Checks one signed number against the bounds.</summary>
    internal static SettingProblem? ValidateI64(IReadOnlySetting setting, long v)
    {
        bool belowMin = setting.MinValue is not null && setting.MinValue.Value.TryGetAsI64(out long minI64) && v < minI64;
        bool aboveMax = setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsI64(out long maxI64) && v > maxI64;
        if (!belowMin && !aboveMax)
        {
            return null;
        }

        return RangeProblem(setting, belowMin
            ? SettingProblemKind.BelowMinimum
            : SettingProblemKind.AboveMaximum);
    }

    /// <summary>Checks that the number of an enumeration value is one of the allowed values.</summary>
    internal static SettingProblem? ValidateEnum(IReadOnlySetting setting, SettingValue value)
    {
        if (setting.EnumMetadata is null)
        {
            return null;
        }

        // A value that is not an enumeration value, or one whose number is not allowed, lists the allowed names.
        if (!value.TryGetAsEnum(out (string _, ulong numericValue) e) || !setting.EnumMetadata.IsAllowedNumeric(e.numericValue))
        {
            return UnknownEnum(setting);
        }

        return null;
    }

    /// <summary>Builds the problem for a value that is not an allowed enumeration value.</summary>
    internal static SettingProblem UnknownEnum(IReadOnlySetting setting) =>
        new(SettingProblemKind.UnknownEnum, setting.Name, "allowed: " + AllowedEnumNames(setting.EnumMetadata));

    /// <summary>Builds the below or above problem with the effective range: the bounds of the setting, else the type range.</summary>
    internal static SettingProblem RangeProblem(IReadOnlySetting setting, SettingProblemKind kind)
    {
        (string min, string max) = RangeTexts(setting);
        return new SettingProblem(kind, setting.Name, "allowed " + min + _RangeSeparator + max);
    }

    /// <summary>
    /// Formats the effective bounds of a numeric setting: the configured minimum and maximum, and for a missing bound the
    /// limit of the type (the arrays use the type of their elements).
    /// </summary>
    internal static (string Min, string Max) RangeTexts(IReadOnlySetting setting)
    {
        switch (setting.Type)
        {
            case SettingType.U64:
            case SettingType.U64Array:
                return (
                    (setting.MinValue is not null && setting.MinValue.Value.TryGetAsU64(out ulong uMin) ? uMin : ulong.MinValue)
                        .ToString(CultureInfo.InvariantCulture),
                    (setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsU64(out ulong uMax) ? uMax : ulong.MaxValue)
                        .ToString(CultureInfo.InvariantCulture));
            case SettingType.I64:
            case SettingType.I64Array:
                return (
                    (setting.MinValue is not null && setting.MinValue.Value.TryGetAsI64(out long iMin) ? iMin : long.MinValue)
                        .ToString(CultureInfo.InvariantCulture),
                    (setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsI64(out long iMax) ? iMax : long.MaxValue)
                        .ToString(CultureInfo.InvariantCulture));
            default:
                return (
                    FormatF64(setting.MinValue is not null && setting.MinValue.Value.TryGetAsF64(out double fMin) ? fMin : -double.MaxValue),
                    FormatF64(setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsF64(out double fMax) ? fMax : double.MaxValue));
        }
    }

    /// <summary>Formats a floating-point number in the invariant round-trip form.</summary>
    internal static string FormatF64(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Lists the allowed enumeration names in registration order, separated by commas.</summary>
    internal static string AllowedEnumNames(EnumSettingMetadata? metadata)
    {
        if (metadata is null)
        {
            return string.Empty;
        }

        return string.Join(", ", metadata.AllowedValues.Select(static allowed => allowed.Name));
    }

    #endregion

    #region Private Helpers

    /// <summary>Checks every element with the scalar check; the first problem is reported with its 1-based position.</summary>
    private static SettingProblem? _ValidateElements<T>(T[] values, Func<T, SettingProblem?> validate)
    {
        for (int i = 0; i < values.Length; i++)
        {
            SettingProblem? problem = validate(values[i]);
            if (problem is not null)
            {
                return problem with { Message = string.Create(CultureInfo.InvariantCulture, $"element {i + 1}: {problem.Message}") };
            }
        }

        return null;
    }

    #endregion
}
