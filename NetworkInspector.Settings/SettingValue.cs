// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Represents a setting value that can be one of several types.
/// Uses a tagged union layout to avoid boxing for value types.
///
/// NaN-safe f64 comparison is implemented to prevent infinite dirty-tracking loops.
/// Two <c>F64(NaN)</c> values are considered equal.
/// <para>
/// Immutable and therefore thread-safe. <c>default(SettingValue)</c> is <c>Bool(false)</c> (the type discriminant 0 is
/// <see cref="SettingType.Bool"/>). The formatting members (<see cref="ToString()"/>, <c>TryFormat</c>) produce short display
/// labels, not the persistence or command-line forms; those live in <see cref="SettingValueJson"/> and
/// <see cref="SettingValueText"/>.
/// </para>
/// </summary>
public readonly struct SettingValue : IEquatable<SettingValue>, ISpanFormattable, IUtf8SpanFormattable
{
    // Storage layout (a tagged union: one 8-byte payload word, one reference, one discriminant, so no value is boxed and the
    // scalar types need no allocation):
    // Bool:   _Bits = 0 or 1, _ReferenceValue = null
    // F64:    _Bits = BitConverter bits, _ReferenceValue = null
    // U64:    _Bits = (long)value, _ReferenceValue = null   (bit reinterpretation; read back with (ulong)_Bits)
    // I64:    _Bits = value, _ReferenceValue = null
    // Limit:  _Bits = value in the base unit of the limit, or -1 for off, _ReferenceValue = null (see LimitValue)
    // String: _Bits = 0, _ReferenceValue = string
    // Bytes:  _Bits = 0, _ReferenceValue = byte[]
    // Enum:   _Bits = numeric value, _ReferenceValue = string (name)
    // BoolArray/StringArray/F64Array/U64Array/I64Array: _Bits = 0, _ReferenceValue = T[] (privately owned copy)
    //
    // Arrays are copied on the way in (factories) and on the way out (TryGetAs*), by design: copying a struct copies only the
    // reference, so every copy of a SettingValue (the current and the pending value of a setting, a registration result, a
    // view) shares one array. A caller that could mutate it would change all holders at once and break equality, hashing, and
    // the immutable snapshot a Setting publishes to lock-free readers. The price is one allocation per array read; a caller
    // that needs the array repeatedly should keep the copy it received.

    /// <summary>Returns the type discriminant for this value.</summary>
    public SettingType Type { get; }

    /// <summary>
    /// The inline payload: a boolean as 0 or 1, an integer, an enum number, or the bits of a double as the same 64 bits, a limit as
    /// its encoding (-1 for off, otherwise the number). Unused (0) for the types that keep a reference.
    /// </summary>
    private readonly long _Bits;

    /// <summary>
    /// The payload of the types that need an object: a string, a byte array, an array of the element type, or the name of an enum
    /// value. <see langword="null"/> for the types that fit in <see cref="_Bits"/>.
    /// </summary>
    private readonly object? _ReferenceValue;

    /// <summary>Assembles a value from its three storage parts; the factories guarantee that they match <paramref name="type"/>.</summary>
    /// <param name="type">The type discriminant.</param>
    /// <param name="bits">The inline payload; see <see cref="_Bits"/>.</param>
    /// <param name="referenceValue">The reference payload, or <see langword="null"/>; see <see cref="_ReferenceValue"/>.</param>
    private SettingValue(SettingType type, long bits, object? referenceValue)
    {
        Type = type;
        _Bits = bits;
        _ReferenceValue = referenceValue;
    }

    #region Creating values

    /// <summary>Creates a boolean setting value.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue Bool(bool value) =>
        new(SettingType.Bool, value
            ? 1L
            : 0L, null);

    /// <summary>Creates a string setting value.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <param name="value">The text; not <see langword="null"/>, kept as given.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.String, 0L, value);
    }

    /// <summary>Creates a 64-bit floating point setting value.</summary>
    /// <param name="value">The number. Any double is accepted here; a setting refuses NaN and infinity when it validates.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue F64(double value) =>
        new(SettingType.F64, BitConverter.DoubleToInt64Bits(value), null);

    /// <summary>Creates a 64-bit unsigned integer setting value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue U64(ulong value) =>
        new(SettingType.U64, (long)value, null);

    /// <summary>Creates a 64-bit signed integer setting value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue I64(long value) =>
        new(SettingType.I64, value, null);

    /// <summary>
    /// Creates a limit setting value. Off is stored as -1 in the payload; a <see cref="LimitValue"/> is never negative otherwise,
    /// so the payload needs no extra field and equality works on the bits.
    /// </summary>
    /// <param name="value">The limit value, a number in the base unit of its definition or off.</param>
    /// <returns>The setting value of type <see cref="SettingType.Limit"/>.</returns>
    public static SettingValue Limit(LimitValue value) =>
        new(SettingType.Limit, value.IsOff
            ? -1L
            : value.Value, null);

    /// <summary>Creates a byte array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <param name="value">The bytes; not <see langword="null"/>, copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue Bytes(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Defensive copy: isolate internal state from external mutations of the source array.
        return new(SettingType.Bytes, 0L, value.ToArray());
    }

    /// <summary>Creates a byte array setting value from a ReadOnlyMemory.</summary>
    /// <param name="value">The bytes; copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue Bytes(ReadOnlyMemory<byte> value) =>
        new(SettingType.Bytes, 0L, value.ToArray());

    /// <summary>Creates an enum setting value.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    /// <param name="name">The name of the enumeration value; not <see langword="null"/>.</param>
    /// <param name="numericValue">The number of the enumeration value.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue Enum(string name, ulong numericValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(SettingType.Enum, (long)numericValue, name);
    }

    /// <summary>Creates a boolean array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <param name="value">The elements; not <see langword="null"/>, copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue BoolArray(bool[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.BoolArray, 0L, value.ToArray());
    }

    /// <summary>
    /// Creates a string array setting value. The array is defensively copied to prevent external mutation.
    /// Null elements are rejected because scalar <see cref="String"/> values cannot be null.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="value"/> is null or any element is null.
    /// </exception>
    /// <param name="value">The elements; not <see langword="null"/>, no <see langword="null"/> element, copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue StringArray(string[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (int i = 0; i < value.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(value[i]);
        }

        return new(SettingType.StringArray, 0L, value.ToArray());
    }

    /// <summary>Creates an F64 array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <param name="value">The elements; not <see langword="null"/>, copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue F64Array(double[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.F64Array, 0L, value.ToArray());
    }

    /// <summary>Creates a U64 array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <param name="value">The elements; not <see langword="null"/>, copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue U64Array(ulong[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.U64Array, 0L, value.ToArray());
    }

    /// <summary>Creates an I64 array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <param name="value">The elements; not <see langword="null"/>, copied.</param>
    /// <returns>The value of the type named by the method.</returns>
    public static SettingValue I64Array(long[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.I64Array, 0L, value.ToArray());
    }

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.Bool"/> and
    /// writes the boolean into <paramref name="value"/>; otherwise sets <paramref name="value"/>
    /// to <see langword="false"/> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsBool(out bool value)
    {
        if (Type == SettingType.Bool)
        {
            value = _Bits != 0;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.String"/> and
    /// writes the string into <paramref name="value"/>; otherwise sets <paramref name="value"/>
    /// to <see cref="string.Empty"/> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsString(out string value)
    {
        if (Type == SettingType.String && _ReferenceValue is string s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.F64"/> and
    /// writes the double into <paramref name="value"/>; otherwise sets <paramref name="value"/>
    /// to <c>0.0</c> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsF64(out double value)
    {
        if (Type == SettingType.F64)
        {
            value = BitConverter.Int64BitsToDouble(_Bits);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.U64"/> and
    /// writes the ulong into <paramref name="value"/>; otherwise sets <paramref name="value"/>
    /// to <c>0</c> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsU64(out ulong value)
    {
        if (Type == SettingType.U64)
        {
            value = (ulong)_Bits;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.I64"/> and
    /// writes the long into <paramref name="value"/>; otherwise sets <paramref name="value"/>
    /// to <c>0</c> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsI64(out long value)
    {
        if (Type == SettingType.I64)
        {
            value = _Bits;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.Limit"/> and writes the limit into
    /// <paramref name="value"/>; otherwise sets <paramref name="value"/> to <c>default</c> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The limit value when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for a limit value.</returns>
    public bool TryGetAsLimit(out LimitValue value)
    {
        if (Type == SettingType.Limit)
        {
            // Invariant: the payload is -1 (off) or a non-negative number; Limit(LimitValue) is the only way to build the type.
            value = _Bits < 0
                ? LimitValue.Off
                : LimitValue.Of(_Bits);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.Bytes"/> and
    /// writes a <em>defensive copy</em> of the byte array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsBytes(out byte[] value)
    {
        if (Type == SettingType.Bytes && _ReferenceValue is byte[] stored)
        {
            // Defensive copy: callers must not be able to mutate internal state.
            value = stored.ToArray();
            return true;
        }
        value = [];
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.Enum"/> and
    /// writes the enum name and numeric value into <paramref name="value"/>; otherwise sets
    /// <paramref name="value"/> to <c>default</c> and returns <see langword="false"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsEnum(out (string Name, ulong Value) value)
    {
        if (Type == SettingType.Enum && _ReferenceValue is string name)
        {
            value = (name, (ulong)_Bits);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.BoolArray"/> and
    /// writes a <em>defensive copy</em> of the boolean array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsBoolArray(out bool[] value)
    {
        if (Type == SettingType.BoolArray && _ReferenceValue is bool[] stored)
        {
            value = stored.ToArray();
            return true;
        }
        value = [];
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.StringArray"/> and
    /// writes a <em>defensive copy</em> of the string array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsStringArray(out string[] value)
    {
        if (Type == SettingType.StringArray && _ReferenceValue is string[] stored)
        {
            value = stored.ToArray();
            return true;
        }
        value = [];
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.F64Array"/> and
    /// writes a <em>defensive copy</em> of the double array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsF64Array(out double[] value)
    {
        if (Type == SettingType.F64Array && _ReferenceValue is double[] stored)
        {
            value = stored.ToArray();
            return true;
        }
        value = [];
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.U64Array"/> and
    /// writes a <em>defensive copy</em> of the ulong array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsU64Array(out ulong[] value)
    {
        if (Type == SettingType.U64Array && _ReferenceValue is ulong[] stored)
        {
            value = stored.ToArray();
            return true;
        }
        value = [];
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.I64Array"/> and
    /// writes a <em>defensive copy</em> of the long array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsI64Array(out long[] value)
    {
        if (Type == SettingType.I64Array && _ReferenceValue is long[] stored)
        {
            value = stored.ToArray();
            return true;
        }
        value = [];
        return false;
    }

    #endregion

    #region Equality

    // Equality is by content, not by reference, and never throws. F64 (scalar and array) compares the IEEE bit patterns:
    // that makes NaN equal to NaN (a value-compare would make a NaN differ from itself, so dirty-tracking never settles) at the price that 0.0
    // and -0.0 are different and NaNs with different payloads are different. GetHashCode uses the same bits so that equal
    // values hash equally. String comparisons are ordinal. Hash codes are only for in-process use: string hashing is
    // randomized per process, so they must not be persisted.

    /// <inheritdoc/>
    public bool Equals(SettingValue other)
    {
        if (Type != other.Type)
        {
            return false;
        }

        return Type switch
        {
            SettingType.Bool => _Bits == other._Bits,
            // NaN-safe: compare bit representations so NaN == NaN
            SettingType.F64 => _Bits == other._Bits,
            SettingType.U64 => _Bits == other._Bits,
            SettingType.I64 => _Bits == other._Bits,
            SettingType.Limit => _Bits == other._Bits,
            SettingType.String => string.Equals(
                (string?)_ReferenceValue, (string?)other._ReferenceValue, StringComparison.Ordinal),
            SettingType.Bytes => _BytesEqual((byte[]?)_ReferenceValue, (byte[]?)other._ReferenceValue),
            SettingType.Enum => _Bits == other._Bits &&
                string.Equals((string?)_ReferenceValue, (string?)other._ReferenceValue, StringComparison.Ordinal),
            SettingType.BoolArray => _ArrayEqual((bool[]?)_ReferenceValue, (bool[]?)other._ReferenceValue),
            SettingType.StringArray => _StringArrayEqual((string[]?)_ReferenceValue, (string[]?)other._ReferenceValue),
            SettingType.F64Array => _F64ArrayEqual((double[]?)_ReferenceValue, (double[]?)other._ReferenceValue),
            SettingType.U64Array => _ArrayEqual((ulong[]?)_ReferenceValue, (ulong[]?)other._ReferenceValue),
            SettingType.I64Array => _ArrayEqual((long[]?)_ReferenceValue, (long[]?)other._ReferenceValue),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is SettingValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        Type switch
        {
            SettingType.Bool => HashCode.Combine(Type, _Bits),
            SettingType.F64 => HashCode.Combine(Type, _Bits),
            SettingType.U64 => HashCode.Combine(Type, _Bits),
            SettingType.I64 => HashCode.Combine(Type, _Bits),
            SettingType.Limit => HashCode.Combine(Type, _Bits),
            SettingType.String => HashCode.Combine(Type, _ReferenceValue),
            SettingType.Bytes => _ContentHashBytes((byte[]?)_ReferenceValue),
            SettingType.Enum => HashCode.Combine(Type, _Bits, _ReferenceValue),
            SettingType.BoolArray => _ContentHashUnmanaged((bool[]?)_ReferenceValue, SettingType.BoolArray),
            SettingType.StringArray => _ContentHashStrings((string[]?)_ReferenceValue),
            SettingType.F64Array => _ContentHashF64Array((double[]?)_ReferenceValue),
            SettingType.U64Array => _ContentHashUnmanaged((ulong[]?)_ReferenceValue, SettingType.U64Array),
            SettingType.I64Array => _ContentHashUnmanaged((long[]?)_ReferenceValue, SettingType.I64Array),
            _ => HashCode.Combine(Type),
        };

    /// <summary>Returns <see langword="true"/> if both values are equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when both values have the same type and content (NaN equals NaN; strings compare ordinally).</returns>
    public static bool operator ==(SettingValue left, SettingValue right) => left.Equals(right);

    /// <summary>Returns <see langword="true"/> if the values are not equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when the values differ in type or content.</returns>
    public static bool operator !=(SettingValue left, SettingValue right) => !left.Equals(right);

    #endregion

    #region ISpanFormattable

    // Display form (for logs and UI labels), written straight into the caller's buffer so formatting allocates nothing:
    //   Bool "True"/"False" (capitalized, unlike the text form), numbers in the invariant culture, String as is,
    //   Bytes "[N bytes]", Enum "Name (value)", arrays "[N type]" (only the element count, never the elements).
    // The provider argument is ignored on purpose: a setting value must read the same in every locale, so numbers always use
    // CultureInfo.InvariantCulture. When the destination is too small the member returns false with 0 written (it never throws),
    // which is how callers probe with a stack buffer and fall back to a larger one. TryGetStringSize must report exactly the
    // lengths produced here; change the labels in both places.

    /// <inheritdoc/>
    public bool TryFormat(
        Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        switch (Type)
        {
            case SettingType.Bool:
            {
                ReadOnlySpan<char> label = _Bits != 0
                    ? "True"
                    : "False";
                if (destination.Length < label.Length)
                {
                    charsWritten = 0;
                    return false;
                }
                label.CopyTo(destination);
                charsWritten = label.Length;
                return true;
            }
            case SettingType.F64:
                return BitConverter.Int64BitsToDouble(_Bits)
                    .TryFormat(destination, out charsWritten, format, CultureInfo.InvariantCulture);
            case SettingType.U64:
                return ((ulong)_Bits).TryFormat(destination, out charsWritten, format, CultureInfo.InvariantCulture);
            case SettingType.I64:
                return _Bits.TryFormat(destination, out charsWritten, format, CultureInfo.InvariantCulture);
            case SettingType.Limit:
                // Display form: "off" or the base number. The text form with units needs the definition (SettingValueText).
                if (_Bits < 0)
                {
                    return _TryCopyLabel("off", destination, out charsWritten);
                }

                return _Bits.TryFormat(destination, out charsWritten, format, CultureInfo.InvariantCulture);
            case SettingType.String:
                if (_ReferenceValue is string str)
                {
                    if (destination.Length < str.Length)
                    {
                        charsWritten = 0;
                        return false;
                    }
                    str.AsSpan().CopyTo(destination);
                    charsWritten = str.Length;
                    return true;
                }
                charsWritten = 0;
                return true;
            case SettingType.Bytes:
                if (_ReferenceValue is byte[] bytes)
                {
                    return _TryFormatBytesLabel(bytes.Length, destination, out charsWritten);
                }
                return _TryFormatBytesLabel(0, destination, out charsWritten);
            case SettingType.Enum:
                if (_ReferenceValue is string name)
                {
                    return _TryFormatEnumLabel(name, (ulong)_Bits, destination, out charsWritten);
                }
                charsWritten = 0;
                return true;
            case SettingType.BoolArray:
                return _TryFormatArrayCountLabel(_ArrayLength((bool[]?)_ReferenceValue), " bool]", destination, out charsWritten);
            case SettingType.StringArray:
                return _TryFormatArrayCountLabel(_ArrayLength((string[]?)_ReferenceValue), " string]", destination, out charsWritten);
            case SettingType.F64Array:
                return _TryFormatArrayCountLabel(_ArrayLength((double[]?)_ReferenceValue), " f64]", destination, out charsWritten);
            case SettingType.U64Array:
                return _TryFormatArrayCountLabel(_ArrayLength((ulong[]?)_ReferenceValue), " u64]", destination, out charsWritten);
            case SettingType.I64Array:
                return _TryFormatArrayCountLabel(_ArrayLength((long[]?)_ReferenceValue), " i64]", destination, out charsWritten);
            default:
                charsWritten = 0;
                return true;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Format();

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (Type == SettingType.String && _ReferenceValue is string str)
        {
            return str;
        }

        if (string.IsNullOrEmpty(format))
        {
            return Format();
        }

        IFormatProvider provider = formatProvider ?? CultureInfo.InvariantCulture;
        return _FormatToString(format, provider);
    }

    #endregion

    #region IUtf8SpanFormattable

    // Same display form and same rules as the char overload above, written as UTF-8 bytes. The labels use u8 literals so no
    // transcoding is needed; String and Enum names are encoded by _TryEncodeUtf8 and _TryFormatEnumLabelUtf8.

    /// <inheritdoc/>
    public bool TryFormat(
        Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        switch (Type)
        {
            case SettingType.Bool:
            {
                ReadOnlySpan<byte> label = _Bits != 0
                    ? "True"u8
                    : "False"u8;
                if (utf8Destination.Length < label.Length)
                {
                    bytesWritten = 0;
                    return false;
                }
                label.CopyTo(utf8Destination);
                bytesWritten = label.Length;
                return true;
            }
            case SettingType.F64:
                return BitConverter.Int64BitsToDouble(_Bits)
                    .TryFormat(utf8Destination, out bytesWritten, format, CultureInfo.InvariantCulture);
            case SettingType.U64:
                return ((ulong)_Bits).TryFormat(utf8Destination, out bytesWritten, format, CultureInfo.InvariantCulture);
            case SettingType.I64:
                return _Bits.TryFormat(utf8Destination, out bytesWritten, format, CultureInfo.InvariantCulture);
            case SettingType.Limit:
                if (_Bits < 0)
                {
                    return _TryCopyLabelUtf8("off"u8, utf8Destination, out bytesWritten);
                }

                return _Bits.TryFormat(utf8Destination, out bytesWritten, format, CultureInfo.InvariantCulture);
            case SettingType.String:
                if (_ReferenceValue is string str)
                {
                    return _TryEncodeUtf8(str, utf8Destination, out bytesWritten);
                }
                bytesWritten = 0;
                return true;
            case SettingType.Bytes:
                if (_ReferenceValue is byte[] bytes)
                {
                    return _TryFormatBytesLabelUtf8(bytes.Length, utf8Destination, out bytesWritten);
                }
                return _TryFormatBytesLabelUtf8(0, utf8Destination, out bytesWritten);
            case SettingType.Enum:
                if (_ReferenceValue is string name)
                {
                    return _TryFormatEnumLabelUtf8(name, (ulong)_Bits, utf8Destination, out bytesWritten);
                }
                bytesWritten = 0;
                return true;
            case SettingType.BoolArray:
                return _TryFormatArrayCountLabelUtf8(_ArrayLength((bool[]?)_ReferenceValue), " bool]"u8, utf8Destination, out bytesWritten);
            case SettingType.StringArray:
                return _TryFormatArrayCountLabelUtf8(_ArrayLength((string[]?)_ReferenceValue), " string]"u8, utf8Destination, out bytesWritten);
            case SettingType.F64Array:
                return _TryFormatArrayCountLabelUtf8(_ArrayLength((double[]?)_ReferenceValue), " f64]"u8, utf8Destination, out bytesWritten);
            case SettingType.U64Array:
                return _TryFormatArrayCountLabelUtf8(_ArrayLength((ulong[]?)_ReferenceValue), " u64]"u8, utf8Destination, out bytesWritten);
            case SettingType.I64Array:
                return _TryFormatArrayCountLabelUtf8(_ArrayLength((long[]?)_ReferenceValue), " i64]"u8, utf8Destination, out bytesWritten);
            default:
                bytesWritten = 0;
                return true;
        }
    }

    #endregion

    #region String Size

    /// <summary>
    /// Tries to determine the number of characters needed to format this value. A plain method (no interface) because
    /// every caller names the concrete type.
    /// Returns <see langword="true"/> with an exact count for Bool, String, Bytes, Enum, array types, and
    /// default-format integers. Returns <see langword="false"/> for <see cref="SettingType.F64"/>
    /// and for integers when a non-empty format string is supplied, because those lengths depend
    /// on the value and format.
    /// </summary>
    /// <param name="format">The format string that the formatting will use; empty for the default form.</param>
    /// <param name="provider">Ignored: a setting value reads the same in every locale.</param>
    /// <param name="size">The exact number of characters when the method returns <see langword="true"/>; otherwise 0.</param>
    /// <returns><see langword="true"/> when the size is exact.</returns>
    public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        // The numbers below are the lengths of the literals TryFormat writes: "True" 4, "False" 5, and the array suffixes
        // " bool]" 6, " string]" 8, " f64]" / " u64]" / " i64]" 5 (the count and the opening bracket are added by the helper).
        // An exact size lets Format() size a buffer once and never retry. F64 has no exact size because its
        // shortest round-trip text depends on the value; a custom format string changes integer lengths as well.
        switch (Type)
        {
            case SettingType.Bool:
                size = _Bits != 0
                    ? 4
                    : 5;
                return true;
            case SettingType.String:
                size = _ReferenceValue is string str
                    ? str.Length
                    : 0;
                return true;
            case SettingType.Bytes:
                int byteCount = _ReferenceValue is byte[] bytes
                    ? bytes.Length
                    : 0;
                size = _BytesLabelCharCount(byteCount);
                return true;
            case SettingType.BoolArray:
                size = _ArrayCountLabelCharCount(_ArrayLength((bool[]?)_ReferenceValue), 6);
                return true;
            case SettingType.StringArray:
                size = _ArrayCountLabelCharCount(_ArrayLength((string[]?)_ReferenceValue), 8);
                return true;
            case SettingType.F64Array:
                size = _ArrayCountLabelCharCount(_ArrayLength((double[]?)_ReferenceValue), 5);
                return true;
            case SettingType.U64Array:
                size = _ArrayCountLabelCharCount(_ArrayLength((ulong[]?)_ReferenceValue), 5);
                return true;
            case SettingType.I64Array:
                size = _ArrayCountLabelCharCount(_ArrayLength((long[]?)_ReferenceValue), 5);
                return true;
            case SettingType.Enum:
                if (_ReferenceValue is string name)
                {
                    size = _EnumLabelCharCount(name, (ulong)_Bits);
                    return true;
                }
                size = 0;
                return true;
            case SettingType.U64:
                if (!format.IsEmpty)
                {
                    size = 0;
                    return false;
                }
                size = _UInt64DigitCount((ulong)_Bits);
                return true;
            case SettingType.I64:
                if (!format.IsEmpty)
                {
                    size = 0;
                    return false;
                }
                size = _Int64FormattedLength(_Bits);
                return true;
            case SettingType.Limit:
                if (!format.IsEmpty)
                {
                    size = 0;
                    return false;
                }

                // "off" is 3 characters; every other payload is a non-negative number.
                size = _Bits < 0
                    ? 3
                    : _Int64FormattedLength(_Bits);
                return true;
            case SettingType.F64:
                size = 0;
                return false;
            default:
                size = 0;
                return true;
        }
    }

    #endregion

    #region Convenience Formatting

    /// <summary>
    /// Upper bound for default <c>G</c>/<c>R</c> formatting of a <see cref="double"/>.
    /// <c>-1.7976931348623157E+308</c> is 24 characters; 32 leaves headroom.
    /// </summary>
    private const int _F64DefaultMaxChars = 32;

    /// <summary>
    /// Largest character count formatted into a <c>stackalloc</c> buffer (256 chars = 512 bytes of stack); a longer result goes
    /// through a pooled <see cref="ArrayPool{T}"/> buffer. Bounds the stack use per call. Constant of the call shape, not
    /// an external-input limit.
    /// </summary>
    private const int _StackFormatLimit = 256;

    /// <summary>Writes the formatted label into <paramref name="destination"/>. Returns characters written (0 if the span is too small).</summary>
    /// <param name="destination">The buffer that receives the label.</param>
    /// <returns>The number of characters written; 0 when the buffer is too small.</returns>
    public int FormatInto(Span<char> destination)
    {
        TryFormat(destination, out int written, default, CultureInfo.InvariantCulture);
        return written;
    }

    /// <summary>
    /// Formats a label longer than <see cref="_StackFormatLimit"/> through a pooled buffer and allocates only the result.
    /// </summary>
    /// <remarks>
    /// <paramref name="capacity"/> is the exact size from <see cref="TryGetStringSize"/>. The rental is returned in
    /// <c>finally</c>; the pool is shared across threads, so nested or concurrent calls simply rent another array. Rejected: a
    /// thread-static buffer, which cannot be used re-entrantly (a nested call would allocate a second multi-megabyte array).
    /// Trade-off: a pool rental per long label instead of a retained per-thread array; long labels are rare.
    /// </remarks>
    /// <param name="capacity">The exact character count of the label.</param>
    /// <param name="format">The non-empty format string.</param>
    /// <param name="provider">The format provider.</param>
    /// <returns>The formatted label.</returns>
    private string _FormatPooled(int capacity, ReadOnlySpan<char> format, IFormatProvider provider)
    {
        char[] rented = ArrayPool<char>.Shared.Rent(capacity);
        try
        {
            if (!TryFormat(rented, out int written, format, provider))
            {
                throw new InvalidOperationException("Setting label exceeds its reported size; TryGetStringSize is wrong.");
            }

            return new string(rented, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Returns the formatted label as a new string.</summary>
    /// <remarks>
    /// String values return the stored instance. Other types allocate exactly the result string: results up to the stack limit
    /// are built on the stack, longer ones through a pooled buffer. Use <see cref="FormatInto"/> when the caller owns a buffer.
    /// </remarks>
    public string Format()
    {
        if (Type == SettingType.String && _ReferenceValue is string str)
        {
            return str;
        }

        // F64 is the only type without an exact size for the default format: its text always fits the fixed upper bound,
        // so it is formatted on the stack as well.
        if (!TryGetStringSize(default, CultureInfo.InvariantCulture, out int size))
        {
            // _F64DefaultMaxChars exceeds the longest default double text (24 characters), so TryFormat cannot run out of room
            // and its result needs no check.
            Span<char> f64Stack = stackalloc char[_F64DefaultMaxChars];
            TryFormat(f64Stack, out int f64Written, default, CultureInfo.InvariantCulture);
            return new string(f64Stack[..f64Written]);
        }

        // Small results of known size are formatted on the stack so the only allocation is the result string itself.
        if (size <= _StackFormatLimit)
        {
            if (size == 0)
            {
                return "";
            }

            Span<char> stack = stackalloc char[size];
            TryFormat(stack, out int written, default, CultureInfo.InvariantCulture);
            return new string(stack[..written]);
        }

        return _FormatPooled(size, default, CultureInfo.InvariantCulture);
    }

    /// <summary>Formats with a non-empty format string into a sized buffer, then allocates the result string.</summary>
    /// <param name="format">The non-empty format string.</param>
    /// <param name="provider">The format provider.</param>
    /// <returns>The formatted label.</returns>
    private string _FormatToString(ReadOnlySpan<char> format, IFormatProvider provider)
    {
        // Exact size known (non-numeric types): one sized buffer, no retry.
        if (TryGetStringSize(format, provider, out int size))
        {
            return _FormatKnownSizeToString(format, provider, size);
        }

        // Size unknown (F64, or an integer with a format string): try a fixed stack buffer first. A 256-char buffer holds any
        // double or 64-bit integer text, so the heap fallback below is only a safety net for unusual format strings.
        Span<char> stack = stackalloc char[_StackFormatLimit];
        if (TryFormat(stack, out int written, format, provider))
        {
            return new string(stack[..written]);
        }

        // The fallback formats with the invariant culture too, not with the caller's provider: TryFormat ignores the provider on
        // purpose (a setting value reads the same in every locale), so the same value must not come out differently only
        // because its text is longer than the stack buffer.
        string formatString = format.ToString();
        if (Type == SettingType.F64)
        {
            return BitConverter.Int64BitsToDouble(_Bits).ToString(formatString, CultureInfo.InvariantCulture);
        }

        if (Type == SettingType.U64)
        {
            return ((ulong)_Bits).ToString(formatString, CultureInfo.InvariantCulture);
        }

        return _Bits.ToString(formatString, CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a value whose character count is already known.</summary>
    /// <param name="format">The format string.</param>
    /// <param name="provider">The format provider.</param>
    /// <param name="size">The exact character count of the label, from <see cref="TryGetStringSize"/>.</param>
    /// <returns>The formatted label.</returns>
    private string _FormatKnownSizeToString(ReadOnlySpan<char> format, IFormatProvider provider, int size)
    {
        if (size <= 0)
        {
            return "";
        }

        if (size <= _StackFormatLimit)
        {
            Span<char> stack = stackalloc char[size];
            TryFormat(stack, out int written, format, provider);
            return new string(stack[..written]);
        }

        return _FormatPooled(size, format, provider);
    }

    #endregion

    #region Formatting helpers

    /// <summary>Copies a fixed label into the caller's buffer; false with 0 written when the buffer is too small (never throws).</summary>
    /// <param name="label">The label to write.</param>
    /// <param name="destination">The caller's buffer.</param>
    /// <param name="charsWritten">The label length, or 0 when the buffer is too small.</param>
    /// <returns><see langword="true"/> when the label fits.</returns>
    private static bool _TryCopyLabel(ReadOnlySpan<char> label, Span<char> destination, out int charsWritten)
    {
        if (destination.Length < label.Length)
        {
            charsWritten = 0;
            return false;
        }

        label.CopyTo(destination);
        charsWritten = label.Length;
        return true;
    }

    /// <summary>UTF-8 counterpart of <see cref="_TryCopyLabel"/>.</summary>
    /// <param name="label">The label to write.</param>
    /// <param name="destination">The caller's buffer.</param>
    /// <param name="bytesWritten">The label length in bytes, or 0 when the buffer is too small.</param>
    /// <returns><see langword="true"/> when the label fits.</returns>
    private static bool _TryCopyLabelUtf8(ReadOnlySpan<byte> label, Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < label.Length)
        {
            bytesWritten = 0;
            return false;
        }

        label.CopyTo(destination);
        bytesWritten = label.Length;
        return true;
    }

    /// <summary>Writes the label <c>[N bytes]</c>; returns <see langword="false"/> with 0 written when the span is too small.</summary>
    /// <param name="byteCount">The number of bytes the label reports; not negative.</param>
    /// <param name="destination">The span that receives the label.</param>
    /// <param name="charsWritten">The characters written; 0 when the span is too small.</param>
    /// <returns><see langword="true"/> when the label fits into <paramref name="destination"/>.</returns>
    private static bool _TryFormatBytesLabel(int byteCount, Span<char> destination, out int charsWritten)
    {
        int total = _BytesLabelCharCount(byteCount);
        if (destination.Length < total)
        {
            charsWritten = 0;
            return false;
        }

        destination[0] = '[';
        byteCount.TryFormat(destination[1..], out int digitCount, default, CultureInfo.InvariantCulture);
        " bytes]".CopyTo(destination[(1 + digitCount)..]);
        charsWritten = total;
        return true;
    }

    /// <summary>UTF-8 counterpart of <see cref="_TryFormatBytesLabel"/>; the label is ASCII, so its char count is its byte count.</summary>
    /// <param name="byteCount">The number of bytes the label reports; not negative.</param>
    /// <param name="destination">The span that receives the label.</param>
    /// <param name="bytesWritten">The bytes written; 0 when the span is too small.</param>
    /// <returns><see langword="true"/> when the label fits into <paramref name="destination"/>.</returns>
    private static bool _TryFormatBytesLabelUtf8(int byteCount, Span<byte> destination, out int bytesWritten)
    {
        int total = _BytesLabelCharCount(byteCount);
        if (destination.Length < total)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = (byte)'[';
        byteCount.TryFormat(destination[1..], out int digitCount, default, CultureInfo.InvariantCulture);
        " bytes]"u8.CopyTo(destination[(1 + digitCount)..]);
        bytesWritten = total;
        return true;
    }

    /// <summary>
    /// Formats <c>[N type]</c> for array settings. <paramref name="suffixWithBracket"/> is the text after the count,
    /// including the closing bracket (e.g. <c> u64]</c>).
    /// </summary>
    /// <param name="count">The number of elements; not negative.</param>
    /// <param name="suffixWithBracket">The text after the count, with the closing bracket.</param>
    /// <param name="destination">The span that receives the label.</param>
    /// <param name="charsWritten">The characters written; 0 when the span is too small.</param>
    /// <returns><see langword="true"/> when the label fits into <paramref name="destination"/>.</returns>
    private static bool _TryFormatArrayCountLabel(
        int count, ReadOnlySpan<char> suffixWithBracket, Span<char> destination, out int charsWritten)
    {
        int total = _ArrayCountLabelCharCount(count, suffixWithBracket.Length);
        if (destination.Length < total)
        {
            charsWritten = 0;
            return false;
        }

        destination[0] = '[';
        count.TryFormat(destination[1..], out int digitCount, default, CultureInfo.InvariantCulture);
        suffixWithBracket.CopyTo(destination[(1 + digitCount)..]);
        charsWritten = total;
        return true;
    }

    /// <summary>UTF-8 counterpart of <see cref="_TryFormatArrayCountLabel"/>.</summary>
    /// <param name="count">The number of elements; not negative.</param>
    /// <param name="suffixWithBracket">The ASCII text after the count, with the closing bracket.</param>
    /// <param name="destination">The span that receives the label.</param>
    /// <param name="bytesWritten">The bytes written; 0 when the span is too small.</param>
    /// <returns><see langword="true"/> when the label fits into <paramref name="destination"/>.</returns>
    private static bool _TryFormatArrayCountLabelUtf8(
        int count, ReadOnlySpan<byte> suffixWithBracket, Span<byte> destination, out int bytesWritten)
    {
        int total = _ArrayCountLabelCharCount(count, suffixWithBracket.Length);
        if (destination.Length < total)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = (byte)'[';
        count.TryFormat(destination[1..], out int digitCount, default, CultureInfo.InvariantCulture);
        suffixWithBracket.CopyTo(destination[(1 + digitCount)..]);
        bytesWritten = total;
        return true;
    }

    /// <summary>Character count of <c>[N&lt;suffix&gt;</c> for a non-negative <paramref name="count"/>.</summary>
    /// <param name="count">The number of elements; not negative.</param>
    /// <param name="suffixLength">The length of the fixed text after the number.</param>
    /// <returns>The character count of the label: one bracket, the digits of the count, and the suffix.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _ArrayCountLabelCharCount(int count, int suffixLength) =>
        1 + _UInt64DigitCount((ulong)(uint)count) + suffixLength;

    /// <summary>Writes the label <c>Name (value)</c>; returns <see langword="false"/> with 0 written when the span is too small.</summary>
    /// <param name="name">The name of the enum value.</param>
    /// <param name="numericValue">The number of the enum value.</param>
    /// <param name="destination">The span that receives the label.</param>
    /// <param name="charsWritten">The characters written; 0 when the span is too small.</param>
    /// <returns><see langword="true"/> when the label fits into <paramref name="destination"/>.</returns>
    private static bool _TryFormatEnumLabel(
        ReadOnlySpan<char> name, ulong numericValue, Span<char> destination, out int charsWritten)
    {
        int total = _EnumLabelCharCount(name, numericValue);
        if (destination.Length < total)
        {
            charsWritten = 0;
            return false;
        }

        name.CopyTo(destination);
        int pos = name.Length;
        destination[pos++] = ' ';
        destination[pos++] = '(';
        numericValue.TryFormat(destination[pos..], out int digitCount, default, CultureInfo.InvariantCulture);
        pos += digitCount;
        destination[pos++] = ')';
        charsWritten = pos;
        return true;
    }

    /// <summary>UTF-8 counterpart of <see cref="_TryFormatEnumLabel"/>; the name may contain non-ASCII characters.</summary>
    /// <param name="name">The name of the enum value.</param>
    /// <param name="numericValue">The number of the enum value.</param>
    /// <param name="destination">The span that receives the label.</param>
    /// <param name="bytesWritten">The bytes written; 0 when the span is too small.</param>
    /// <returns><see langword="true"/> when the label fits into <paramref name="destination"/>.</returns>
    private static bool _TryFormatEnumLabelUtf8(
        ReadOnlySpan<char> name, ulong numericValue, Span<byte> destination, out int bytesWritten)
    {
        int digits = _UInt64DigitCount(numericValue);

        // ASCII names (the usual case) are narrowed char by char: the byte count equals the char count, so the size check needs
        // no encoder pass. Any other name takes the general path below, which asks the UTF-8 encoder for the exact byte count.
        if (System.Text.Ascii.IsValid(name))
        {
            int total = name.Length + 3 + digits;
            if (destination.Length < total)
            {
                bytesWritten = 0;
                return false;
            }

            int written = _NarrowAscii(name, destination);
            destination[written++] = (byte)' ';
            destination[written++] = (byte)'(';
            numericValue.TryFormat(destination[written..], out int digitCount, default, CultureInfo.InvariantCulture);
            written += digitCount;
            destination[written++] = (byte)')';
            bytesWritten = written;
            return true;
        }

        int nameBytes = Encoding.UTF8.GetByteCount(name);
        int utf8Total = nameBytes + 3 + digits;
        if (destination.Length < utf8Total)
        {
            bytesWritten = 0;
            return false;
        }

        int pos = Encoding.UTF8.GetBytes(name, destination);
        destination[pos++] = (byte)' ';
        destination[pos++] = (byte)'(';
        numericValue.TryFormat(destination[pos..], out int utf8Digits, default, CultureInfo.InvariantCulture);
        pos += utf8Digits;
        destination[pos++] = (byte)')';
        bytesWritten = pos;
        return true;
    }

    /// <summary>
    /// Encodes <paramref name="chars"/> as UTF-8. Returns <see langword="false"/> when the
    /// destination is shorter than the encoded size (does not throw). ASCII text is narrowed
    /// without going through <see cref="Encoding.UTF8"/>.
    /// </summary>
    /// <param name="chars">The text to encode.</param>
    /// <param name="utf8Destination">The span that receives the bytes.</param>
    /// <param name="bytesWritten">The bytes written; 0 when the destination is too short.</param>
    /// <returns><see langword="true"/> when the whole text was encoded.</returns>
    private static bool _TryEncodeUtf8(ReadOnlySpan<char> chars, Span<byte> utf8Destination, out int bytesWritten)
    {
        if (System.Text.Ascii.IsValid(chars))
        {
            if (utf8Destination.Length < chars.Length)
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = _NarrowAscii(chars, utf8Destination);
            return true;
        }

        int byteCount = Encoding.UTF8.GetByteCount(chars);
        if (utf8Destination.Length < byteCount)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = Encoding.UTF8.GetBytes(chars, utf8Destination);
        return true;
    }

    /// <summary>Character count of <c>[N bytes]</c> for a non-negative <paramref name="byteCount"/>.</summary>
    /// <param name="byteCount">The number of bytes the label reports; not negative.</param>
    /// <returns>The character count of the label.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _BytesLabelCharCount(int byteCount) =>
        1 + _UInt64DigitCount((ulong)(uint)byteCount) + 7;

    /// <summary>
    /// Character count of <c>Name (value)</c>.
    /// Name length plus 3 plus at most 20 digits cannot overflow a 32-bit signed length for any allocatable string.
    /// </summary>
    /// <param name="name">The name of the enum value.</param>
    /// <param name="numericValue">The number of the enum value.</param>
    /// <returns>The character count of the label.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _EnumLabelCharCount(ReadOnlySpan<char> name, ulong numericValue) =>
        name.Length + 3 + _UInt64DigitCount(numericValue);

    /// <summary>Narrows already-validated ASCII chars to bytes. Caller guarantees destination is large enough.</summary>
    /// <param name="chars">The characters; every one is ASCII.</param>
    /// <param name="destination">The span that receives the bytes; at least as long as <paramref name="chars"/>.</param>
    /// <returns>The number of bytes written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _NarrowAscii(ReadOnlySpan<char> chars, Span<byte> destination)
    {
        for (int i = 0; i < chars.Length; i++)
        {
            destination[i] = (byte)chars[i];
        }

        return chars.Length;
    }

    /// <summary>Decimal digit count of <paramref name="value"/> (1 for zero). Range 1..20.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The digit count, 1 to 20.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _UInt64DigitCount(ulong value)
    {
        int digits = 1;
        while (value >= 10UL)
        {
            value /= 10UL;
            digits++;
        }

        return digits;
    }

    /// <summary>Decimal character count of <paramref name="value"/> including a leading minus when negative.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The character count, one more for a negative number.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _Int64FormattedLength(long value)
    {
        if (value >= 0)
        {
            return _UInt64DigitCount((ulong)value);
        }

        // Negating long.MinValue overflows (its magnitude is 2^63, one more than long.MaxValue), so it is answered directly:
        // "-9223372036854775808" is 1 + 19 = 20 characters.
        if (value == long.MinValue)
        {
            return 20;
        }

        return 1 + _UInt64DigitCount((ulong)(-value));
    }

    #endregion

    #region Implicit Conversions

    /// <summary>Wraps a <see cref="bool"/> into a <see cref="SettingValue"/>.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>A boolean setting value.</returns>
    public static implicit operator SettingValue(bool value) => Bool(value);

    /// <summary>Wraps a <see cref="string"/> into a <see cref="SettingValue"/>.</summary>
    /// <param name="value">The text; not null.</param>
    /// <returns>A string setting value.</returns>
    public static implicit operator SettingValue(string value) => String(value);

    /// <summary>Wraps a <see cref="double"/> into a <see cref="SettingValue"/>.</summary>
    /// <param name="value">The number.</param>
    /// <returns>An F64 setting value.</returns>
    public static implicit operator SettingValue(double value) => F64(value);

    /// <summary>
    /// Wraps a <see cref="ulong"/> into a <see cref="SettingValue"/>.
    /// Without this overload, literals passed to APIs such as <see cref="SettingsManager.PreloadValue"/>
    /// would match the <see cref="double"/> conversion instead, producing <see cref="SettingType.F64"/>
    /// and failing type validation for <see cref="SettingType.U64"/> settings.
    /// </summary>
    /// <param name="value">The number.</param>
    /// <returns>A U64 setting value.</returns>
    public static implicit operator SettingValue(ulong value) => U64(value);

    /// <summary>Compares two byte arrays for element-wise equality.</summary>
    /// <param name="a">The first array, or <see langword="null"/>.</param>
    /// <param name="b">The second array, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are null, or both hold the same bytes.</returns>
    private static bool _BytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return a.AsSpan().SequenceEqual(b.AsSpan());
    }

    /// <summary>Computes a content-based hash code for byte array settings.</summary>
    /// <param name="data">The bytes, or <see langword="null"/>.</param>
    /// <returns>A hash of the type and the content; for <see langword="null"/> a hash of the type alone.</returns>
    private static int _ContentHashBytes(byte[]? data)
    {
        HashCode hash = new();
        hash.Add(SettingType.Bytes);
        if (data is not null)
        {
            hash.AddBytes(data.AsSpan());
        }
        return hash.ToHashCode();
    }

    /// <summary>Element count of a stored array; 0 when the reference is null.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="array">The stored array, or <see langword="null"/>.</param>
    /// <returns>The number of elements; 0 for <see langword="null"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _ArrayLength<T>(T[]? array)
    {
        if (array is null)
        {
            return 0;
        }

        return array.Length;
    }

    /// <summary>Element-wise equality for unmanaged arrays. Null is not equal to an empty stored array.</summary>
    /// <typeparam name="T">The unmanaged element type.</typeparam>
    /// <param name="a">The first array, or <see langword="null"/>.</param>
    /// <param name="b">The second array, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are null, or both hold the same elements in the same order.</returns>
    private static bool _ArrayEqual<T>(T[]? a, T[]? b)
        where T : unmanaged, IEquatable<T>
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return a.AsSpan().SequenceEqual(b.AsSpan());
    }

    /// <summary>Ordinal element-wise equality for string arrays.</summary>
    /// <param name="a">The first array, or <see langword="null"/>.</param>
    /// <param name="b">The second array, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are null, or both hold the same strings in the same order.</returns>
    private static bool _StringArrayEqual(string[]? a, string[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bit-wise F64 array equality so NaN equals NaN, matching scalar <see cref="SettingType.F64"/>.
    /// </summary>
    /// <param name="a">The first array, or <see langword="null"/>.</param>
    /// <param name="b">The second array, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are null, or every element has the same bits.</returns>
    private static bool _F64ArrayEqual(double[]? a, double[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }

        return MemoryMarshal.Cast<double, long>(a).SequenceEqual(MemoryMarshal.Cast<double, long>(b));
    }

    /// <summary>Content hash for unmanaged array settings.</summary>
    /// <typeparam name="T">The unmanaged element type.</typeparam>
    /// <param name="data">The elements, or <see langword="null"/>.</param>
    /// <param name="type">The setting type, mixed into the hash so equal bytes of different types differ.</param>
    /// <returns>A hash of the type and the content; for <see langword="null"/> a hash of the type alone.</returns>
    private static int _ContentHashUnmanaged<T>(T[]? data, SettingType type)
        where T : unmanaged
    {
        HashCode hash = new();
        hash.Add(type);
        if (data is not null)
        {
            hash.AddBytes(MemoryMarshal.AsBytes(data.AsSpan()));
        }
        return hash.ToHashCode();
    }

    /// <summary>Content hash for F64 arrays using IEEE bit patterns so NaN hashes stably.</summary>
    /// <param name="data">The numbers, or <see langword="null"/>.</param>
    /// <returns>A hash of the type and the bit patterns; for <see langword="null"/> a hash of the type alone.</returns>
    private static int _ContentHashF64Array(double[]? data)
    {
        HashCode hash = new();
        hash.Add(SettingType.F64Array);
        if (data is not null)
        {
            hash.AddBytes(MemoryMarshal.AsBytes(MemoryMarshal.Cast<double, long>(data)));
        }
        return hash.ToHashCode();
    }

    /// <summary>Ordinal content hash for string arrays.</summary>
    /// <param name="data">The strings, or <see langword="null"/>.</param>
    /// <returns>A hash of the type and the strings; for <see langword="null"/> a hash of the type alone.</returns>
    private static int _ContentHashStrings(string[]? data)
    {
        HashCode hash = new();
        hash.Add(SettingType.StringArray);
        if (data is not null)
        {
            for (int i = 0; i < data.Length; i++)
            {
                hash.Add(data[i], StringComparer.Ordinal);
            }
        }
        return hash.ToHashCode();
    }
    #endregion
}
