// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Represents a setting value that can be one of several types.
/// Uses a tagged union layout to avoid boxing for value types.
///
/// NaN-safe f64 comparison is implemented to prevent infinite dirty-tracking loops.
/// Two <c>F64(NaN)</c> values are considered equal.
/// </summary>
public readonly struct SettingValue : IEquatable<SettingValue>, ISpanFormattable, IUtf8SpanFormattable, IStringSize
{
    // Storage layout:
    // Bool:   _Bits = 0 or 1, _ReferenceValue = null
    // F64:    _Bits = BitConverter bits, _ReferenceValue = null
    // U64:    _Bits = (long)value, _ReferenceValue = null
    // I64:    _Bits = value, _ReferenceValue = null
    // String: _Bits = 0, _ReferenceValue = string
    // Bytes:  _Bits = 0, _ReferenceValue = byte[]
    // Enum:   _Bits = numeric value, _ReferenceValue = string (name)

    /// <summary>Returns the type discriminant for this value.</summary>
    public SettingType Type { get; }

    private readonly long _Bits;
    private readonly object? _ReferenceValue;

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

    #endregion

    #region Equality

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
            _ => HashCode.Combine(Type),
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
            default:
                bytesWritten = 0;
                return true;
        }
    }

    #endregion

    #region IStringSize

    /// <summary>
    /// Tries to determine the number of characters needed to format this value.
    /// Returns <see langword="true"/> with an exact count for Bool, String, Bytes, Enum, and
    /// default-format integers. Returns <see langword="false"/> for <see cref="SettingType.F64"/>
    /// and for integers when a non-empty format string is supplied, because those lengths depend
    /// on the value and format.
    /// </summary>
    public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
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

    /// <summary>Stack threshold before switching to a ZeroAlloc-managed buffer.</summary>
    private const int _StackFormatLimit = 256;

    /// <summary>Writes the formatted label into <paramref name="destination"/>. Returns characters written (0 if the span is too small).</summary>
    public int FormatInto(Span<char> destination)
    {
        TryFormat(destination, out int written, default, CultureInfo.InvariantCulture);
        return written;
    }

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static or pooled buffer.</summary>
    /// <remarks>
    /// The underlying buffer is thread-local or pooled. The caller must dispose the returned <see cref="TempString"/>
    /// before making another <see cref="FormatTemp"/> call on the same thread to avoid overwriting the buffer.
    /// Do not retain references to the underlying span after disposal.
    /// </remarks>
    public TempString FormatTemp()
    {
        if (Type == SettingType.String && _ReferenceValue is string str)
        {
            if (str.Length == 0)
            {
                return new TempString([], 0, false);
            }

            char[] stringBuffer = ZeroAllocHelper.AcquireCharBuffer(str.Length, out bool stringThreadStatic);
            str.CopyTo(stringBuffer);
            return new TempString(stringBuffer, str.Length, stringThreadStatic);
        }

        int size;
        if (!TryGetStringSize(default, CultureInfo.InvariantCulture, out size))
        {
            size = _F64DefaultMaxChars;
        }

        if (size <= 0)
        {
            return new TempString([], 0, false);
        }

        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(size, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the formatted label as a new string.</summary>
    /// <remarks>
    /// String values return the stored instance. Other types allocate a new string.
    /// Use <see cref="FormatInto"/> or <see cref="FormatTemp"/> for allocation-free hot paths.
    /// </remarks>
    public string Format()
    {
        if (Type == SettingType.String && _ReferenceValue is string str)
        {
            return str;
        }

        if (TryGetStringSize(default, CultureInfo.InvariantCulture, out int size) && size <= _StackFormatLimit)
        {
            if (size == 0)
            {
                return "";
            }

            Span<char> stack = stackalloc char[size];
            TryFormat(stack, out int written, default, CultureInfo.InvariantCulture);
            return new string(stack[..written]);
        }

        using TempString temp = FormatTemp();
        return temp.ToString();
    }

    /// <summary>Formats with a non-empty format string into a sized buffer, then allocates the result string.</summary>
    private string _FormatToString(ReadOnlySpan<char> format, IFormatProvider provider)
    {
        if (TryGetStringSize(format, provider, out int size))
        {
            return _FormatKnownSizeToString(format, provider, size);
        }

        Span<char> stack = stackalloc char[_StackFormatLimit];
        if (TryFormat(stack, out int written, format, provider))
        {
            return new string(stack[..written]);
        }

        string formatString = format.ToString();
        if (Type == SettingType.F64)
        {
            return BitConverter.Int64BitsToDouble(_Bits).ToString(formatString, provider);
        }

        if (Type == SettingType.U64)
        {
            return ((ulong)_Bits).ToString(formatString, provider);
        }

        return _Bits.ToString(formatString, provider);
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

        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(size, out bool isThreadStatic);
        TryFormat(buffer, out int writtenLarge, format, provider);
        using TempString temp = new(buffer, writtenLarge, isThreadStatic);
        return temp.ToString();
    }

    #endregion

    #region Formatting helpers

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

    private static bool _TryFormatEnumLabelUtf8(
        ReadOnlySpan<char> name, ulong numericValue, Span<byte> destination, out int bytesWritten)
    {
        int digits = _UInt64DigitCount(numericValue);
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
    #endregion
}
