// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// The JSON form of setting values: the form the settings files use and the form MCP uses. Bool, String, F64, U64, and I64 are
/// the JSON values of the same name (U64 and I64 also accept a numeric string); Bytes is a base64 string; Enum is an object
/// with <c>name</c> and <c>value</c> (reading also accepts a bare name or an allowed number); arrays are JSON arrays of the
/// element form. Stateless; thread-safe; never throws for any JSON element.
/// </summary>
public static class SettingValueJson
{
    #region Public API

    /// <summary>Reads and validates a JSON value for <paramref name="setting"/>; never throws for any element.</summary>
    /// <param name="setting">The setting that defines the type and the constraints.</param>
    /// <param name="element">The JSON value.</param>
    /// <param name="value">The value on success, otherwise the default <see cref="SettingValue"/>.</param>
    /// <param name="problem">Why the element is refused; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setting"/> is <see langword="null"/>.</exception>
    public static bool TryRead(IReadOnlySetting setting, JsonElement element, out SettingValue value,
        [NotNullWhen(false)] out SettingProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(setting);
        value = default;

        // Work on the node form the persistence code understands; an undefined element has no raw text at all.
        JsonNode? node;
        try
        {
            node = element.ValueKind == JsonValueKind.Undefined
                ? null
                : JsonNode.Parse(element.GetRawText());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            node = null;
        }

        if (node is null)
        {
            problem = new SettingProblem(SettingProblemKind.WrongType, setting.Name, $"expected {setting.Type}, got JSON null");
            return false;
        }

        // An enumeration may also be given as a bare name or an allowed number, as in the text form.
        if (setting.Type == SettingType.Enum && node is JsonValue enumValue && setting.EnumMetadata is { } metadata)
        {
            EnumSettingValue? entry = null;
            if (enumValue.TryGetValue(out string? name))
            {
                entry = metadata.GetByName(name);
            }
            else if (enumValue.TryGetValue(out ulong number))
            {
                entry = metadata.GetByNumeric(number);
            }

            if (entry is null)
            {
                problem = SettingValidation.UnknownEnum(setting);
                return false;
            }

            value = SettingValue.Enum(entry.Value.Name, entry.Value.NumericValue);
            problem = null;
            return true;
        }

        (SettingValue? parsed, SettingLoadResult result, _) =
            FromNode(node, setting.Type, setting.EnumMetadata, setting.GroupName, setting.Name);
        if (parsed is null)
        {
            problem = new SettingProblem(SettingProblemKind.WrongType, setting.Name, result == SettingLoadResult.DeserializationError
                ? $"the JSON {_KindOf(element)} cannot be decoded as {setting.Type}"
                : $"expected {setting.Type}, got JSON {_KindOf(element)}");
            return false;
        }

        problem = SettingValidation.Validate(setting, parsed.Value);
        if (problem is not null)
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    /// <summary>Writes <paramref name="value"/> in the persistence form (Bytes base64, enum object, F64 round-trip).</summary>
    /// <param name="writer">Receives one JSON value.</param>
    /// <param name="setting">The setting the value belongs to (reserved for forms that depend on the setting).</param>
    /// <param name="value">The value to write.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An F64 value is not finite (a broken invariant).</exception>
    public static void Write(Utf8JsonWriter writer, IReadOnlySetting setting, SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(setting);
        ToNode(value)?.WriteTo(writer);
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Converts a JSON node to a setting value based on the setting type; the persistence reader uses it and turns the result
    /// into load warnings. Returns a tuple of the parsed value (or null) and a <see cref="SettingLoadResult"/> discriminant.
    /// </summary>
    internal static (SettingValue? Value, SettingLoadResult Result, SettingsLoadWarning? Warning) FromNode(
        JsonNode node, SettingType type, EnumSettingMetadata? enumMetadata, string groupName, string settingName) =>
        _JsonToSettingValue(node, type, enumMetadata, groupName, settingName);

    /// <summary>Converts a setting value to its JSON node (the persistence form).</summary>
    internal static JsonNode? ToNode(SettingValue value) => _SettingValueToJson(value);

    #endregion

    #region Private Helpers

    /// <summary>Names the JSON kind of an element for messages.</summary>
    private static string _KindOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };

    #endregion

    #region JSON Conversion

    /// <summary>
    /// Converts a JSON node to a setting value based on the setting type.
    /// Returns a tuple of the parsed value (or null) and a <see cref="SettingLoadResult"/> discriminant.
    /// <list type="bullet">
    ///   <item><see cref="SettingLoadResult.Success"/> — value parsed successfully.</item>
    ///   <item><see cref="SettingLoadResult.TypeMismatch"/> — JSON node's structural type does not match the setting type.</item>
    ///   <item><see cref="SettingLoadResult.DeserializationError"/> — JSON node type matches but content cannot be decoded (e.g. invalid base64).</item>
    /// </list>
    /// </summary>
    private static (SettingValue? Value, SettingLoadResult Result, SettingsLoadWarning? Warning) _JsonToSettingValue(
        JsonNode node, SettingType type, EnumSettingMetadata? enumMetadata, string groupName, string settingName)
    {
        try
        {
            if (type == SettingType.Bytes)
            {
                // Bytes parsing is handled separately because the FormatException from
                // invalid base64 is a DeserializationError, not a TypeMismatch.
                (SettingValue? bytesValue, SettingLoadResult bytesResult) = _TryParseBytesWithResult(node);
                return (bytesValue, bytesResult, null);
            }

            if (type == SettingType.Enum)
            {
                return _TryParseEnumWithMetadata(node, enumMetadata, groupName, settingName);
            }

            if (type is SettingType.BoolArray or SettingType.StringArray or SettingType.F64Array
                or SettingType.U64Array or SettingType.I64Array)
            {
                SettingValue? arrayValue = type switch
                {
                    SettingType.BoolArray => _TryParseBoolArray(node),
                    SettingType.StringArray => _TryParseStringArray(node),
                    SettingType.F64Array => _TryParseF64Array(node),
                    SettingType.U64Array => _TryParseU64Array(node),
                    SettingType.I64Array => _TryParseI64Array(node),
                    _ => null,
                };
                return (arrayValue, arrayValue is not null ? SettingLoadResult.Success : SettingLoadResult.TypeMismatch, null);
            }

            SettingValue? value = type switch
            {
                SettingType.Bool => (SettingValue?)SettingValue.Bool(node.GetValue<bool>()),
                SettingType.String => node.GetValue<string>() is { } text
                    ? (SettingValue?)SettingValue.String(text)
                    : null,
                SettingType.F64 => _TryParseFiniteF64(node),
                SettingType.U64 => _TryParseU64(node),
                SettingType.I64 => _TryParseI64(node),
                _ => null,
            };
            return (value, value is not null ? SettingLoadResult.Success : SettingLoadResult.TypeMismatch, null);
        }
        catch (InvalidOperationException)
        {
            // JSON node structural type doesn't match expected setting type (e.g., got array for bool).
            return (null, SettingLoadResult.TypeMismatch, null);
        }
        catch (FormatException)
        {
            // JSON value cannot be converted to the target number/string format.
            return (null, SettingLoadResult.DeserializationError, null);
        }
        catch (ArgumentException)
        {
            // An enum object repeats a property name: malformed content, not an exception for Load or registration.
            return (null, SettingLoadResult.DeserializationError, null);
        }
    }

    /// <summary>Tries to parse a finite <see cref="SettingType.F64"/> from a JSON node. Returns null for NaN or Infinity.</summary>
    private static SettingValue? _TryParseFiniteF64(JsonNode node)
    {
        if (node is JsonValue val && val.TryGetValue(out double d) && double.IsFinite(d))
        {
            return SettingValue.F64(d);
        }
        return null;
    }

    /// <summary>Tries to parse a <see cref="SettingType.U64"/> from a JSON node.</summary>
    private static SettingValue? _TryParseU64(JsonNode node)
    {
        // Try direct numeric first, then string parsing
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out ulong u))
            {
                return SettingValue.U64(u);
            }
            if (val.TryGetValue(out long l) && l >= 0)
            {
                return SettingValue.U64((ulong)l);
            }
            if (val.TryGetValue(out string? s)
                && ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
            {
                return SettingValue.U64(parsed);
            }
        }
        return null;
    }

    /// <summary>Tries to parse a <see cref="SettingType.I64"/> from a JSON node.</summary>
    private static SettingValue? _TryParseI64(JsonNode node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out long l))
            {
                return SettingValue.I64(l);
            }
            if (val.TryGetValue(out string? s)
                && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return SettingValue.I64(parsed);
            }
        }
        return null;
    }

    /// <summary>Tries to parse a <see cref="SettingType.BoolArray"/> from a JSON array of booleans.</summary>
    private static SettingValue? _TryParseBoolArray(JsonNode node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        bool[] values = new bool[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? el = arr[i];
            if (el is null)
            {
                return null;
            }

            values[i] = el.GetValue<bool>();
        }

        return SettingValue.BoolArray(values);
    }

    /// <summary>Tries to parse a <see cref="SettingType.StringArray"/> from a JSON array of strings.</summary>
    private static SettingValue? _TryParseStringArray(JsonNode node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        string[] values = new string[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? el = arr[i];
            if (el is not JsonValue val || !val.TryGetValue(out string? s) || s is null)
            {
                return null;
            }

            values[i] = s;
        }

        return SettingValue.StringArray(values);
    }

    /// <summary>Tries to parse a <see cref="SettingType.F64Array"/> from a JSON array of finite numbers.</summary>
    private static SettingValue? _TryParseF64Array(JsonNode node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        double[] values = new double[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? el = arr[i];
            if (el is null)
            {
                return null;
            }

            SettingValue? parsed = _TryParseFiniteF64(el);
            if (parsed is null || !parsed.Value.TryGetAsF64(out double d))
            {
                return null;
            }

            values[i] = d;
        }

        return SettingValue.F64Array(values);
    }

    /// <summary>Tries to parse a <see cref="SettingType.U64Array"/> from a JSON array of unsigned integers.</summary>
    private static SettingValue? _TryParseU64Array(JsonNode node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        ulong[] values = new ulong[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? el = arr[i];
            if (el is null)
            {
                return null;
            }

            SettingValue? parsed = _TryParseU64(el);
            if (parsed is null || !parsed.Value.TryGetAsU64(out ulong u))
            {
                return null;
            }

            values[i] = u;
        }

        return SettingValue.U64Array(values);
    }

    /// <summary>Tries to parse a <see cref="SettingType.I64Array"/> from a JSON array of signed integers.</summary>
    private static SettingValue? _TryParseI64Array(JsonNode node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        long[] values = new long[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            JsonNode? el = arr[i];
            if (el is null)
            {
                return null;
            }

            SettingValue? parsed = _TryParseI64(el);
            if (parsed is null || !parsed.Value.TryGetAsI64(out long l))
            {
                return null;
            }

            values[i] = l;
        }

        return SettingValue.I64Array(values);
    }

    /// <summary>Tries to parse a <see cref="SettingType.Bytes"/> from a base64-encoded JSON string.
    /// Distinguishes <see cref="SettingLoadResult.TypeMismatch"/> (node is not a JSON string)
    /// from <see cref="SettingLoadResult.DeserializationError"/> (string is not valid base64).</summary>
    private static (SettingValue? Value, SettingLoadResult Result) _TryParseBytesWithResult(JsonNode node)
    {
        if (node is not JsonValue val || !val.TryGetValue(out string? s))
        {
            // Node is not a JSON string — structurally wrong type.
            return (null, SettingLoadResult.TypeMismatch);
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(s);
            return (SettingValue.Bytes(bytes), SettingLoadResult.Success);
        }
        catch (FormatException)
        {
            // String is not valid base64 — content error, correct structural type.
            return (null, SettingLoadResult.DeserializationError);
        }
    }

    /// <summary>
    /// Tries to parse a <see cref="SettingType.Enum"/> from a JSON object with name/value properties.
    /// After numeric validation, canonicalizes the name via <paramref name="enumMetadata"/>.
    /// </summary>
    private static (SettingValue? Value, SettingLoadResult Result, SettingsLoadWarning? Warning) _TryParseEnumWithMetadata(
        JsonNode node, EnumSettingMetadata? enumMetadata, string groupName, string settingName)
    {
        // Expect object with "name" and "value" properties
        if (node is not JsonObject obj)
        {
            return (null, SettingLoadResult.TypeMismatch, null);
        }
        if (obj["name"] is not JsonValue nameVal)
        {
            return (null, SettingLoadResult.TypeMismatch, null);
        }
        if (obj["value"] is not JsonValue valueVal)
        {
            return (null, SettingLoadResult.TypeMismatch, null);
        }

        if (!nameVal.TryGetValue(out string? persistedName))
        {
            return (null, SettingLoadResult.TypeMismatch, null);
        }

        ulong? numericValue = _TryGetEnumNumericValue(valueVal);
        if (numericValue is null)
        {
            return (null, SettingLoadResult.TypeMismatch, null);
        }

        if (enumMetadata is null)
        {
            return (SettingValue.Enum(persistedName, numericValue.Value), SettingLoadResult.Success, null);
        }

        EnumSettingValue? canonical = enumMetadata.GetByNumeric(numericValue.Value);
        if (canonical is null)
        {
            return (null, SettingLoadResult.TypeMismatch, null);
        }

        string canonicalName = canonical.Value.Name;
        SettingValue value = SettingValue.Enum(canonicalName, numericValue.Value);
        SettingsLoadWarning? warning = null;
        if (!string.Equals(persistedName, canonicalName, StringComparison.OrdinalIgnoreCase))
        {
            warning = new SettingsLoadWarning(
                SettingsLoadWarningKind.EnumNameMismatch,
                groupName,
                settingName,
                $"Persisted enum name '{persistedName}' for '{settingName}' does not match the canonical name " +
                $"'{canonicalName}' for numeric value {numericValue.Value}. The canonical name is applied.");
        }

        return (value, SettingLoadResult.Success, warning);
    }

    /// <summary>Extracts the numeric component from an enum JSON value node.</summary>
    private static ulong? _TryGetEnumNumericValue(JsonValue valueVal)
    {
        if (valueVal.TryGetValue(out ulong numVal))
        {
            return numVal;
        }

        if (valueVal.TryGetValue(out long longVal) && longVal >= 0)
        {
            return (ulong)longVal;
        }

        if (valueVal.TryGetValue(out string? strVal)
            && ulong.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>Converts a setting value to a JSON node.</summary>
    private static JsonNode? _SettingValueToJson(SettingValue value)
    {
        switch (value.Type)
        {
            case SettingType.Bool:
                value.TryGetAsBool(out bool boolVal);
                return JsonValue.Create(boolVal);
            case SettingType.String:
                value.TryGetAsString(out string strVal);
                return JsonValue.Create(strVal);
            case SettingType.F64:
                return _SettingF64ToJson(value);
            case SettingType.U64:
                value.TryGetAsU64(out ulong u64Val);
                return JsonValue.Create(u64Val);
            case SettingType.I64:
                value.TryGetAsI64(out long i64Val);
                return JsonValue.Create(i64Val);
            case SettingType.Bytes:
                value.TryGetAsBytes(out byte[] bytesVal);
                return JsonValue.Create(Convert.ToBase64String(bytesVal));
            case SettingType.Enum:
                return _CreateEnumJson(value);
            case SettingType.BoolArray:
                return _BoolArrayToJson(value);
            case SettingType.StringArray:
                return _StringArrayToJson(value);
            case SettingType.F64Array:
                return _F64ArrayToJson(value);
            case SettingType.U64Array:
                return _U64ArrayToJson(value);
            case SettingType.I64Array:
                return _I64ArrayToJson(value);
            default:
                return null;
        }
    }

    /// <summary>Converts an F64 setting to a JSON value. F64 settings are always finite (enforced at registration and mutation).</summary>
    private static JsonValue _SettingF64ToJson(SettingValue value)
    {
        value.TryGetAsF64(out double f64);
        // F64 settings are guaranteed finite by Setting.F64() and Setting.ValidateF64().
        // Guard against invariant violations that would produce invalid JSON.
        if (!double.IsFinite(f64))
        {
            ThrowHelpers.ThrowNonFiniteF64(f64);
        }
        return JsonValue.Create(f64);
    }

    /// <summary>Converts a boolean array value to a JSON array of booleans.</summary>
    private static JsonArray _BoolArrayToJson(SettingValue value)
    {
        value.TryGetAsBoolArray(out bool[] items);
        JsonArray json = [];
        for (int i = 0; i < items.Length; i++)
        {
            json.Add(JsonValue.Create(items[i]));
        }

        return json;
    }

    /// <summary>Converts a string array value to a JSON array of strings.</summary>
    private static JsonArray _StringArrayToJson(SettingValue value)
    {
        value.TryGetAsStringArray(out string[] items);
        JsonArray json = [];
        for (int i = 0; i < items.Length; i++)
        {
            json.Add(JsonValue.Create(items[i]));
        }

        return json;
    }

    /// <summary>Converts a floating-point array value to a JSON array of numbers; a non-finite item is refused.</summary>
    private static JsonArray _F64ArrayToJson(SettingValue value)
    {
        value.TryGetAsF64Array(out double[] items);
        JsonArray json = [];
        for (int i = 0; i < items.Length; i++)
        {
            double d = items[i];
            if (!double.IsFinite(d))
            {
                ThrowHelpers.ThrowNonFiniteF64(d);
            }

            json.Add(JsonValue.Create(d));
        }

        return json;
    }

    /// <summary>Converts an unsigned integer array value to a JSON array of numbers.</summary>
    private static JsonArray _U64ArrayToJson(SettingValue value)
    {
        value.TryGetAsU64Array(out ulong[] items);
        JsonArray json = [];
        for (int i = 0; i < items.Length; i++)
        {
            json.Add(JsonValue.Create(items[i]));
        }

        return json;
    }

    /// <summary>Converts a signed integer array value to a JSON array of numbers.</summary>
    private static JsonArray _I64ArrayToJson(SettingValue value)
    {
        value.TryGetAsI64Array(out long[] items);
        JsonArray json = [];
        for (int i = 0; i < items.Length; i++)
        {
            json.Add(JsonValue.Create(items[i]));
        }

        return json;
    }

    /// <summary>Creates a JSON object for an enum setting value.</summary>
    private static JsonObject _CreateEnumJson(SettingValue value)
    {
        value.TryGetAsEnum(out (string name, ulong numericValue) e);
        return new JsonObject
        {
            ["name"] = JsonValue.Create(e.name),
            ["value"] = JsonValue.Create(e.numericValue),
        };
    }

    #endregion
}
