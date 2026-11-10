// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// The one text form of setting values, used by a command line, by option texts of remote clients and components, and by text inputs
/// of a user interface, so every front end accepts the same texts and gives the same messages.
/// <list type="bullet">
///   <item>Bool: <c>true</c> or <c>false</c> in any case.</item>
///   <item>I64, U64, F64: invariant culture whatever the thread culture is (integers with an optional sign, F64 in float
///   style and finite).</item>
///   <item>Bytes: hex digits with an optional <c>0x</c> prefix; empty text is empty bytes.</item>
///   <item>Enum: a name in any case, or an allowed number.</item>
///   <item>Limit: a whole number with an optional unit word of the unit of its definition (<c>256 MiB</c>,
///   <c>90 s</c>), or <c>off</c> when the definition allows it; ambiguous or inexact forms are refused with the form to write
///   instead.</item>
///   <item>String: the text as given, spaces included.</item>
///   <item>Arrays: a JSON array (<c>[1,2]</c>, <c>["a,b","c"]</c>) or a comma list (<c>1, 2</c>; elements are trimmed);
///   empty text is the empty array.</item>
/// </list>
/// Stateless; thread-safe; never throws for any text.
/// </summary>
public static class SettingValueText
{
    #region Parsing constants

    /// <summary>
    /// Deepest nesting accepted in the JSON array form. An array of scalars needs depth 1; the limit only keeps hostile
    /// nesting from reaching the JSON reader's recursion (the text is a trust boundary).
    /// Limit record: value 64 · threat: deeply nested text exhausting the stack or time of the JSON reader · constant, because
    /// the legitimate form never nests (no application needs a different value) · secure by default. A deeper text is refused as
    /// "not a valid JSON array". The length of the text is bounded by <see cref="SettingsLimits.MaxTextLength"/>, checked in
    /// <see cref="TryParse(IReadOnlySetting, string, LimitValue, out SettingValue, out SettingProblem?)"/>.
    /// </summary>
    private const int _MaxJsonDepth = 64;

    #endregion

    #region Shared parse tables

    /// <summary>Reader options of the JSON array form.</summary>
    private static readonly JsonDocumentOptions _JsonOptions = new() { MaxDepth = _MaxJsonDepth };

    /// <summary>
    /// Writer options of the compact array form: readable (no <c>'</c> escapes) and without indentation.
    /// Security decision: <c>UnsafeRelaxedJsonEscaping</c> leaves characters such as <c>&lt;</c>, <c>&amp;</c>, and <c>'</c>
    /// unescaped, which is unsafe inside HTML or script. That is acceptable because this text is for the command line, remote
    /// text, and text inputs, and is never embedded in markup by this library; the default encoder was rejected because it
    /// would print <c>'</c> for every apostrophe in a list the user is meant to read and retype. Residual risk: a front end
    /// that places the text into HTML must encode it itself.
    /// </summary>
    private static readonly JsonWriterOptions _WriterOptions = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    #endregion

    #region Parsing a value text

    /// <summary>
    /// Parses and validates <paramref name="text"/> for <paramref name="setting"/> (type, bounds, enumeration) with the default
    /// of <see cref="SettingsLimits.MaxTextLength"/>.
    /// </summary>
    /// <param name="setting">The setting that defines the type and the constraints.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="value">The value on success, otherwise the default <see cref="SettingValue"/>.</param>
    /// <param name="problem">Why the text is refused; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <seealso cref="TryParse(IReadOnlySetting, string, LimitValue, out SettingValue, out SettingProblem?)"/>
    public static bool TryParse(IReadOnlySetting setting, string text, out SettingValue value,
        [NotNullWhen(false)] out SettingProblem? problem) =>
        TryParse(setting, text, SettingsLimits.MaxTextLength.DefaultValue, out value, out problem);

    /// <summary>
    /// Parses and validates <paramref name="text"/> for <paramref name="setting"/> (type, bounds, enumeration); a text longer
    /// than <paramref name="maxTextLength"/> characters is refused before anything is parsed or allocated for it.
    /// </summary>
    /// <param name="setting">The setting that defines the type and the constraints.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="maxTextLength">
    /// A value of <see cref="SettingsLimits.MaxTextLength"/> (a front end passes <see cref="SettingsLimitValues.MaxTextLength"/>);
    /// <see cref="LimitValue.Off"/> accepts a text of any length.
    /// </param>
    /// <param name="value">The value on success, otherwise the default <see cref="SettingValue"/>.</param>
    /// <param name="problem">Why the text is refused; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTextLength"/> is outside the range of its definition.</exception>
    /// <remarks>
    /// The length limit is the one trust-boundary check of this member. Threat: a text of hundreds of megabytes makes an array or
    /// bytes setting allocate memory in proportion (one string per comma element, half the text length for hex bytes).
    /// Mitigation: the check runs first; the default is 1,048,576 characters and the front end chooses the value. Rejected: no
    /// limit with "the caller bounds it" (the first caller to forget is an agent or a remote client). Residual risk: a value
    /// near the maximum or <c>off</c> allocates in proportion to the text; the array nesting depth is bounded separately (64).
    /// </remarks>
    public static bool TryParse(IReadOnlySetting setting, string text, LimitValue maxTextLength, out SettingValue value,
        [NotNullWhen(false)] out SettingProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(text);
        SettingsLimits.MaxTextLength.ThrowIfInvalid(maxTextLength, nameof(maxTextLength));

        // The limit first: nothing below allocates in proportion to the text before this check has passed.
        if (text.Length > maxTextLength.Ceiling)
        {
            value = default;
            problem = new SettingProblem(
                SettingProblemKind.TextTooLong,
                setting.Name,
                SettingsLimits.MaxTextLength.HitMessage(
                    Invariant($"The text has {text.Length} characters"),
                    maxTextLength,
                    SettingsLimits.ChangeHint(SettingsLimits.MaxTextLength)));
            return false;
        }

        // Parse the shape first; the shared validation then checks bounds and enumeration, so the text path and every other
        // path refuse a value with the same words.
        value = default;
        SettingValue parsed;
        problem = setting.Type switch
        {
            SettingType.Bool => _ParseBool(setting, text, out parsed),
            SettingType.String => _Accept(SettingValue.String(text), out parsed),
            SettingType.F64 => _ParseF64(setting, text, out parsed),
            SettingType.U64 => _ParseU64(setting, text, out parsed),
            SettingType.I64 => _ParseI64(setting, text, out parsed),
            SettingType.Limit => _ParseLimit(setting, text, out parsed),
            SettingType.Bytes => _ParseBytes(setting, text, out parsed),
            SettingType.Enum => _ParseEnum(setting, text, out parsed),
            _ => _ParseArray(setting, text, out parsed),
        };
        if (problem is not null)
        {
            return false;
        }

        problem = SettingValidation.Validate(setting, parsed);
        if (problem is not null)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Formats a value in the text form: lower-case Bool, invariant numbers (F64 in the round-trip form), upper-case hex
    /// without prefix for Bytes, the enumeration name, the string as it is, and compact JSON for arrays. The formatted text
    /// parses back to the same value.
    /// </summary>
    /// <param name="setting">The setting the value belongs to (it supplies the canonical enumeration names).</param>
    /// <param name="value">The value to format.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setting"/> is <see langword="null"/>.</exception>
    public static string Format(IReadOnlySetting setting, SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(setting);

        // Scalars first; the cases are short and have no shared work. A value of an unexpected shape formats as empty text.
        switch (value.Type)
        {
            case SettingType.Bool:
                return value.TryGetAsBool(out bool flag) && flag
                    ? "true"
                    : "false";
            case SettingType.String:
                return value.TryGetAsString(out string text)
                    ? text
                    : string.Empty;
            case SettingType.F64:
                return value.TryGetAsF64(out double number)
                    ? SettingValidation.FormatF64(number)
                    : string.Empty;
            case SettingType.U64:
                return value.TryGetAsU64(out ulong unsigned)
                    ? unsigned.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            case SettingType.I64:
                return value.TryGetAsI64(out long signed)
                    ? signed.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            case SettingType.Limit:
                // The text form of a limit needs its definition for the unit: "256 MiB", "90 s", "off".
                return setting.LimitDefinition is { } definition && value.TryGetAsLimit(out LimitValue limit)
                    ? definition.Format(limit)
                    : string.Empty;
            case SettingType.Bytes:
                return value.TryGetAsBytes(out byte[] bytes)
                    ? Convert.ToHexString(bytes)
                    : string.Empty;
            case SettingType.Enum:
                return _FormatEnum(setting, value);
            default:
                return _FormatArray(value);
        }
    }

    #endregion

    #region Scalar Parsers

    /// <summary>Stores a value that needs no parsing and reports success (no problem).</summary>
    /// <param name="value">The value that needs no parsing.</param>
    /// <param name="parsed">Set to <paramref name="value"/>.</param>
    /// <returns>Always <see langword="null"/>: no problem.</returns>
    private static SettingProblem? _Accept(SettingValue value, out SettingValue parsed)
    {
        parsed = value;
        return null;
    }

    /// <summary>Parses <c>true</c> or <c>false</c> in any case.</summary>
    /// <param name="setting">The boolean setting; its name appears in the problem.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="parsed">The boolean value when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem, or <see langword="null"/> when the text is <c>true</c> or <c>false</c>.</returns>
    private static SettingProblem? _ParseBool(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        if (bool.TryParse(text, out bool flag))
        {
            return _Accept(SettingValue.Bool(flag), out parsed);
        }

        parsed = default;
        return new SettingProblem(SettingProblemKind.WrongType, setting.Name, Invariant($"'{SettingValidation.Quote(text)}' is not true or false"));
    }

    /// <summary>Parses a floating-point number in the invariant culture; NaN and infinity are refused.</summary>
    /// <param name="setting">The floating-point setting; its name and bounds appear in the problem.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="parsed">The number when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem, or <see langword="null"/> when the text is a finite number.</returns>
    private static SettingProblem? _ParseF64(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        SettingProblem? problem = _ParseF64Number(setting, text, out double number);
        parsed = problem is null
            ? SettingValue.F64(number)
            : default;
        return problem;
    }

    /// <summary>Parses an unsigned integer; a whole number outside the type range is reported as below or above it.</summary>
    /// <param name="setting">The unsigned setting; its name and bounds appear in the problem.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="parsed">The number when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem, or <see langword="null"/> when the text is a whole number of the type.</returns>
    private static SettingProblem? _ParseU64(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        SettingProblem? problem = _ParseU64Number(setting, text, out ulong number);
        parsed = problem is null
            ? SettingValue.U64(number)
            : default;
        return problem;
    }

    /// <summary>Parses a signed integer; a whole number outside the type range is reported as below or above it.</summary>
    /// <param name="setting">The signed setting; its name and bounds appear in the problem.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="parsed">The number when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem, or <see langword="null"/> when the text is a whole number of the type.</returns>
    private static SettingProblem? _ParseI64(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        SettingProblem? problem = _ParseI64Number(setting, text, out long number);
        parsed = problem is null
            ? SettingValue.I64(number)
            : default;
        return problem;
    }

    /// <summary>
    /// Parses a limit: a whole number with an optional unit word of the unit of its definition, or <c>off</c> when allowed. Only the
    /// shape is checked here; the range is checked by the shared validation afterwards.
    /// </summary>
    /// <param name="setting">The limit setting; its definition parses the text.</param>
    /// <param name="text">The text a user wrote: a number with a unit word, or <c>off</c>.</param>
    /// <param name="parsed">The limit value when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem with the spelling to write instead, or <see langword="null"/> when the text has the right shape.</returns>
    private static SettingProblem? _ParseLimit(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        parsed = default;

        // A limit setting always has a definition (Setting.FromLimit); another IReadOnlySetting implementation might not.
        if (setting.LimitDefinition is not { } definition)
        {
            return new SettingProblem(SettingProblemKind.WrongType, setting.Name, "a limit setting needs a limit definition");
        }

        if (!definition.TryParseShape(text, out LimitValue value, out string? problem))
        {
            return new SettingProblem(SettingProblemKind.WrongType, setting.Name, problem);
        }

        parsed = SettingValue.Limit(value);
        return null;
    }

    /// <summary>Parses a floating-point element.</summary>
    /// <param name="setting">The array setting that owns the element; its name appears in the problem.</param>
    /// <param name="text">The text of one element.</param>
    /// <param name="number">The number when the method returns <see langword="null"/>; otherwise zero.</param>
    /// <returns>The problem, or <see langword="null"/> when the element is a finite number.</returns>
    private static SettingProblem? _ParseF64Number(IReadOnlySetting setting, string text, out double number)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return new SettingProblem(
                SettingProblemKind.WrongType,
                setting.Name,
                Invariant($"'{SettingValidation.Quote(text)}' is not a number; example: {_Sample(setting)}"));
        }

        // .NET parses "NaN", "Infinity", and an overflowing "1e400" successfully; a setting only holds finite numbers.
        if (!double.IsFinite(number))
        {
            return new SettingProblem(SettingProblemKind.NotFinite, setting.Name, Invariant($"'{SettingValidation.Quote(text)}' is not a finite number"));
        }

        return null;
    }

    /// <summary>Parses an unsigned element.</summary>
    /// <param name="setting">The array setting that owns the element; its name appears in the problem.</param>
    /// <param name="text">The text of one element.</param>
    /// <param name="number">The number when the method returns <see langword="null"/>; otherwise zero.</param>
    /// <returns>The problem, or <see langword="null"/> when the element is a whole number of the type.</returns>
    private static SettingProblem? _ParseU64Number(IReadOnlySetting setting, string text, out ulong number)
    {
        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return null;
        }

        return _IntegerProblem(setting, text);
    }

    /// <summary>Parses a signed element.</summary>
    /// <param name="setting">The array setting that owns the element; its name appears in the problem.</param>
    /// <param name="text">The text of one element.</param>
    /// <param name="number">The number when the method returns <see langword="null"/>; otherwise zero.</param>
    /// <returns>The problem, or <see langword="null"/> when the element is a whole number of the type.</returns>
    private static SettingProblem? _ParseI64Number(IReadOnlySetting setting, string text, out long number)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return null;
        }

        return _IntegerProblem(setting, text);
    }

    /// <summary>
    /// Explains why an integer text was refused: a whole number that does not fit is a range problem (no rounding through
    /// double), anything else has the wrong shape.
    /// </summary>
    /// <param name="setting">The integer setting; its name and bounds appear in the problem.</param>
    /// <param name="text">The text that did not parse as an integer of the type.</param>
    /// <returns>The problem: below or above the type range for a whole number, not finite, or the wrong shape.</returns>
    private static SettingProblem _IntegerProblem(IReadOnlySetting setting, string text)
    {
        // The shape is checked digit by digit instead of parsing a big integer: the parse of a very long digit string grows
        // quadratically, and the text may come from outside. The accepted shape is the one of NumberStyles.Integer: optional
        // surrounding white space, an optional sign, and decimal digits.
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim(" \t\n\v\f\r");
        bool negative = trimmed.Length > 0 && trimmed[0] == '-';
        ReadOnlySpan<char> digits = trimmed.Length > 0 && trimmed[0] is '-' or '+'
            ? trimmed[1..]
            : trimmed;
        if (digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return SettingValidation.RangeProblem(setting, negative
                ? SettingProblemKind.BelowMinimum
                : SettingProblemKind.AboveMaximum);
        }

        return new SettingProblem(
            SettingProblemKind.WrongType,
            setting.Name,
            Invariant($"'{SettingValidation.Quote(text)}' is not a whole number; example: {_Sample(setting)}"));
    }

    /// <summary>Parses hex digits with an optional prefix.</summary>
    /// <param name="setting">The bytes setting; its name appears in the problem.</param>
    /// <param name="text">The hex digits, with an optional prefix.</param>
    /// <param name="parsed">The byte array value when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem, or <see langword="null"/> when the text is valid hex.</returns>
    private static SettingProblem? _ParseBytes(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        // Surrounding spaces and the prefix do not belong to the digits; an odd digit count or a non-hex character is refused.
        string digits = text.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        try
        {
            return _Accept(SettingValue.Bytes(Convert.FromHexString(digits)), out parsed);
        }
        catch (FormatException)
        {
            parsed = default;
            return new SettingProblem(
                SettingProblemKind.WrongType,
                setting.Name,
                Invariant($"'{SettingValidation.Quote(text)}' is not hex bytes; example: 0A1B"));
        }
    }

    /// <summary>Parses an enumeration by name (any case) or by an allowed number.</summary>
    /// <param name="setting">The enum setting; its metadata lists the allowed names and numbers.</param>
    /// <param name="text">An allowed name (any case) or an allowed number.</param>
    /// <param name="parsed">The enum value when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem that lists the allowed names, or <see langword="null"/> when the text names an allowed value.</returns>
    private static SettingProblem? _ParseEnum(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        parsed = default;
        EnumSettingMetadata? metadata = setting.EnumMetadata;
        if (metadata is null)
        {
            return new SettingProblem(SettingProblemKind.UnknownEnum, setting.Name, "the setting has no allowed values");
        }

        // The exact text first (names may contain spaces), then the trimmed text, then an allowed number.
        EnumSettingValue? entry = metadata.GetByName(text) ?? metadata.GetByName(text.Trim());
        if (entry is null
            && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong number))
        {
            entry = metadata.GetByNumeric(number);
        }

        if (entry is null)
        {
            return SettingValidation.UnknownEnum(setting);
        }

        return _Accept(SettingValue.Enum(entry.Value.Name, entry.Value.NumericValue), out parsed);
    }

    #endregion

    #region Array Parsers

    /// <summary>Parses a JSON array or a comma list into an array value.</summary>
    /// <param name="setting">The array setting; its element type decides how each element is parsed.</param>
    /// <param name="text">A JSON array, a comma list, or the empty text for an empty array.</param>
    /// <param name="parsed">The array value when the method returns <see langword="null"/>; otherwise the default value.</param>
    /// <returns>The problem with the 1-based position of the first bad element, or <see langword="null"/> when every element parsed.</returns>
    private static SettingProblem? _ParseArray(IReadOnlySetting setting, string text, out SettingValue parsed)
    {
        parsed = default;
        SettingProblem? problem = _SplitElements(setting, text, out List<string> elements);
        if (problem is not null)
        {
            return problem;
        }

        // Each element is parsed with the scalar parser of the element type, so an array and a scalar refuse a bad value with
        // the same words, prefixed by the 1-based element position. The first bad element ends the parse. The bounds are
        // checked afterwards by SettingValidation, per element, in TryParse.
        switch (setting.Type)
        {
            case SettingType.StringArray:
                return _Accept(SettingValue.StringArray([.. elements]), out parsed);
            case SettingType.BoolArray:
                bool[] flags = new bool[elements.Count];
                for (int i = 0; i < flags.Length; i++)
                {
                    if (!bool.TryParse(elements[i], out flags[i]))
                    {
                        return _ElementProblem(i, new SettingProblem(SettingProblemKind.WrongType, setting.Name,
                            Invariant($"'{SettingValidation.Quote(elements[i])}' is not true or false")));
                    }
                }

                return _Accept(SettingValue.BoolArray(flags), out parsed);
            case SettingType.F64Array:
                double[] doubles = new double[elements.Count];
                for (int i = 0; i < doubles.Length; i++)
                {
                    problem = _ParseF64Number(setting, elements[i], out doubles[i]);
                    if (problem is not null)
                    {
                        return _ElementProblem(i, problem);
                    }
                }

                return _Accept(SettingValue.F64Array(doubles), out parsed);
            case SettingType.U64Array:
                ulong[] unsigned = new ulong[elements.Count];
                for (int i = 0; i < unsigned.Length; i++)
                {
                    problem = _ParseU64Number(setting, elements[i], out unsigned[i]);
                    if (problem is not null)
                    {
                        return _ElementProblem(i, problem);
                    }
                }

                return _Accept(SettingValue.U64Array(unsigned), out parsed);
            default:
                long[] signed = new long[elements.Count];
                for (int i = 0; i < signed.Length; i++)
                {
                    problem = _ParseI64Number(setting, elements[i], out signed[i]);
                    if (problem is not null)
                    {
                        return _ElementProblem(i, problem);
                    }
                }

                return _Accept(SettingValue.I64Array(signed), out parsed);
        }
    }

    /// <summary>Splits the text into element texts: empty, a JSON array, or a comma list.</summary>
    /// <param name="setting">The array setting; its name appears in the problem.</param>
    /// <param name="text">The text of the whole array.</param>
    /// <param name="elements">The element texts when the method returns <see langword="null"/>; otherwise an empty list.</param>
    /// <returns>The problem for text that starts like a JSON array but is not one, or <see langword="null"/>.</returns>
    private static SettingProblem? _SplitElements(IReadOnlySetting setting, string text, out List<string> elements)
    {
        elements = [];
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // A comma list is the default form; a leading bracket selects the JSON form (strings may contain commas there).
        if (trimmed[0] != '[')
        {
            foreach (string part in trimmed.Split(','))
            {
                elements.Add(part.Trim());
            }

            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed, _JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return _NotJsonArray(setting, text);
            }

            int index = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                index++;
                if (setting.Type == SettingType.StringArray && element.ValueKind != JsonValueKind.String)
                {
                    return _ElementProblem(index - 1, new SettingProblem(SettingProblemKind.WrongType, setting.Name,
                        Invariant($"{SettingValidation.Quote(element.GetRawText())} is not a JSON string")));
                }

                // A string element of a numeric array is parsed like the text (persisted files do the same for numbers).
                elements.Add(element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText());
            }

            return null;
        }
        catch (JsonException)
        {
            return _NotJsonArray(setting, text);
        }
    }

    /// <summary>Reports text that starts like a JSON array but is not one.</summary>
    /// <param name="setting">The array setting; its name appears in the problem.</param>
    /// <param name="text">The text that is not a valid JSON array; quoted shortened.</param>
    /// <returns>The problem of the kind <see cref="SettingProblemKind.WrongType"/>.</returns>
    private static SettingProblem _NotJsonArray(IReadOnlySetting setting, string text) =>
        new(SettingProblemKind.WrongType, setting.Name, Invariant($"'{SettingValidation.Quote(text)}' is not a valid JSON array"));

    /// <summary>Prefixes an element problem with the 1-based position of the element.</summary>
    /// <param name="zeroBasedIndex">The index of the element in the array, counted from zero.</param>
    /// <param name="problem">The problem of that element.</param>
    /// <returns>The same problem with <c>element N:</c> in front of its message, N counted from one.</returns>
    private static SettingProblem _ElementProblem(int zeroBasedIndex, SettingProblem problem) =>
        problem with { Message = string.Create(CultureInfo.InvariantCulture, $"element {zeroBasedIndex + 1}: {problem.Message}") };

    #endregion

    #region Formatting

    /// <summary>Formats the canonical enumeration name of a value.</summary>
    /// <param name="setting">The enum setting; its metadata holds the canonical names.</param>
    /// <param name="value">The enum value to format.</param>
    /// <returns>
    /// The canonical name of the metadata; the name stored in <paramref name="value"/> when the metadata does not know its number;
    /// an empty string when <paramref name="value"/> is not an enum value.
    /// </returns>
    private static string _FormatEnum(IReadOnlySetting setting, SettingValue value)
    {
        if (!value.TryGetAsEnum(out (string Name, ulong Value) entry))
        {
            return string.Empty;
        }

        // The canonical name comes from the metadata, so a value created with another spelling prints the registered one.
        EnumSettingValue? canonical = setting.EnumMetadata?.GetByNumeric(entry.Value);
        return canonical is null
            ? entry.Name
            : canonical.Value.Name;
    }

    /// <summary>Formats an array value as compact JSON.</summary>
    /// <param name="value">The array value to format.</param>
    /// <returns>The compact JSON array text of the elements.</returns>
    private static string _FormatArray(SettingValue value)
    {
        // Not a hot path (display and command line): a growable buffer plus one string per call is fine. The writer is disposed,
        // which flushes it, before the buffer is read below; reading earlier would miss buffered bytes. An unexpected value type
        // yields "[]".
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, _WriterOptions))
        {
            writer.WriteStartArray();
            switch (value.Type)
            {
                case SettingType.BoolArray when value.TryGetAsBoolArray(out bool[] flags):
                    foreach (bool flag in flags)
                    {
                        writer.WriteBooleanValue(flag);
                    }

                    break;
                case SettingType.StringArray when value.TryGetAsStringArray(out string[] strings):
                    foreach (string item in strings)
                    {
                        writer.WriteStringValue(item);
                    }

                    break;
                case SettingType.F64Array when value.TryGetAsF64Array(out double[] doubles):
                    foreach (double number in doubles)
                    {
                        writer.WriteNumberValue(number);
                    }

                    break;
                case SettingType.U64Array when value.TryGetAsU64Array(out ulong[] unsigned):
                    foreach (ulong number in unsigned)
                    {
                        writer.WriteNumberValue(number);
                    }

                    break;
                case SettingType.I64Array when value.TryGetAsI64Array(out long[] signed):
                    foreach (long number in signed)
                    {
                        writer.WriteNumberValue(number);
                    }

                    break;
                default:
                    break;
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// An example text for messages: the formatted default (the first element for arrays) when there is one, otherwise a
    /// sample of the type.
    /// </summary>
    /// <param name="setting">The setting whose type and bounds decide the sample.</param>
    /// <returns>
    /// A short example text for refusal messages: the default (the first element for an array) when the setting has a usable
    /// one, otherwise a fixed sample of the type.
    /// </returns>
    private static string _Sample(IReadOnlySetting setting)
    {
        string formatted = setting.DefaultValue.Type switch
        {
            SettingType.U64Array when setting.DefaultValue.TryGetAsU64Array(out ulong[] unsigned) && unsigned.Length > 0 =>
                unsigned[0].ToString(CultureInfo.InvariantCulture),
            SettingType.I64Array when setting.DefaultValue.TryGetAsI64Array(out long[] signed) && signed.Length > 0 =>
                signed[0].ToString(CultureInfo.InvariantCulture),
            SettingType.F64Array when setting.DefaultValue.TryGetAsF64Array(out double[] doubles) && doubles.Length > 0 =>
                SettingValidation.FormatF64(doubles[0]),
            SettingType.U64 or SettingType.I64 or SettingType.F64 or SettingType.Bytes => Format(setting, setting.DefaultValue),
            _ => string.Empty,
        };
        if (formatted.Length > 0)
        {
            return formatted;
        }

        // Types without a usable default: the sample of the (element) type.
        return setting.Type switch
        {
            SettingType.F64 or SettingType.F64Array => "1.5",
            SettingType.Bytes => "0A1B",
            SettingType.Bool or SettingType.BoolArray => "true",
            _ => "1",
        };
    }

    #endregion
}
