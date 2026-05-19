// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Wraps <see cref="FieldValueData"/> with an optional custom display representation.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct FieldValue : IEquatable<FieldValue>, IComparable<FieldValue>, ISpanFormattable, IUtf8SpanFormattable, IStringSize
{
    /// <summary>None value constant (container fields).</summary>
    public static readonly FieldValue None = default;

    private readonly FieldValueData _Data;
    private readonly LazyString _CustomRepresentation;

    /// <summary>Creates a field value from raw data and optional custom representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FieldValue(FieldValueData data, LazyString customRepresentation)
    {
        _Data = data;
        _CustomRepresentation = customRepresentation;
    }

    /// <summary>The inner value data.</summary>
    public FieldValueData Data
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Data;
    }
    /// <summary>The field type discriminant.</summary>
    public FieldType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Data.Type;
    }
    /// <summary>Optional custom display representation (check <see cref="LazyString.IsNull"/> for absence).</summary>
    public LazyString CustomRepresentation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _CustomRepresentation;
    }

    /// <summary>
    /// Returns a <see cref="DefaultText"/> wrapper that formats the raw data value,
    /// ignoring the <see cref="CustomRepresentation"/>.
    /// </summary>
    public DefaultText DataText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_Data);
    }

    /// <summary>Creates a new FieldValue with a custom representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldValue WithCustomRepresentation(LazyString text) => new(_Data, text);

    #region Factory methods

    /// <summary>Creates a <see cref="FieldType.Bool"/> field value with an optional custom display representation.</summary>
    /// <param name="value">Boolean payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewBool(bool value, LazyString custom = default) => new(FieldValueData.NewBool(value), custom);
    /// <summary>Creates a <see cref="FieldType.I64"/> (signed 64-bit integer) field value with an optional custom display representation.</summary>
    /// <param name="value">Signed 64-bit integer payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewI64(long value, LazyString custom = default) => new(FieldValueData.NewI64(value), custom);
    /// <summary>Creates a <see cref="FieldType.U64"/> (unsigned 64-bit integer) field value with an optional custom display representation.</summary>
    /// <param name="value">Unsigned 64-bit integer payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewU64(ulong value, LazyString custom = default) => new(FieldValueData.NewU64(value), custom);
    /// <summary>Creates a <see cref="FieldType.F64"/> (IEEE-754 double-precision) field value with an optional custom display representation.</summary>
    /// <param name="value">Double-precision floating-point payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewF64(double value, LazyString custom = default) => new(FieldValueData.NewF64(value), custom);
    /// <summary>Creates a <see cref="FieldType.String"/> field value with an optional custom display representation.</summary>
    /// <param name="value">String payload. The reference is stored as-is; the caller must not mutate it after the call.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewString(string value, LazyString custom = default) => new(FieldValueData.NewString(value), custom);
    /// <summary>Creates a <see cref="FieldType.Bytes"/> field value referencing the given byte memory and an optional custom display representation.</summary>
    /// <param name="value">Byte memory payload. The memory is stored by reference; the caller must not mutate the underlying buffer afterwards.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewBytes(ReadOnlyMemory<byte> value, LazyString custom = default) => new(FieldValueData.NewBytes(value), custom);
    /// <summary>Creates a <see cref="FieldType.MacAddress"/> field value with an optional custom display representation.</summary>
    /// <param name="value">48-bit MAC address payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewMacAddress(MacAddress value, LazyString custom = default) => new(FieldValueData.NewMacAddress(value), custom);
    /// <summary>Creates a <see cref="FieldType.IPv4Address"/> field value with an optional custom display representation.</summary>
    /// <param name="value">IPv4 address payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewIPv4(IPv4Address value, LazyString custom = default) => new(FieldValueData.NewIPv4(value), custom);
    /// <summary>Creates a <see cref="FieldType.IPv6Address"/> field value with an optional custom display representation.</summary>
    /// <param name="value">IPv6 address payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewIPv6(IPv6Address value, LazyString custom = default) => new(FieldValueData.NewIPv6(value), custom);
    /// <summary>Creates a <see cref="FieldType.Eui64"/> field value with an optional custom display representation.</summary>
    /// <param name="value">64-bit EUI-64 identifier payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewEui64(Eui64 value, LazyString custom = default) => new(FieldValueData.NewEui64(value), custom);
    /// <summary>Creates a <see cref="FieldType.Uuid"/> field value with an optional custom display representation.</summary>
    /// <param name="value">128-bit UUID payload.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewUuid(Values.Uuid value, LazyString custom = default) => new(FieldValueData.NewUuid(value), custom);
    /// <summary>Creates a <see cref="FieldType.Timestamp"/> field value with an optional custom display representation.</summary>
    /// <param name="value">Timestamp payload (high-resolution, time-zone-agnostic).</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewTimestamp(Timestamp value, LazyString custom = default) => new(FieldValueData.NewTimestamp(value), custom);

    /// <summary>
    /// Creates a <see cref="FieldType.String"/> field value whose content is deferred until
    /// first access via <see cref="FieldValueData.TryGetAsString"/> or <see cref="ToString()"/>.
    /// The <see cref="LazyString"/> wrapper holds the factory on the heap, so the
    /// evaluated result is cached in-place and shared across all copies of this
    /// <see cref="FieldValue"/>.
    /// </summary>
    internal static FieldValue NewLazyString(LazyString lazy) => new(FieldValueData.NewLazyString(lazy), default);

    #endregion

    #region Equality (ignores custom representation)

    /// <inheritdoc/>
    public bool Equals(FieldValue other) => _Data.Equals(other._Data);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FieldValue other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => _Data.GetHashCode();

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/>
    /// are equal (data only; custom representation ignored).</summary>
    public static bool operator ==(FieldValue left, FieldValue right) => left.Equals(right);
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/>
    /// are not equal (data only; custom representation ignored).</summary>
    public static bool operator !=(FieldValue left, FieldValue right) => !left.Equals(right);

    #endregion

    #region IComparable<FieldValue> (ignores custom representation)

    /// <summary>
    /// Compares by data only, ignoring custom representation.
    /// Supports cross-type numeric comparison (I64 vs U64 vs F64).
    /// </summary>
    public int CompareTo(FieldValue other) => _Data.CompareTo(other._Data);

    #endregion

    #region ISpanFormattable

    /// <summary>
    /// Formats the value into a character span.
    /// Uses <see cref="CustomRepresentation"/> if present, otherwise formats the raw data.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!_CustomRepresentation.IsNull)
        {
            ReadOnlySpan<char> text = _CustomRepresentation.AsSpan;
            if (destination.Length < text.Length)
            {
                charsWritten = 0;
                return false;
            }
            text.CopyTo(destination);
            charsWritten = text.Length;
            return true;
        }
        return _Data.TryFormat(destination, out charsWritten, format, provider);
    }

    /// <summary>
    /// Formats the value into a UTF-8 byte span.
    /// Uses <see cref="CustomRepresentation"/> if present, otherwise formats the raw data.
    /// </summary>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!_CustomRepresentation.IsNull)
        {
            ReadOnlySpan<char> text = _CustomRepresentation.AsSpan;
            int byteCount = Encoding.UTF8.GetByteCount(text);
            if (utf8Destination.Length < byteCount)
            {
                bytesWritten = 0;
                return false;
            }
            bytesWritten = Encoding.UTF8.GetBytes(text, utf8Destination);
            return true;
        }
        return _Data.TryFormat(utf8Destination, out bytesWritten, format, provider);
    }

    /// <summary>Returns the custom representation if present, otherwise the formatted data value.</summary>
    public override string ToString() =>
        !_CustomRepresentation.IsNull ? _CustomRepresentation.AsString : _Data.ToString();

    /// <summary>Returns the custom representation if present, otherwise the formatted data value.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    #endregion

    #region IStringSize

    /// <summary>
    /// Tries to determine the number of characters needed to format this value.
    /// When a custom representation is present, returns its exact length.
    /// Otherwise delegates to the underlying <see cref="FieldValueData"/>.
    /// </summary>
    public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        if (!_CustomRepresentation.IsNull)
        {
            size = _CustomRepresentation.AsSpan.Length;
            return true;
        }
        return _Data.TryGetStringSize(format, provider, out size);
    }

    #endregion

    #region Zero-allocation formatting

    /// <summary>
    /// Returns a <see cref="TempString"/> containing the formatted value.
    /// Uses the custom representation if present, otherwise formats the raw data.
    /// When a thread-static buffer is available and the value fits, no heap allocation occurs.
    /// The caller is responsible for disposing the returned <see cref="TempString"/>.
    /// </summary>
    public TempString ToTempString()
    {
        if (!_CustomRepresentation.IsNull)
        {
            ReadOnlySpan<char> text = _CustomRepresentation.AsSpan;
            char[] buffer = ZeroAllocHelper.AcquireCharBuffer(text.Length, out bool isThreadStatic);
            text.CopyTo(buffer);
            return new TempString(buffer, text.Length, isThreadStatic);
        }
        return _Data.ToTempString();
    }

    #endregion

    #region Implicit operators

    /// <summary>Creates a Bool field value.</summary>
    public static implicit operator FieldValue(bool value) => NewBool(value);
    /// <summary>Creates an I64 field value from a signed 64-bit integer.</summary>
    public static implicit operator FieldValue(long value) => NewI64(value);
    /// <summary>Creates a U64 field value from an unsigned 64-bit integer.</summary>
    public static implicit operator FieldValue(ulong value) => NewU64(value);
    /// <summary>Creates an I64 field value from a signed 32-bit integer.</summary>
    public static implicit operator FieldValue(int value) => NewI64(value);
    /// <summary>Creates a U64 field value from an unsigned 32-bit integer.</summary>
    public static implicit operator FieldValue(uint value) => NewU64(value);
    /// <summary>Creates an I64 field value from a signed 16-bit integer.</summary>
    public static implicit operator FieldValue(short value) => NewI64(value);
    /// <summary>Creates a U64 field value from an unsigned 16-bit integer.</summary>
    public static implicit operator FieldValue(ushort value) => NewU64(value);
    /// <summary>Creates an I64 field value from a signed byte.</summary>
    public static implicit operator FieldValue(sbyte value) => NewI64(value);
    /// <summary>Creates a U64 field value from an unsigned byte.</summary>
    public static implicit operator FieldValue(byte value) => NewU64(value);
    /// <summary>Creates an F64 field value.</summary>
    public static implicit operator FieldValue(double value) => NewF64(value);
    /// <summary>Creates a String field value (null produces None).</summary>
    public static implicit operator FieldValue(string? value) =>
        value is null ? None : NewString(value);
    /// <summary>Creates a Bytes field value.</summary>
    public static implicit operator FieldValue(ReadOnlyMemory<byte> value) => NewBytes(value);
    /// <summary>Creates a Bytes field value from a byte array (null produces None).</summary>
    public static implicit operator FieldValue(byte[]? value) =>
        value is null ? None : NewBytes(value);
    /// <summary>Creates a MacAddress field value.</summary>
    public static implicit operator FieldValue(MacAddress value) => NewMacAddress(value);
    /// <summary>Creates an IPv4Address field value.</summary>
    public static implicit operator FieldValue(IPv4Address value) => NewIPv4(value);
    /// <summary>Creates an IPv6Address field value.</summary>
    public static implicit operator FieldValue(IPv6Address value) => NewIPv6(value);
    /// <summary>Creates an Eui64 field value.</summary>
    public static implicit operator FieldValue(Eui64 value) => NewEui64(value);
    /// <summary>Creates a Uuid field value.</summary>
    public static implicit operator FieldValue(Values.Uuid value) => NewUuid(value);
    /// <summary>Creates a Timestamp field value.</summary>
    public static implicit operator FieldValue(Timestamp value) => NewTimestamp(value);

    #endregion

    #region Nested DefaultText

    /// <summary>
    /// Lightweight wrapper that formats the raw <see cref="FieldValueData"/>
    /// ignoring any <see cref="FieldValue.CustomRepresentation"/>.
    /// Implements <see cref="ISpanFormattable"/> and <see cref="IUtf8SpanFormattable"/>
    /// so it can be used directly in interpolated strings and formatting pipelines.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    [SuppressMessage("Design", "CA1034:Nested types should not be visible",
        Justification = "DefaultText is a lightweight formatting wrapper tightly coupled to FieldValue")]
    public readonly struct DefaultText(FieldValueData data) : ISpanFormattable, IUtf8SpanFormattable, IStringSize
    {
        private readonly FieldValueData _Data = data;

        /// <summary>Formats the raw data value into a character span.</summary>
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => _Data.TryFormat(destination, out charsWritten, format, provider);

        /// <summary>Formats the raw data value into a UTF-8 byte span.</summary>
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => _Data.TryFormat(utf8Destination, out bytesWritten, format, provider);

        /// <summary>Returns the formatted string of the raw data value.</summary>
        public override string ToString() => _Data.ToString();

        /// <summary>Returns the formatted string of the raw data value.</summary>
        public string ToString(string? format, IFormatProvider? formatProvider) => _Data.ToString();

        /// <inheritdoc cref="FieldValueData.TryGetStringSize"/>
        public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
            => _Data.TryGetStringSize(format, provider, out size);

        /// <summary>
        /// Returns a <see cref="TempString"/> containing the formatted raw data value.
        /// No heap allocation occurs when a thread-static buffer is available.
        /// The caller is responsible for disposing the returned <see cref="TempString"/>.
        /// </summary>
        public TempString ToTempString() => _Data.ToTempString();

        /// <summary>Implicit conversion to string for convenience.</summary>
        public static implicit operator string(DefaultText text) => text.ToString();
    }
    #endregion
}