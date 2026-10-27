// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

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

    private readonly long _Bits;
    private readonly object? _ReferenceValue;

    /// <summary>Assembles a value from its three storage parts; the factories guarantee that they match <paramref name="type"/>.</summary>
    private SettingValue(SettingType type, long bits, object? referenceValue)
    {
        Type = type;
        _Bits = bits;
        _ReferenceValue = referenceValue;
    }

    #region Factory Methods

    /// <summary>Creates a boolean setting value.</summary>
    public static SettingValue Bool(bool value) =>
        new(SettingType.Bool, value ? 1L : 0L, null);

    /// <summary>Creates a string setting value.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public static SettingValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.String, 0L, value);
    }

    /// <summary>Creates a 64-bit floating point setting value.</summary>
    public static SettingValue F64(double value) =>
        new(SettingType.F64, BitConverter.DoubleToInt64Bits(value), null);

    /// <summary>Creates a 64-bit unsigned integer setting value.</summary>
    public static SettingValue U64(ulong value) =>
        new(SettingType.U64, (long)value, null);

    /// <summary>Creates a 64-bit signed integer setting value.</summary>
    public static SettingValue I64(long value) =>
        new(SettingType.I64, value, null);

    /// <summary>Creates a byte array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public static SettingValue Bytes(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Defensive copy: isolate internal state from external mutations of the source array.
        return new(SettingType.Bytes, 0L, value.ToArray());
    }

    /// <summary>Creates a byte array setting value from a ReadOnlyMemory.</summary>
    public static SettingValue Bytes(ReadOnlyMemory<byte> value) =>
        new(SettingType.Bytes, 0L, value.ToArray());

    /// <summary>Creates an enum setting value.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    public static SettingValue Enum(string name, ulong numericValue)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(SettingType.Enum, (long)numericValue, name);
    }

    /// <summary>Creates a boolean array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
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
    public static SettingValue F64Array(double[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.F64Array, 0L, value.ToArray());
    }

    /// <summary>Creates a U64 array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public static SettingValue U64Array(ulong[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SettingType.U64Array, 0L, value.ToArray());
    }

    /// <summary>Creates an I64 array setting value. The array is defensively copied to prevent external mutation.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
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
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.Bytes"/> and
    /// writes a <em>defensive copy</em> of the byte array into <paramref name="value"/>;
    /// otherwise sets <paramref name="value"/> to an empty array and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
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
    public static bool operator ==(SettingValue left, SettingValue right) => left.Equals(right);

    /// <summary>Returns <see langword="true"/> if the values are not equal.</summary>
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
                ReadOnlySpan<char> label = _Bits != 0 ? "True" : "False";
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
                ReadOnlySpan<byte> label = _Bits != 0 ? "True"u8 : "False"u8;
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
    public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        // The numbers below are the lengths of the literals TryFormat writes: "True" 4, "False" 5, and the array suffixes
        // " bool]" 6, " string]" 8, " f64]" / " u64]" / " i64]" 5 (the count and the opening bracket are added by the helper).
        // An exact size lets Format() size a buffer once and never retry. F64 has no exact size because its
        // shortest round-trip text depends on the value; a custom format string changes integer lengths as well.
        switch (Type)
        {
            case SettingType.Bool:
                size = _Bits != 0 ? 4 : 5;
                return true;
            case SettingType.String:
                size = _ReferenceValue is string str ? str.Length : 0;
                return true;
            case SettingType.Bytes:
                int byteCount = _ReferenceValue is byte[] bytes ? bytes.Length : 0;
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
    /// <c>finally</c>; the pool is shared across threads, so nested or concurrent calls simply rent another array. This
    /// replaced a thread-static buffer that could not be used re-entrantly (a nested call allocated a second multi-megabyte
    /// array). Trade-off: a pool rental per long label instead of a retained per-thread array; long labels are rare.
    /// </remarks>
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

    /// <summary>Writes the label <c>[N bytes]</c>; returns <see langword="false"/> with 0 written when the span is too small.</summary>
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _ArrayCountLabelCharCount(int count, int suffixLength) =>
        1 + _UInt64DigitCount((ulong)(uint)count) + suffixLength;

    /// <summary>Writes the label <c>Name (value)</c>; returns <see langword="false"/> with 0 written when the span is too small.</summary>
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _BytesLabelCharCount(int byteCount) =>
        1 + _UInt64DigitCount((ulong)(uint)byteCount) + 7;

    /// <summary>
    /// Character count of <c>Name (value)</c>.
    /// Name length plus 3 plus at most 20 digits cannot overflow a 32-bit signed length for any allocatable string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _EnumLabelCharCount(ReadOnlySpan<char> name, ulong numericValue) =>
        name.Length + 3 + _UInt64DigitCount(numericValue);

    /// <summary>Narrows already-validated ASCII chars to bytes. Caller guarantees destination is large enough.</summary>
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
    public static implicit operator SettingValue(bool value) => Bool(value);

    /// <summary>Wraps a <see cref="string"/> into a <see cref="SettingValue"/>.</summary>
    public static implicit operator SettingValue(string value) => String(value);

    /// <summary>Wraps a <see cref="double"/> into a <see cref="SettingValue"/>.</summary>
    public static implicit operator SettingValue(double value) => F64(value);

    /// <summary>
    /// Wraps a <see cref="ulong"/> into a <see cref="SettingValue"/>.
    /// Without this overload, literals passed to APIs such as <see cref="SettingsManager.PreloadValue"/>
    /// would match the <see cref="double"/> conversion instead, producing <see cref="SettingType.F64"/>
    /// and failing type validation for <see cref="SettingType.U64"/> settings.
    /// </summary>
    public static implicit operator SettingValue(ulong value) => U64(value);

    /// <summary>Compares two byte arrays for element-wise equality.</summary>
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
