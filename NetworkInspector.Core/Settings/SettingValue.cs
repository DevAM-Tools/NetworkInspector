// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Represents a setting value that can be one of several types.
/// Uses a tagged union layout to avoid boxing for value types.
///
/// NaN-safe f64 comparison is implemented to prevent infinite dirty-tracking loops.
/// Two <c>F64(NaN)</c> values are considered equal.
/// </summary>
public readonly struct SettingValue : IEquatable<SettingValue>, ISpanFormattable, IUtf8SpanFormattable
{
    // Storage layout:
    // Bool:   _Bits = 0 or 1, _ReferenceValue = null
    // F64:    _Bits = BitConverter bits, _ReferenceValue = null
    // U64:    _Bits = (long)value, _ReferenceValue = null
    // I64:    _Bits = value, _ReferenceValue = null
    // String: _Bits = 0, _ReferenceValue = string
    // Bytes:  _Bits = 0, _ReferenceValue = byte[]
    // Enum:   _Bits = numeric value, _ReferenceValue = string (name)

    private readonly SettingType _Type;
    private readonly long _Bits;
    private readonly object? _ReferenceValue;

    private SettingValue(SettingType type, long bits, object? referenceValue)
    {
        _Type = type;
        _Bits = bits;
        _ReferenceValue = referenceValue;
    }

    /// <summary>Returns the type discriminant for this value.</summary>
    public SettingType Type => _Type;

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

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation

    /// <summary>
    /// Returns <see langword="true"/> if this value is <see cref="SettingType.Bool"/> and
    /// writes the boolean into <paramref name="value"/>; otherwise sets <paramref name="value"/>
    /// to <see langword="false"/> and returns <see langword="false"/>.
    /// </summary>
    public bool TryGetAsBool(out bool value)
    {
        if (_Type == SettingType.Bool)
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
        if (_Type == SettingType.String && _ReferenceValue is string s)
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
        if (_Type == SettingType.F64)
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
        if (_Type == SettingType.U64)
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
        if (_Type == SettingType.I64)
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
    /// otherwise sets <paramref name="value"/> to <see langword="null"/> and returns
    /// <see langword="false"/>. The copy prevents callers from mutating internal state.
    /// </summary>
    public bool TryGetAsBytes(out byte[] value)
    {
        if (_Type == SettingType.Bytes && _ReferenceValue is byte[] stored)
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
        if (_Type == SettingType.Enum && _ReferenceValue is string name)
        {
            value = (name, (ulong)_Bits);
            return true;
        }
        value = default;
        return false;
    }

    #endregion

    #region Equality

    /// <inheritdoc/>
    public bool Equals(SettingValue other)
    {
        if (_Type != other._Type)
        {
            return false;
        }

        return _Type switch
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
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is SettingValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        _Type switch
        {
            SettingType.Bool => HashCode.Combine(_Type, _Bits),
            SettingType.F64 => HashCode.Combine(_Type, _Bits),
            SettingType.U64 => HashCode.Combine(_Type, _Bits),
            SettingType.I64 => HashCode.Combine(_Type, _Bits),
            SettingType.String => HashCode.Combine(_Type, _ReferenceValue),
            SettingType.Bytes => _ContentHashBytes((byte[]?)_ReferenceValue),
            SettingType.Enum => HashCode.Combine(_Type, _Bits, _ReferenceValue),
            _ => HashCode.Combine(_Type),
        };

    /// <summary>Returns <see langword="true"/> if both values are equal.</summary>
    public static bool operator ==(SettingValue left, SettingValue right) => left.Equals(right);

    /// <summary>Returns <see langword="true"/> if the values are not equal.</summary>
    public static bool operator !=(SettingValue left, SettingValue right) => !left.Equals(right);

    #endregion

    #region ISpanFormattable

    /// <inheritdoc/>
    public bool TryFormat(
        Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        switch (_Type)
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
            default:
                charsWritten = 0;
                return true;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        if (_Type == SettingType.String && _ReferenceValue is string str)
        {
            return str;
        }

        Span<char> buffer = stackalloc char[256];
        if (TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture))
        {
            return buffer[..written].ToString();
        }

        return "";
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (_Type == SettingType.String && _ReferenceValue is string str)
        {
            return str;
        }

        Span<char> buffer = stackalloc char[256];
        if (TryFormat(buffer, out int written, format, formatProvider ?? CultureInfo.InvariantCulture))
        {
            return buffer[..written].ToString();
        }

        return "";
    }

    #endregion

    #region IUtf8SpanFormattable

    /// <inheritdoc/>
    public bool TryFormat(
        Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_Type == SettingType.String && _ReferenceValue is string str)
        {
            int byteCount = Encoding.UTF8.GetByteCount(str);
            if (utf8Destination.Length < byteCount)
            {
                bytesWritten = 0;
                return false;
            }
            bytesWritten = Encoding.UTF8.GetBytes(str, utf8Destination);
            return true;
        }

        Span<char> chars = stackalloc char[256];
        if (!TryFormat(chars, out int charCount, format, provider))
        {
            bytesWritten = 0;
            return false;
        }

        if (utf8Destination.Length < charCount)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = Encoding.UTF8.GetBytes(chars[..charCount], utf8Destination);
        return true;
    }

    #endregion

    #region Formatting helpers

    private static bool _TryFormatBytesLabel(int byteCount, Span<char> destination, out int charsWritten)
    {
        if (destination.IsEmpty)
        {
            charsWritten = 0;
            return false;
        }

        destination[0] = '[';
        if (!byteCount.TryFormat(destination[1..], out int digitCount, default, CultureInfo.InvariantCulture))
        {
            charsWritten = 0;
            return false;
        }

        ReadOnlySpan<char> suffix = " bytes]";
        int total = 1 + digitCount + suffix.Length;
        if (destination.Length < total)
        {
            charsWritten = 0;
            return false;
        }

        suffix.CopyTo(destination[(1 + digitCount)..]);
        charsWritten = total;
        return true;
    }

    private static bool _TryFormatEnumLabel(
        ReadOnlySpan<char> name, ulong numericValue, Span<char> destination, out int charsWritten)
    {
        int pos = 0;
        if (destination.Length < name.Length)
        {
            charsWritten = 0;
            return false;
        }

        name.CopyTo(destination);
        pos = name.Length;

        if (destination.Length < pos + 2)
        {
            charsWritten = 0;
            return false;
        }

        destination[pos++] = ' ';
        destination[pos++] = '(';

        if (!numericValue.TryFormat(destination[pos..], out int digitCount, default, CultureInfo.InvariantCulture))
        {
            charsWritten = 0;
            return false;
        }

        pos += digitCount;
        if (destination.Length < pos + 1)
        {
            charsWritten = 0;
            return false;
        }

        destination[pos++] = ')';
        charsWritten = pos;
        return true;
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
    #endregion
}
