// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Wraps <see cref="FieldValueData"/> with an optional custom display representation.
/// <para>
/// The data decides everything semantic (equality, ordering, hashing); the custom representation only changes
/// how the value is shown or formatted (for example a flag value displayed as its name). Keeping them apart means
/// attaching nicer display text can never change how values compare.
/// </para>
/// <para>
/// <b>Thread-safety:</b> an immutable value type that may be shared across threads. A lazily evaluated custom text
/// builds its string once per deferred object and caches it inside its own storage location (see <see cref="LazyString"/>);
/// the location of the custom representation is the field of this struct. Evaluate it through a reference to the stored value
/// (<c>ref readonly FieldValue value = ref field.Value;</c>) so the stored value drops the deferred object; a copy of this
/// struct carries its own copy of the representation, shares the one build, and keeps holding the deferred object.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct FieldValue : IEquatable<FieldValue>, IComparable<FieldValue>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>None value constant (container fields).</summary>
    public static readonly FieldValue None;

    /// <summary>
    /// The inner value data. Its <see cref="FieldValueData.ToString()"/> and <c>TryFormat</c> give the default text of the data,
    /// ignoring <see cref="CustomRepresentation"/>: the part for a caller that builds its own line from the parts of a field (see
    /// <see cref="Field.ToString()"/> for the default line).
    /// </summary>
    public FieldValueData Data { get; }

    /// <summary>
    /// The custom representation, or the default (null) <see cref="LazyString"/> for none. It is also the location in which a
    /// deferred representation caches its string (see <see cref="LazyString"/>): this field of whichever
    /// <see cref="FieldValue"/> instance is read through a reference.
    /// </summary>
    private readonly LazyString _CustomRepresentation;

    /// <summary>
    /// Optional custom display representation (check <see cref="LazyString.IsNull"/> for absence), as a reference to the
    /// storage of this value. Reading the property never evaluates a deferred text.
    /// <para>
    /// The string is built once however it is read (see <see cref="LazyString"/>). Evaluating through the reference
    /// (<c>value.CustomRepresentation.AsString</c>) additionally replaces the deferred object in the value that was read; when
    /// that value is read through a reference to its stored location (a field, a visit, an <see langword="in"/> parameter),
    /// the stored value drops the object and its captured state. Assigning it to a local
    /// (<c>LazyString text = value.CustomRepresentation;</c>) shares the build but keeps holding the object.
    /// </para>
    /// </summary>
    public ref readonly LazyString CustomRepresentation
    {
        // UnscopedRef: a struct member returns a reference to its own field only when it opts out of the implicit
        // "this is scoped" rule (CS8170); the reference is exactly as long-lived as the location of the FieldValue read.
        [UnscopedRef]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _CustomRepresentation;
    }

    /// <summary>Creates a field value from raw data and optional custom representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FieldValue(FieldValueData data, LazyString customRepresentation)
    {
        Data = data;
        _CustomRepresentation = customRepresentation;
    }

    /// <summary>The field type discriminant.</summary>
    public FieldType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data.Type;
    }

    /// <summary>Creates a new FieldValue with a custom representation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldValue WithCustomRepresentation(LazyString text) => new(Data, text);

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
    /// <param name="value">String payload. The reference is stored as-is (no copy); <see langword="null"/> is stored as the empty string.</param>
    /// <param name="custom">Optional custom display text. Pass <see langword="default"/> to use the type's default formatting.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValue NewString(string value, LazyString custom = default) => new(FieldValueData.NewString(value), custom);
    /// <summary>Creates a <see cref="FieldType.Bytes"/> field value referencing the given byte memory and an optional custom display representation.</summary>
    /// <param name="value">
    /// Byte memory payload. Array-backed memory is referenced without copying, so the caller must not mutate the
    /// underlying array while the value is in use; memory that is not array-backed is copied.
    /// </param>
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

    // Equality, hashing, and ordering use the data only: two values that differ only in their display text are the same
    // value, so equal values always hash equally whatever text they are shown with.

    /// <inheritdoc/>
    public bool Equals(FieldValue other) => Data.Equals(other.Data);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FieldValue other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => Data.GetHashCode();

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
    public int CompareTo(FieldValue other) => Data.CompareTo(other.Data);

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    public static bool operator <(FieldValue left, FieldValue right) => left.CompareTo(right) < 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    public static bool operator >(FieldValue left, FieldValue right) => left.CompareTo(right) > 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(FieldValue left, FieldValue right) => left.CompareTo(right) <= 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(FieldValue left, FieldValue right) => left.CompareTo(right) >= 0;

    #endregion

    #region ISpanFormattable

    /// <summary>
    /// Formats the value into a character span: the default string representation of the value.
    /// Uses <see cref="CustomRepresentation"/> if present, otherwise formats the raw data. It is the value part of the default
    /// display line of a field (see <see cref="Field.ToString()"/>).
    /// Expected failure (span too small) is reported by the return value, not by an exception.
    /// </summary>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Format string forwarded to the data formatter; ignored for a custom representation.</param>
    /// <param name="provider">Format provider forwarded to the data formatter; ignored for a custom representation.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!CustomRepresentation.IsNull)
        {
            ReadOnlySpan<char> text = CustomRepresentation.AsSpan;
            if (destination.Length < text.Length)
            {
                charsWritten = 0;
                return false;
            }
            text.CopyTo(destination);
            charsWritten = text.Length;
            return true;
        }
        return Data.TryFormat(destination, out charsWritten, format, provider);
    }

    /// <summary>
    /// Formats the value into a UTF-8 byte span.
    /// Uses <see cref="CustomRepresentation"/> if present, otherwise formats the raw data.
    /// A custom representation is transcoded; its UTF-8 length is measured first so nothing is written when it does not fit.
    /// </summary>
    /// <param name="utf8Destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Format string forwarded to the data formatter; ignored for a custom representation.</param>
    /// <param name="provider">Format provider forwarded to the data formatter; ignored for a custom representation.</param>
    /// <returns><see langword="false"/> when <paramref name="utf8Destination"/> is too small.</returns>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!CustomRepresentation.IsNull)
        {
            ReadOnlySpan<char> text = CustomRepresentation.AsSpan;
            int byteCount = Encoding.UTF8.GetByteCount(text);
            if (utf8Destination.Length < byteCount)
            {
                bytesWritten = 0;
                return false;
            }
            bytesWritten = Encoding.UTF8.GetBytes(text, utf8Destination);
            return true;
        }
        return Data.TryFormat(utf8Destination, out bytesWritten, format, provider);
    }

    /// <summary>
    /// Returns the default string representation of the value: the custom representation if present, otherwise the formatted
    /// data value. It is the value part of the default display line of a field (see <see cref="Field.ToString()"/>).
    /// </summary>
    public override string ToString()
    {
        if (!CustomRepresentation.IsNull)
        {
            return CustomRepresentation.AsString;
        }
        return Data.ToString();
    }

    /// <summary>Returns the custom representation if present, otherwise the formatted data value.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    #endregion

    #region String Size

    /// <summary>
    /// Tries to determine the number of characters needed to format this value.
    /// When a custom representation is present, returns its exact length.
    /// Otherwise delegates to the underlying <see cref="FieldValueData"/>. A caller that presizes the buffer for
    /// <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/> of this value uses it; it is a plain
    /// method (no interface) because every caller names the concrete type. The exporters format the data text, so they presize
    /// with <see cref="FieldValueData.TryGetStringSize"/>.
    /// </summary>
    public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        if (!CustomRepresentation.IsNull)
        {
            size = CustomRepresentation.AsSpan.Length;
            return true;
        }
        return Data.TryGetStringSize(format, provider, out size);
    }

    #endregion

    #region Implicit operators

    // Integral conversions keep the signedness of the source type: signed sources produce I64, unsigned sources U64
    // (narrow types are widened first). So the field type, and therefore comparison and display behavior, follows the
    // C# type the protocol author passes in. Floating point maps to F64. Null strings and byte arrays map to None.

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
    public static implicit operator FieldValue(string? value)
    {
        if (value is null)
        {
            return None;
        }
        return NewString(value);
    }
    /// <summary>Creates a Bytes field value.</summary>
    public static implicit operator FieldValue(ReadOnlyMemory<byte> value) => NewBytes(value);
    /// <summary>Creates a Bytes field value from a byte array (null produces None).</summary>
    public static implicit operator FieldValue(byte[]? value)
    {
        if (value is null)
        {
            return None;
        }
        return NewBytes(value);
    }
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
}
