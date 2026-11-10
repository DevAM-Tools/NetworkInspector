// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// The one validation of setting values against type, bounds, allowed enumeration values, and finiteness. The
/// <see cref="Setting"/> mutators, the text codec (<see cref="SettingValueText"/>), and the JSON codec
/// (<see cref="SettingValueJson"/>) all call it, so a value is refused with the same <see cref="SettingProblem"/> wherever it
/// comes from. Also holds the quoting rule that every refusal message uses for input it echoes. Stateless; thread-safe.
/// </summary>
internal static class SettingValidation
{
    #region Message rules

    /// <summary>Separator of the two bounds in range messages (<c>allowed 1 – 65535</c>): a spaced en dash.</summary>
    private const string _RangeSeparator = " – ";

    /// <summary>
    /// Display rule, not a limit: a refusal quotes at most this many characters of the input. The same number as the limit texts
    /// of <see cref="TableUnit"/>, so the two libraries shorten alike.
    /// </summary>
    private const int _MaxQuotedLength = 32;

    /// <summary>Display rule, not a limit: a message repeats at most this many characters of a library exception text.</summary>
    private const int _MaxQuotedMessageLength = 200;

    /// <summary>
    /// Shortens input for a refusal and replaces its control characters, so the message stays short and cannot forge a line.
    /// </summary>
    /// <param name="text">The input to quote: a value text, a key, or a group name.</param>
    /// <returns>
    /// The input unchanged when it has at most 32 characters and no control character; otherwise at most 32 characters, each
    /// control character replaced by U+FFFD, followed by an ellipsis when the input was shortened.
    /// </returns>
    /// <remarks>
    /// Security: the text comes from files, command lines, and remote clients and ends up in terminals and logs. Threat: forged
    /// log lines (CWE-117), terminal control sequences, and a message as large as the input. Mitigation: a length cap and U+FFFD
    /// for each control character. Rejected: quoting as given. Residual risk: none beyond a shortened quote. Cost: one small string.
    /// </remarks>
    internal static string Quote(ReadOnlySpan<char> text)
    {
        int length = Math.Min(text.Length, _MaxQuotedLength);
        Span<char> buffer = stackalloc char[_MaxQuotedLength + 1];
        return _Sanitize(text, length, buffer);
    }

    /// <summary>
    /// Shortens the text of a library exception (a parser failure) that a message repeats, and replaces its control characters.
    /// The parser text can carry names from the file, so it is bounded like any other input.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <returns>At most 200 characters, each control character replaced by U+FFFD, followed by an ellipsis when shortened.</returns>
    /// <remarks>Security: as <see cref="Quote"/>; the larger cap keeps a parser explanation readable. Cost: one small string.</remarks>
    internal static string QuoteMessage(string message)
    {
        int length = Math.Min(message.Length, _MaxQuotedMessageLength);
        Span<char> buffer = stackalloc char[_MaxQuotedMessageLength + 1];
        return _Sanitize(message, length, buffer);
    }

    /// <summary>Copies the start of a text into a buffer, replaces control characters, and marks a cut with an ellipsis.</summary>
    /// <param name="text">The input.</param>
    /// <param name="length">How many characters to keep; at most the length of <paramref name="text"/>.</param>
    /// <param name="buffer">Scratch space of at least <c>length + 1</c> characters.</param>
    /// <returns>The sanitized, possibly shortened text.</returns>
    private static string _Sanitize(ReadOnlySpan<char> text, int length, Span<char> buffer)
    {
        for (int index = 0; index < length; index++)
        {
            buffer[index] = char.IsControl(text[index])
                ? '�'
                : text[index];
        }

        if (text.Length > length)
        {
            buffer[length++] = '…';
        }

        return new string(buffer[..length]);
    }

    #endregion

    #region Validation of a value

    /// <summary>Checks <paramref name="value"/> for <paramref name="setting"/> and describes the first problem.</summary>
    /// <param name="setting">The setting whose type and constraints apply.</param>
    /// <param name="value">The value to check.</param>
    /// <returns>The problem, or <see langword="null"/> when the value is valid.</returns>
    internal static SettingProblem? Validate(IReadOnlySetting setting, SettingValue value)
    {
        // The type is compared first so that the checks below can rely on the value holding the payload of the setting type.
        if (setting.Type != value.Type)
        {
            return new SettingProblem(SettingProblemKind.WrongType, setting.Name, Invariant($"expected {setting.Type}, got {value.Type}"));
        }

        // Only numeric types, enumerations, and their arrays carry constraints; Bool, String, Bytes, BoolArray, and StringArray
        // accept every value of their type (the final null arm). Array settings apply the bounds of the setting to each
        // element. The element delegates capture the setting and allocate per call; that is accepted because validation runs on
        // edits, loads, and text parsing, never on the read path of a setting.
        return setting.Type switch
        {
            SettingType.F64 when value.TryGetAsF64(out double f64v) => ValidateF64(setting, f64v),
            SettingType.U64 when value.TryGetAsU64(out ulong u64v) => ValidateU64(setting, u64v),
            SettingType.I64 when value.TryGetAsI64(out long i64v) => ValidateI64(setting, i64v),
            SettingType.Limit when value.TryGetAsLimit(out LimitValue limit) => ValidateLimit(setting, limit),
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
    /// <param name="setting">The setting whose bounds apply.</param>
    /// <param name="v">The number to check.</param>
    /// <returns>The problem, or <see langword="null"/> when the number is valid.</returns>
    internal static SettingProblem? ValidateF64(IReadOnlySetting setting, double v)
    {
        // Finiteness is checked before the bounds: every comparison with NaN is false, so NaN would otherwise pass the range
        // check and be stored (and later fail to serialize, because JSON has no NaN or infinity).
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
    /// <param name="setting">The setting whose bounds apply.</param>
    /// <param name="v">The number to check.</param>
    /// <returns>The problem, or <see langword="null"/> when the number is inside the bounds.</returns>
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
    /// <param name="setting">The setting whose bounds apply.</param>
    /// <param name="v">The number to check.</param>
    /// <returns>The problem, or <see langword="null"/> when the number is inside the bounds.</returns>
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

    /// <summary>
    /// Checks a limit value against the hard range of its definition; off is valid when the definition allows it
    /// (<see cref="LimitDefinition.AllowOff"/>).
    /// </summary>
    /// <param name="setting">The limit setting; its <see cref="IReadOnlySetting.LimitDefinition"/> holds the range.</param>
    /// <param name="value">The value to check.</param>
    /// <returns>
    /// The below or above problem with the allowed range, a wrong-type problem for off on a limit that cannot be switched off,
    /// or <see langword="null"/> when the value is valid.
    /// </returns>
    internal static SettingProblem? ValidateLimit(IReadOnlySetting setting, LimitValue value)
    {
        // The factories (Setting.FromLimit, FromLimitOption) are the only way to build a limit setting, but another
        // IReadOnlySetting implementation could claim the type without a definition; refuse it instead of dereferencing null.
        if (setting.LimitDefinition is not { } definition)
        {
            return new SettingProblem(SettingProblemKind.WrongType, setting.Name, "a limit setting needs a limit definition");
        }

        // Read-only: the only valid value is the default. Checked before the range, so a read-only limit never reports a range
        // problem, and a file or text can never change it (the same check serves the codecs and the pending value).
        if (definition.ReadOnly && value != definition.DefaultValue)
        {
            return new SettingProblem(SettingProblemKind.ReadOnly, setting.Name, "the limit is read-only");
        }

        if (definition.TryValidateRange(value, out string? problem))
        {
            return null;
        }

        // Off reaches this point only when the definition refuses it: a refused off is the wrong form, not a number below or above
        // the range (Value would throw for it).
        if (value.IsOff)
        {
            return new SettingProblem(SettingProblemKind.WrongType, setting.Name, problem);
        }

        // The value is a number; its side of the range decides the kind.
        return new SettingProblem(
            value.Value < definition.Minimum
                ? SettingProblemKind.BelowMinimum
                : SettingProblemKind.AboveMaximum,
            setting.Name,
            problem);
    }

    /// <summary>Checks that the number of an enumeration value is one of the allowed values.</summary>
    /// <param name="setting">The enum setting; without metadata every value passes.</param>
    /// <param name="value">The value to check.</param>
    /// <returns>The problem that lists the allowed names, or <see langword="null"/> when the value is allowed.</returns>
    internal static SettingProblem? ValidateEnum(IReadOnlySetting setting, SettingValue value)
    {
        // Without metadata there is no allowed set to check against; a setting created through the factories always has it.
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
    /// <param name="setting">The enum setting whose allowed names are listed.</param>
    /// <returns>The problem of the kind <see cref="SettingProblemKind.UnknownEnum"/>.</returns>
    internal static SettingProblem UnknownEnum(IReadOnlySetting setting) =>
        new(SettingProblemKind.UnknownEnum, setting.Name, "allowed: " + AllowedEnumNames(setting.EnumMetadata));

    /// <summary>Builds the below or above problem with the effective range: the bounds of the setting, else the type range.</summary>
    /// <param name="setting">The numeric setting whose range is named.</param>
    /// <param name="kind">Below minimum or above maximum.</param>
    /// <returns>The problem with the allowed range in its message.</returns>
    internal static SettingProblem RangeProblem(IReadOnlySetting setting, SettingProblemKind kind)
    {
        (string min, string max) = RangeTexts(setting);
        return new SettingProblem(kind, setting.Name, "allowed " + min + _RangeSeparator + max);
    }

    #endregion

    #region Range and name texts

    /// <summary>
    /// Formats the effective bounds of a numeric setting: the configured minimum and maximum, and for a missing bound the
    /// limit of the type (the arrays use the type of their elements).
    /// </summary>
    /// <param name="setting">The numeric setting.</param>
    /// <returns>The smallest and the largest accepted value as invariant text.</returns>
    internal static (string Min, string Max) RangeTexts(IReadOnlySetting setting)
    {
        switch (setting.Type)
        {
            case SettingType.U64:
            case SettingType.U64Array:
                ulong unsignedMin = setting.MinValue is not null && setting.MinValue.Value.TryGetAsU64(out ulong uMin)
                    ? uMin
                    : ulong.MinValue;
                ulong unsignedMax = setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsU64(out ulong uMax)
                    ? uMax
                    : ulong.MaxValue;
                return (unsignedMin.ToString(CultureInfo.InvariantCulture), unsignedMax.ToString(CultureInfo.InvariantCulture));
            case SettingType.I64:
            case SettingType.I64Array:
                long signedMin = setting.MinValue is not null && setting.MinValue.Value.TryGetAsI64(out long iMin)
                    ? iMin
                    : long.MinValue;
                long signedMax = setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsI64(out long iMax)
                    ? iMax
                    : long.MaxValue;
                return (signedMin.ToString(CultureInfo.InvariantCulture), signedMax.ToString(CultureInfo.InvariantCulture));
            default:
                // -double.MaxValue, not negative infinity: a setting never holds an infinite number, so the finite type range
                // is the range the user can actually enter.
                double floatMin = setting.MinValue is not null && setting.MinValue.Value.TryGetAsF64(out double fMin)
                    ? fMin
                    : -double.MaxValue;
                double floatMax = setting.MaxValue is not null && setting.MaxValue.Value.TryGetAsF64(out double fMax)
                    ? fMax
                    : double.MaxValue;
                return (FormatF64(floatMin), FormatF64(floatMax));
        }
    }

    /// <summary>Formats a floating-point number in the invariant round-trip form.</summary>
    /// <param name="value">The number to format.</param>
    /// <returns>The shortest text that parses back to the same number.</returns>
    internal static string FormatF64(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Lists the allowed enumeration names in registration order, separated by commas.</summary>
    /// <param name="metadata">The enum metadata, or <see langword="null"/>.</param>
    /// <returns>The names, or an empty string without metadata.</returns>
    internal static string AllowedEnumNames(EnumSettingMetadata? metadata)
    {
        if (metadata is null)
        {
            return string.Empty;
        }

        return string.Join(", ", metadata.AllowedValues.Select(static allowed => allowed.Name));
    }

    #endregion

    #region Elements of arrays

    /// <summary>Checks every element with the scalar check; the first problem is reported with its 1-based position.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The elements of an array setting.</param>
    /// <param name="validate">The scalar check applied to each element.</param>
    /// <returns>The first problem with the position of its element, or <see langword="null"/> when every element is valid.</returns>
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
