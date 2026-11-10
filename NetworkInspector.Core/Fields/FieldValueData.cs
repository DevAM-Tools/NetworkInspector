// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Tagged union holding one of 13 field value types in 24 bytes.
/// <para>
/// <b>Layout (x64):</b><br/>
/// <c>_Data</c> (8 bytes) — inline value storage for numeric and address types, or packed
/// offset|length for Bytes (offset in the low 32 bits, length in the high 32 bits). For 128-bit types
/// (IPv6, Uuid) stores the high 64 bits.<br/>
/// <c>_Data1</c> (8 bytes) — second inline value for 128-bit types (low 64 bits). Zero for
/// all other types.<br/>
/// <c>_Ref</c> (8 bytes) — type discriminant via static marker singletons, or payload carrier:
/// <list type="bullet">
///   <item><c>null</c> → <see cref="FieldType.None"/></item>
///   <item>Static marker singleton (e.g. <see cref="I64Marker"/>) → inline type, value in <c>_Data</c></item>
///   <item><c>string</c> or boxed <see cref="LazyString"/> → <see cref="FieldType.String"/></item>
///   <item><c>byte[]</c> → <see cref="FieldType.Bytes"/></item>
///   <item><see cref="IPv6AddressMarker"/> → 128-bit IPv6 address inline in <c>_Data</c>/<c>_Data1</c></item>
///   <item><see cref="UuidMarker"/> → 128-bit UUID inline in <c>_Data</c>/<c>_Data1</c></item>
/// </list>
/// </para>
/// <para>
/// Values are accessed exclusively through <c>TryGetAs*</c> methods which combine type
/// checking and value extraction in a single operation.
/// </para>
/// <para>
/// <b>Why this shape:</b> a field value is created for every produced field of every parsed frame, so the struct is
/// kept small and free of per-value heap objects. A stored <see cref="FieldType"/> byte next to the three 8-byte
/// words would pad the struct to 32 bytes; deriving the type from the identity of <c>_Ref</c> keeps it at 24 bytes.
/// Scalars, addresses and timestamps (including the 128-bit ones) therefore never allocate, because the marker
/// objects are shared statics. Only String and Bytes carry a heap payload.
/// </para>
/// <para>
/// <b>Aliasing:</b> a Bytes value references the caller's array without copying (see <c>NewBytes</c>). The array
/// must not be mutated while the value is in use.
/// </para>
/// <para><b>Thread-safety:</b> an immutable value that may be shared across threads. A lazy string payload caches its
/// result with a compare-exchange inside the shared box; concurrent first readers may each run the factory, one
/// result wins and all readers see that same string (see <c>NewLazyString</c>).</para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct FieldValueData : IEquatable<FieldValueData>, IComparable<FieldValueData>, ISpanFormattable, IUtf8SpanFormattable
{
    #region Storage and construction

    /// <summary>
    /// Inline value storage: the raw bits of a numeric, address, or timestamp value, the packed offset and length of a Bytes
    /// value, or the high 64 bits of a 128-bit value. 0 for a String and for None. Three fields of 8 bytes make 24 bytes on x64.
    /// </summary>
    private readonly ulong _Data;

    /// <summary>The low 64 bits of a 128-bit value (IPv6Address, Uuid); 0 for every other type.</summary>
    private readonly ulong _Data1;

    /// <summary>The type discriminant or payload: null (None), a marker singleton, a string, a boxed <see cref="LazyString"/>, or a byte array.</summary>
    private readonly object? _Ref;

    /// <summary>The default (empty / unset) field value. Represents <see cref="FieldType.None"/>.</summary>
    public static readonly FieldValueData None;

    /// <summary>Creates field value data with inline storage and a marker/payload reference.</summary>
    /// <param name="data">The inline value bits (see <c>_Data</c>).</param>
    /// <param name="reference">The marker singleton or payload that also decides the type; null for None.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FieldValueData(ulong data, object? reference)
    {
        _Data = data;
        _Data1 = 0;
        _Ref = reference;
    }

    /// <summary>Creates field value data with dual inline storage for 128-bit types.</summary>
    /// <param name="data">The high 64 bits.</param>
    /// <param name="data1">The low 64 bits.</param>
    /// <param name="reference">The marker singleton of the 128-bit type.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FieldValueData(ulong data, ulong data1, object? reference)
    {
        _Data = data;
        _Data1 = data1;
        _Ref = reference;
    }

    #endregion

    #region Creating values

    /// <summary>Creates a <see cref="FieldType.Bool"/> field value.</summary>
    /// <param name="value">Boolean payload.</param>
    /// <returns>A <see cref="FieldType.Bool"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewBool(bool value) => new(
        value
            ? 1UL
            : 0UL,
        BoolMarker.Instance);

    /// <summary>Creates a <see cref="FieldType.I64"/> (signed 64-bit integer) field value.</summary>
    /// <param name="value">Signed 64-bit integer payload.</param>
    /// <returns>An <see cref="FieldType.I64"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewI64(long value) => new((ulong)value, I64Marker.Instance);

    /// <summary>Creates a <see cref="FieldType.U64"/> (unsigned 64-bit integer) field value.</summary>
    /// <param name="value">Unsigned 64-bit integer payload.</param>
    /// <returns>A <see cref="FieldType.U64"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewU64(ulong value) => new(value, U64Marker.Instance);

    /// <summary>Creates a <see cref="FieldType.F64"/> (IEEE-754 double-precision) field value.</summary>
    /// <param name="value">Double-precision payload; stored as its IEEE-754 bit pattern (NaN payloads and the sign of zero survive).</param>
    /// <returns>An <see cref="FieldType.F64"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewF64(double value) => new(BitConverter.DoubleToUInt64Bits(value), F64Marker.Instance);

    /// <summary>
    /// Creates a string field value from a <see cref="string"/>. A <see langword="null"/> argument is stored as the empty
    /// string (no exception) so that a stored String value never has a null payload.
    /// </summary>
    /// <param name="value">String payload; the reference is stored as-is (no copy).</param>
    /// <returns>A <see cref="FieldType.String"/> value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewString(string value) => new(0, value ?? string.Empty);

    /// <summary>
    /// Creates a string field value whose content is deferred until first access via <see cref="TryGetAsString"/>.
    /// </summary>
    /// <param name="lazy">The deferred text; boxed once so every copy of the value shares the cached string.</param>
    /// <returns>A <see cref="FieldType.String"/> value that builds its text on first read.</returns>
    /// <remarks>
    /// <see cref="LazyString"/> is a struct that caches its evaluated result by replacing its own inner reference, and
    /// that cache lives only in the storage location that was evaluated; evaluating a copy does not update the original.
    /// The <see cref="LazyString"/> is therefore boxed once here: every copy of this <see cref="FieldValueData"/> copies only
    /// the reference to the one box, and <see cref="LazyString.AsString"/> is invoked on the box in place via
    /// <see cref="Unsafe.Unbox{T}"/> so the cached string persists for all copies. The naive alternative (unboxing to a
    /// local copy) would re-run the factory on every read. Costs one box allocation per lazy value.
    /// </remarks>
    internal static FieldValueData NewLazyString(LazyString lazy) => new(0, (object)lazy);

    /// <summary>
    /// Creates a string field value from a <see cref="ReadOnlyMemory{Char}"/>.
    /// The memory is materialized into a new string (one allocation and copy), because a string payload must not alias
    /// memory the caller may reuse.
    /// </summary>
    /// <param name="value">The characters to copy; empty gives the empty string.</param>
    /// <returns>A <see cref="FieldType.String"/> value that owns its own copy of the text.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewString(ReadOnlyMemory<char> value) => new(0, new string(value.Span));

    /// <summary>
    /// Creates a <see cref="FieldType.Bytes"/> field value from a <see cref="ReadOnlyMemory{T}"/> of bytes.
    /// Attempts zero-copy via ArraySegment; falls back to a copy if the memory is not array-backed.
    /// In the zero-copy case the value aliases the caller's array, so the array must not be mutated while the value is in use.
    /// </summary>
    /// <param name="value">The bytes to reference; empty is allowed.</param>
    /// <returns>A <see cref="FieldType.Bytes"/> value over the same bytes (array-backed memory) or over a copy.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewBytes(ReadOnlyMemory<byte> value)
    {
        // Zero-copy: keep the backing array and remember the slice as offset (low 32 bits) | length (high 32 bits)
        // in _Data. Rejected alternative: always copy, which would allocate per value on the parse path.
        // Offset and Count are non-negative ints, so the (uint) casts only reinterpret them.
        if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment))
        {
            ulong packed = (ulong)(uint)segment.Offset | ((ulong)(uint)segment.Count << 32);
            return new(packed, segment.Array);
        }

        // Memory that is not array-backed (for example a MemoryManager or native memory) cannot be referenced by
        // a byte[] discriminant, so it is copied. Offset is 0, only the length is packed.
        byte[] copy = value.ToArray();
        ulong packedCopy = (ulong)(uint)copy.Length << 32;
        return new(packedCopy, copy);
    }

    /// <summary>
    /// Creates a bytes field value directly from a <c>byte[]</c> (zero-copy). The array is aliased, not copied,
    /// so it must not be mutated while the value is in use.
    /// </summary>
    /// <param name="value">The array to reference; its whole length is the value.</param>
    /// <returns>A <see cref="FieldType.Bytes"/> value over <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewBytes(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ulong packed = (ulong)(uint)value.Length << 32;
        return new(packed, value);
    }

    /// <summary>Creates a <see cref="FieldType.MacAddress"/> field value.</summary>
    /// <param name="value">The 48-bit address.</param>
    /// <returns>A <see cref="FieldType.MacAddress"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewMacAddress(MacAddress value) => new(value.RawValue, MacAddressMarker.Instance);

    /// <summary>Creates a <see cref="FieldType.IPv4Address"/> field value.</summary>
    /// <param name="value">The address.</param>
    /// <returns>An <see cref="FieldType.IPv4Address"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewIPv4(IPv4Address value) => new(value.RawValue, IPv4AddressMarker.Instance);

    /// <summary>Creates an IPv6 field value. The 128-bit address is stored inline in <c>_Data</c>/<c>_Data1</c>.</summary>
    /// <param name="value">The address.</param>
    /// <returns>An <see cref="FieldType.IPv6Address"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewIPv6(IPv6Address value) => new(value.High, value.Low, IPv6AddressMarker.Instance);

    /// <summary>Creates a <see cref="FieldType.Eui64"/> field value.</summary>
    /// <param name="value">The 64-bit identifier.</param>
    /// <returns>An <see cref="FieldType.Eui64"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewEui64(Eui64 value) => new(value.RawValue, Eui64Marker.Instance);

    /// <summary>Creates a <see cref="FieldType.Uuid"/> field value. The 128-bit value is stored inline in <c>_Data</c>/<c>_Data1</c>.</summary>
    /// <param name="value">The UUID.</param>
    /// <returns>A <see cref="FieldType.Uuid"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewUuid(Values.Uuid value) => new(value.High, value.Low, UuidMarker.Instance);

    /// <summary>Creates a <see cref="FieldType.Timestamp"/> field value.</summary>
    /// <param name="value">The timestamp; stored as its nanosecond count.</param>
    /// <returns>A <see cref="FieldType.Timestamp"/> value holding <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewTimestamp(Timestamp value) => new((ulong)value.AsNanos, TimestampMarker.Instance);

    #endregion

    #region Type discriminant

    /// <summary>
    /// Returns the <see cref="FieldType"/> discriminant by inspecting <c>_Ref</c>.
    /// <para>
    /// Fast path: <c>null</c> → None, marker singletons → identity check.
    /// Slow path: runtime type test for String/Bytes/IPv6/Uuid payloads.
    /// </para>
    /// </summary>
    public FieldType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ClassifyRef(_Ref);
    }

    /// <summary>
    /// Classifies the reference into a <see cref="FieldType"/>.
    /// Marker singletons are checked by identity (ReferenceEquals) for speed.
    /// Payload types use pattern matching on the runtime type.
    /// </summary>
    /// <param name="reference">The <c>_Ref</c> of a value.</param>
    /// <returns>The type that <paramref name="reference"/> encodes; <see cref="FieldType.None"/> for null and for an unknown object.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FieldType _ClassifyRef(object? reference)
    {
        if (reference is null)
        {
            return FieldType.None;
        }

        // Inline value types — identity checks on static singletons (cheapest path).
        // Ordered by expected frequency: U64 and I64 are the most common field types
        // in network protocol parsing (ports, lengths, counters, flags, offsets).
        if (ReferenceEquals(reference, U64Marker.Instance))
        {
            return FieldType.U64;
        }
        if (ReferenceEquals(reference, I64Marker.Instance))
        {
            return FieldType.I64;
        }
        if (ReferenceEquals(reference, BoolMarker.Instance))
        {
            return FieldType.Bool;
        }
        if (ReferenceEquals(reference, MacAddressMarker.Instance))
        {
            return FieldType.MacAddress;
        }
        if (ReferenceEquals(reference, IPv4AddressMarker.Instance))
        {
            return FieldType.IPv4Address;
        }
        if (ReferenceEquals(reference, Eui64Marker.Instance))
        {
            return FieldType.Eui64;
        }
        if (ReferenceEquals(reference, TimestampMarker.Instance))
        {
            return FieldType.Timestamp;
        }
        if (ReferenceEquals(reference, F64Marker.Instance))
        {
            return FieldType.F64;
        }
        if (ReferenceEquals(reference, IPv6AddressMarker.Instance))
        {
            return FieldType.IPv6Address;
        }
        if (ReferenceEquals(reference, UuidMarker.Instance))
        {
            return FieldType.Uuid;
        }

        // Payload-carrying reference types — runtime type checks.
        // These allocate objects anyway, so the type check cost is negligible.
        // string/boxed LazyString are checked first among reference types because they are the
        // most common payload types after marker-based inline values.
        if (reference is string or LazyString)
        {
            return FieldType.String;
        }
        if (reference is byte[])
        {
            return FieldType.Bytes;
        }

        // Unreachable for instances built through the factory methods (the only way to set _Ref);
        // an unknown payload degrades to None rather than throwing.
        return FieldType.None;
    }

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation
    // These are the only public accessors for extracting typed values.
    // Each one is a single reference-identity test on the marker singleton followed by reinterpreting the
    // inline bits (signed values and timestamps are stored as the raw bits of their 64-bit two's complement
    // value, doubles via BitConverter). Check and extraction are one Try call so the type is classified only once.

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.Bool"/>, extracting the value.</summary>
    /// <param name="value">The boolean when the type matches; <see langword="false"/> otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsBool(out bool value)
    {
        if (ReferenceEquals(_Ref, BoolMarker.Instance))
        {
            value = _Data != 0;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.I64"/>, extracting the value.</summary>
    /// <param name="value">The integer when the type matches; 0 otherwise. A U64 or F64 value does not match.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsI64(out long value)
    {
        if (ReferenceEquals(_Ref, I64Marker.Instance))
        {
            value = (long)_Data;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.U64"/>, extracting the value.</summary>
    /// <param name="value">The integer when the type matches; 0 otherwise. An I64 or F64 value does not match.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsU64(out ulong value)
    {
        if (ReferenceEquals(_Ref, U64Marker.Instance))
        {
            value = _Data;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.F64"/>, extracting the value.</summary>
    /// <param name="value">The number when the type matches; 0 otherwise. An integer value does not match.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsF64(out double value)
    {
        if (ReferenceEquals(_Ref, F64Marker.Instance))
        {
            value = BitConverter.UInt64BitsToDouble(_Data);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if this value is <see cref="FieldType.String"/>, extracting the string.
    /// For lazy string values, evaluates and caches the factory on first call.
    /// </summary>
    /// <param name="value">The string when the type matches; the empty string otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsString(out string value)
    {
        if (_Ref is string str)
        {
            value = str;
            return true;
        }
        // Lazy string: call AsString via Unsafe.Unbox so the CAS-based cache persists to the heap-resident boxed struct.
        if (_Ref is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(_Ref);
            value = ls.AsString;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if this value is <see cref="FieldType.Bytes"/>, extracting the value.
    /// The returned memory aliases the stored array (no copy).
    /// </summary>
    /// <param name="value">The bytes when the type matches; empty otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsBytes(out ReadOnlyMemory<byte> value)
    {
        if (_Ref is byte[] array)
        {
            // Unpack the slice written by NewBytes: offset in the low 32 bits, length in the high 32 bits.
            int offset = (int)(_Data & 0xFFFFFFFF);
            int length = (int)(_Data >> 32);
            value = new ReadOnlyMemory<byte>(array, offset, length);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.MacAddress"/>, extracting the value.</summary>
    /// <param name="value">The address when the type matches; the default address otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsMacAddress(out MacAddress value)
    {
        if (ReferenceEquals(_Ref, MacAddressMarker.Instance))
        {
            value = new MacAddress(_Data);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.IPv4Address"/>, extracting the value.</summary>
    /// <param name="value">The address when the type matches; the default address otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsIPv4(out IPv4Address value)
    {
        if (ReferenceEquals(_Ref, IPv4AddressMarker.Instance))
        {
            value = new IPv4Address((uint)_Data);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.IPv6Address"/>, extracting the value.</summary>
    /// <param name="value">The address when the type matches; the default address otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsIPv6(out IPv6Address value)
    {
        if (ReferenceEquals(_Ref, IPv6AddressMarker.Instance))
        {
            value = new IPv6Address(_Data, _Data1);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.Eui64"/>, extracting the value.</summary>
    /// <param name="value">The identifier when the type matches; the default identifier otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsEui64(out Eui64 value)
    {
        if (ReferenceEquals(_Ref, Eui64Marker.Instance))
        {
            value = new Eui64(_Data);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.Uuid"/>, extracting the value.</summary>
    /// <param name="value">The UUID when the type matches; the default UUID otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsUuid(out Values.Uuid value)
    {
        if (ReferenceEquals(_Ref, UuidMarker.Instance))
        {
            value = new Values.Uuid(_Data, _Data1);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.Timestamp"/>, extracting the value.</summary>
    /// <param name="value">The timestamp when the type matches; the default timestamp otherwise.</param>
    /// <returns><see langword="true"/> when the type matches.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsTimestamp(out Timestamp value)
    {
        if (ReferenceEquals(_Ref, TimestampMarker.Instance))
        {
            value = new Timestamp((long)_Data);
            return true;
        }
        value = default;
        return false;
    }

    #endregion

    #region String payload extraction

    /// <summary>
    /// Extracts the string value from <paramref name="refValue"/>.
    /// Returns the string directly or evaluates the lazy string on demand.
    /// Returns the empty string for any other payload. Must be called on the stored <c>_Ref</c> object, not on a copy of the
    /// <see cref="LazyString"/>, so the cached result is shared (see <c>NewLazyString</c>).
    /// </summary>
    /// <param name="refValue">The <c>_Ref</c> of a value: a string, a boxed <see cref="LazyString"/>, or anything else.</param>
    /// <returns>The text of a String payload; the empty string for every other payload.</returns>
    private static string _ExtractString(object? refValue)
    {
        if (refValue is string str)
        {
            return str;
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue);
            return ls.AsString;
        }
        return string.Empty;
    }

    #endregion

    #region Equality

    /// <summary>
    /// Value equality. Two values are equal only when they have the same <see cref="FieldType"/> and the same content:
    /// an I64 never equals a U64 or F64 with the same number (unlike <see cref="CompareTo"/>, which orders across numeric
    /// types). F64 values are compared by their IEEE-754 bit pattern, so +0 and -0 differ and a NaN equals a NaN with
    /// identical bits. Strings use ordinal comparison; Bytes compare by content.
    /// </summary>
    /// <param name="other">The value to compare with.</param>
    /// <returns><see langword="true"/> when both values have the same type and content.</returns>
    public bool Equals(FieldValueData other)
    {
        // Fast path for None and the heap payloads: when both sides hold the very same _Ref object (or both null),
        // no content comparison is needed and only the inline words are compared. For a shared string or lazy-string
        // box _Data is 0 on both sides; for a shared byte[] _Data holds the offset|length slice. Marker singletons
        // are not handled here; they fall through to the type switch below, which compares the inline words.
        if (ReferenceEquals(_Ref, other._Ref))
        {
            if (_Ref is null or string or LazyString)
            {
                return _Data == other._Data && _Data1 == other._Data1;
            }

            // Two slices of one shared byte[]: the same slice is equal at once. A different offset or length is not a
            // verdict, because the two slices can still hold the same bytes (CompareTo and GetHashCode compare by content).
            // Returning false here would break the Equals/GetHashCode contract, so it falls through to the content comparison.
            if (_Ref is byte[] && _Data == other._Data && _Data1 == other._Data1)
            {
                return true;
            }
        }

        // Different types are never equal; the same type is compared by content.
        FieldType thisType = Type;
        FieldType otherType = other.Type;
        if (thisType != otherType)
        {
            return false;
        }
        return thisType switch
        {
            FieldType.None => true,
            FieldType.String => string.Equals(_ExtractString(_Ref), _ExtractString(other._Ref), StringComparison.Ordinal),
            FieldType.Bytes => _ExtractBytesSpan().SequenceEqual(other._ExtractBytesSpan()),
            // The 128-bit types keep their value in both inline words, so both must match.
            FieldType.IPv6Address or FieldType.Uuid => _Data == other._Data && _Data1 == other._Data1,
            // All other inline types (and Bool) keep the whole value in _Data.
            _ => _Data == other._Data,
        };
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FieldValueData other && Equals(other);

    /// <summary>
    /// Hash code consistent with <see cref="Equals(FieldValueData)"/>: the type is part of the hash, strings hash by
    /// ordinal content, Bytes by content (not by array identity), and the 128-bit types by both inline words.
    /// The value is process-specific (<see cref="HashCode"/> is randomly seeded) and must not be persisted.
    /// </summary>
    /// <returns>The hash code of the type and content.</returns>
    public override int GetHashCode()
    {
        FieldType type = Type;
        return type switch
        {
            FieldType.String => HashCode.Combine(type, StringComparer.Ordinal.GetHashCode(_ExtractString(_Ref))),
            FieldType.Bytes => _BytesHashCode(),
            // Both inline words participate in equality for IPv6 and Uuid, so both are hashed.
            FieldType.IPv6Address or FieldType.Uuid => HashCode.Combine(type, _Data, _Data1),
            _ => HashCode.Combine(type, _Data),
        };
    }

    /// <summary>Hashes the sliced content of a Bytes value so equal slices of different arrays hash equally.</summary>
    /// <returns>The hash code of the type tag and the bytes of the slice.</returns>
    private int _BytesHashCode()
    {
        HashCode hash = new();
        hash.Add((byte)FieldType.Bytes);
        hash.AddBytes(_ExtractBytesSpan());
        return hash.ToHashCode();
    }

    #endregion

    #region IComparable

    /// <summary>
    /// Orders two values. Same-type values compare by content: signed for I64 and Timestamp,
    /// <see cref="double.CompareTo(double)"/> for F64 (NaN sorts below every other value), ordinal for String,
    /// lexicographic for Bytes, unsigned numeric for the remaining inline types.
    /// Different numeric types (I64, U64, F64) compare by numeric value; MacAddress and Eui64 compare by raw value;
    /// IPv4 sorts before IPv6. Any other mix is ordered by the <see cref="FieldType"/> discriminant.
    /// </summary>
    /// <param name="other">The value to compare with.</param>
    /// <returns>A negative number, zero, or a positive number when this value sorts before, equal to, or after <paramref name="other"/>.</returns>
    /// <remarks>
    /// This ordering is intentionally more lenient than <see cref="Equals(FieldValueData)"/>: values of different types
    /// can compare as 0 while not being equal. Comparing I64 or U64 with F64 converts the integer to <see cref="double"/>,
    /// which loses precision above 2^53, so those cross-type comparisons are approximate for very large integers.
    /// </remarks>
    public int CompareTo(FieldValueData other)
    {
        FieldType thisType = Type;
        FieldType otherType = other.Type;

        // Same type — direct dispatch
        if (thisType == otherType)
        {
            return thisType switch
            {
                FieldType.None => 0,
                FieldType.I64 => ((long)_Data).CompareTo((long)other._Data),
                FieldType.F64 => BitConverter.UInt64BitsToDouble(_Data).CompareTo(BitConverter.UInt64BitsToDouble(other._Data)),
                FieldType.String => string.Compare(_ExtractString(_Ref), _ExtractString(other._Ref), StringComparison.Ordinal),
                FieldType.Bytes => _ExtractBytesSpan().SequenceCompareTo(other._ExtractBytesSpan()),
                FieldType.IPv6Address => new IPv6Address(_Data, _Data1).CompareTo(new IPv6Address(other._Data, other._Data1)),
                FieldType.Uuid => new Values.Uuid(_Data, _Data1).CompareTo(new Values.Uuid(other._Data, other._Data1)),
                FieldType.Timestamp => ((long)_Data).CompareTo((long)other._Data),
                _ => _Data.CompareTo(other._Data),
            };
        }

        // Cross-type numeric comparisons (I64, U64, F64). I64 versus U64 is exact (no widening to double);
        // the pairs involving F64 convert the integer to double and so lose precision above 2^53.
        return (thisType, otherType) switch
        {
            (FieldType.I64, FieldType.U64) => _CompareSignedToUnsigned((long)_Data, other._Data),
            (FieldType.I64, FieldType.F64) => ((double)(long)_Data).CompareTo(BitConverter.UInt64BitsToDouble(other._Data)),
            (FieldType.U64, FieldType.I64) => -_CompareSignedToUnsigned((long)other._Data, _Data),
            (FieldType.U64, FieldType.F64) => ((double)_Data).CompareTo(BitConverter.UInt64BitsToDouble(other._Data)),
            (FieldType.F64, FieldType.I64) => BitConverter.UInt64BitsToDouble(_Data).CompareTo((double)(long)other._Data),
            (FieldType.F64, FieldType.U64) => BitConverter.UInt64BitsToDouble(_Data).CompareTo((double)other._Data),
            // Cross-type address comparisons: a MAC (48-bit) and an EUI-64 compare by raw numeric value;
            // IPv4 sorts before IPv6 regardless of value.
            (FieldType.MacAddress, FieldType.Eui64) => _Data.CompareTo(other._Data),
            (FieldType.Eui64, FieldType.MacAddress) => _Data.CompareTo(other._Data),
            (FieldType.IPv4Address, FieldType.IPv6Address) => -1,
            (FieldType.IPv6Address, FieldType.IPv4Address) => 1,
            // Incompatible types: order by type discriminant
            _ => thisType.CompareTo(otherType),
        };
    }

    /// <summary>Exact comparison of a signed and an unsigned 64-bit integer without widening (every negative value is smaller).</summary>
    /// <param name="signed">The signed operand.</param>
    /// <param name="unsigned">The unsigned operand.</param>
    /// <returns>-1 when <paramref name="signed"/> is negative; otherwise the comparison of the two numbers.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _CompareSignedToUnsigned(long signed, ulong unsigned)
    {
        if (signed < 0)
        {
            return -1;
        }
        return ((ulong)signed).CompareTo(unsigned);
    }

    /// <summary>Whether two values are equal (see <see cref="Equals(FieldValueData)"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when both values have the same type and content.</returns>
    public static bool operator ==(FieldValueData left, FieldValueData right) => left.Equals(right);

    /// <summary>Whether two values differ (see <see cref="Equals(FieldValueData)"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when the values differ in type or content.</returns>
    public static bool operator !=(FieldValueData left, FieldValueData right) => !left.Equals(right);

    /// <summary>Whether <paramref name="left"/> sorts before <paramref name="right"/> (see <see cref="CompareTo"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is less than <paramref name="right"/>.</returns>
    public static bool operator <(FieldValueData left, FieldValueData right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> sorts after <paramref name="right"/> (see <see cref="CompareTo"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is greater than <paramref name="right"/>.</returns>
    public static bool operator >(FieldValueData left, FieldValueData right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> sorts before or equal to <paramref name="right"/> (see <see cref="CompareTo"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is less than or equal to <paramref name="right"/>.</returns>
    public static bool operator <=(FieldValueData left, FieldValueData right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> sorts after or equal to <paramref name="right"/> (see <see cref="CompareTo"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</returns>
    public static bool operator >=(FieldValueData left, FieldValueData right) => left.CompareTo(right) >= 0;

    #endregion

    #region ISpanFormattable

    /// <summary>
    /// Formats the value into a character span.
    /// Dispatches directly based on <c>_Ref</c> identity, avoiding double classification.
    /// </summary>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Accepted for <see cref="ISpanFormattable"/> and ignored: a field value has one invariant text form.</param>
    /// <param name="provider">Accepted for <see cref="ISpanFormattable"/> and ignored: a field value has one invariant text form.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small; nothing is written then.</returns>
    /// <remarks>
    /// The <paramref name="format"/> argument is ignored: every type has exactly one display form. Integers and doubles are always
    /// formatted with <see cref="CultureInfo.InvariantCulture"/> regardless of <paramref name="provider"/>, so the displayed
    /// text never depends on the host locale. Returns <see langword="false"/> with <c>0</c> chars written when
    /// <paramref name="destination"/> is too small. The order of the identity checks mirrors <c>_ClassifyRef</c>
    /// (most frequent types first); it is a speed choice only. The same dispatch exists for UTF-8 below and the two must stay in sync.
    /// </remarks>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        object? refValue = _Ref;

        // Null → None
        if (refValue is null)
        {
            return _TryFormatEmpty(out charsWritten);
        }

        // Marker singletons — ordered by frequency
        if (ReferenceEquals(refValue, U64Marker.Instance))
        {
            return _Data.TryFormat(destination, out charsWritten, default, CultureInfo.InvariantCulture);
        }
        if (ReferenceEquals(refValue, I64Marker.Instance))
        {
            return ((long)_Data).TryFormat(destination, out charsWritten, default, CultureInfo.InvariantCulture);
        }
        if (ReferenceEquals(refValue, BoolMarker.Instance))
        {
            return _TryFormatBool(destination, out charsWritten);
        }
        if (ReferenceEquals(refValue, MacAddressMarker.Instance))
        {
            return new MacAddress(_Data).TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, IPv4AddressMarker.Instance))
        {
            return new IPv4Address((uint)_Data).TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, Eui64Marker.Instance))
        {
            return new Eui64(_Data).TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, TimestampMarker.Instance))
        {
            return new Timestamp((long)_Data).TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, F64Marker.Instance))
        {
            return BitConverter.UInt64BitsToDouble(_Data).TryFormat(destination, out charsWritten, default, CultureInfo.InvariantCulture);
        }
        if (ReferenceEquals(refValue, IPv6AddressMarker.Instance))
        {
            return new IPv6Address(_Data, _Data1).TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, UuidMarker.Instance))
        {
            return new Values.Uuid(_Data, _Data1).TryFormat(destination, out charsWritten, default, provider);
        }

        // Reference-carrying payload types
        if (refValue is string str)
        {
            return _TryFormatString(str, destination, out charsWritten);
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue);
            return _TryFormatString(ls.AsString, destination, out charsWritten);
        }
        if (refValue is byte[])
        {
            return _TryFormatBytes(destination, out charsWritten);
        }

        charsWritten = 0;
        return false;
    }

    /// <summary>
    /// Formats the value into a UTF-8 byte span.
    /// Dispatches directly based on <c>_Ref</c> identity, avoiding double classification.
    /// Same rules as the character overload: <paramref name="format"/> is ignored, numbers use the invariant culture,
    /// and <see langword="false"/> with <c>0</c> bytes written means the destination is too small.
    /// </summary>
    /// <param name="utf8Destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Accepted for <see cref="IUtf8SpanFormattable"/> and ignored: a field value has one invariant text form.</param>
    /// <param name="provider">Accepted for <see cref="IUtf8SpanFormattable"/> and ignored: a field value has one invariant text form.</param>
    /// <returns><see langword="false"/> when <paramref name="utf8Destination"/> is too small; nothing is written then.</returns>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        object? refValue = _Ref;

        if (refValue is null)
        {
            return _TryFormatEmpty(out bytesWritten);
        }

        if (ReferenceEquals(refValue, U64Marker.Instance))
        {
            return _Data.TryFormat(utf8Destination, out bytesWritten, default, CultureInfo.InvariantCulture);
        }
        if (ReferenceEquals(refValue, I64Marker.Instance))
        {
            return ((long)_Data).TryFormat(utf8Destination, out bytesWritten, default, CultureInfo.InvariantCulture);
        }
        if (ReferenceEquals(refValue, BoolMarker.Instance))
        {
            return _TryFormatBoolUtf8(utf8Destination, out bytesWritten);
        }
        if (ReferenceEquals(refValue, MacAddressMarker.Instance))
        {
            return new MacAddress(_Data).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, IPv4AddressMarker.Instance))
        {
            return new IPv4Address((uint)_Data).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, Eui64Marker.Instance))
        {
            return new Eui64(_Data).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, TimestampMarker.Instance))
        {
            return new Timestamp((long)_Data).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, F64Marker.Instance))
        {
            return BitConverter.UInt64BitsToDouble(_Data).TryFormat(utf8Destination, out bytesWritten, default, CultureInfo.InvariantCulture);
        }
        if (ReferenceEquals(refValue, IPv6AddressMarker.Instance))
        {
            return new IPv6Address(_Data, _Data1).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, UuidMarker.Instance))
        {
            return new Values.Uuid(_Data, _Data1).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }

        if (refValue is string str)
        {
            return _TryFormatStringUtf8(str, utf8Destination, out bytesWritten);
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue);
            return _TryFormatStringUtf8(ls.AsString, utf8Destination, out bytesWritten);
        }
        if (refValue is byte[])
        {
            return _TryFormatBytesUtf8(utf8Destination, out bytesWritten);
        }

        bytesWritten = 0;
        return false;
    }

    /// <summary>
    /// Returns the display text of the value. String values return the stored string (no allocation); a lazy string
    /// is evaluated in place and cached; every other type allocates exactly the result string.
    /// </summary>
    /// <remarks>
    /// Scalars are formatted into a <see cref="_StackFormatLimit"/>-char stack buffer and copied once into the result; a
    /// Bytes value gets an exact-size <see cref="string.Create{TState}"/> so the hex text (3 chars per byte minus one) is
    /// written straight into the string. Both paths are re-entrant and keep no per-thread state. Rejected: a thread-static
    /// scratch buffer, which holds 2 Mi chars per thread for the largest Bytes value, and whose nested use (a value formatted
    /// while another is being formatted) would need a second buffer. Numbers always use
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </remarks>
    /// <returns>The display text; see the remarks for the allocation of each type.</returns>
    /// <exception cref="InvalidOperationException">
    /// A Bytes value is so long that its hex form exceeds <see cref="int.MaxValue"/> characters (see <see cref="TryGetStringSize"/>).
    /// </exception>
    public override string ToString()
    {
        object? refValue = _Ref;

        // Fast paths: no formatting needed. The lazy string is evaluated through the box so every copy shares the cache.
        if (refValue is null)
        {
            return string.Empty;
        }
        if (refValue is string str)
        {
            return str;
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue);
            return ls.AsString;
        }

        // Bytes: the hex text can be large (3n - 1 chars), so write it directly into an exactly sized string. A length
        // whose hex form does not fit an int is reported by TryGetStringSize; the throw below marks that broken
        // invariant for a caller that skipped the size hint, not an expected failure.
        if (refValue is byte[])
        {
            if (!_TryGetHexFormattedByteCharCount((int)(_Data >> 32), out int hexChars))
            {
                throw new InvalidOperationException("Bytes value too long to format as text (the hex form exceeds int.MaxValue characters).");
            }

            // string.Create hands the callback a span of exactly hexChars characters, which is the size the formatter needs, so
            // its result is always true and is not checked (a callback cannot report failure to string.Create anyway).
            return hexChars == 0
                ? string.Empty
                : string.Create(hexChars, this, static (span, self) => self._TryFormatBytes(span, out _));
        }

        // Scalars: every default text fits _StackFormatLimit; one copy into the result string.
        Span<char> buffer = stackalloc char[_StackFormatLimit];
        if (!TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Scalar text exceeds the stack format limit; the limit is wrong.");
        }
        return new string(buffer[..written]);
    }

    /// <summary>Returns the formatted string representation of the value. Both arguments are ignored (see <see cref="ToString()"/>).</summary>
    /// <param name="format">Ignored: a field value has one invariant text form.</param>
    /// <param name="formatProvider">Ignored: a field value has one invariant text form.</param>
    /// <returns>The same text as <see cref="ToString()"/>.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    #endregion

    #region String Size

    /// <summary>
    /// Tries to determine the number of characters needed to format this value.
    /// Returns <c>true</c> for types where the size can be determined without formatting;
    /// returns <c>false</c> for F64 whose character count depends on the actual value, and for a Bytes value whose
    /// hex form would not fit in a 32-bit length.
    /// Dispatches directly on <c>_Ref</c> to avoid double classification. Exporters call it to presize their buffers;
    /// it is a plain method (no interface) because every caller names the concrete type.
    /// </summary>
    /// <remarks>
    /// The size is exact for most types but a tight upper bound for I64 and U64 (the maximum width, not the digit count), so the
    /// caller can pre-size a buffer in O(1). A lazy string is evaluated (and cached) here, because its length is only known
    /// after evaluation.
    /// </remarks>
    /// <param name="format">Passed on to the formatted address and timestamp types, which ignore the arguments they do not use.</param>
    /// <param name="provider">Passed on like <paramref name="format"/>.</param>
    /// <param name="size">The character count when the method returns <see langword="true"/>; 0 otherwise.</param>
    /// <returns><see langword="true"/> when the size is known without formatting.</returns>
    public bool TryGetStringSize(ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        object? refValue = _Ref;

        if (refValue is null)
        {
            size = 0;
            return true;
        }
        if (ReferenceEquals(refValue, BoolMarker.Instance))
        {
            // "True" or "False"
            size = _Data != 0
                ? 4
                : 5;
            return true;
        }
        if (ReferenceEquals(refValue, I64Marker.Instance) || ReferenceEquals(refValue, U64Marker.Instance))
        {
            size = 20; // Upper bound: long.MinValue is "-" plus 19 digits and ulong.MaxValue is 20 digits.
            return true;
        }
        if (ReferenceEquals(refValue, F64Marker.Instance))
        {
            size = 0;
            return false; // F64 size depends on actual value
        }
        if (ReferenceEquals(refValue, MacAddressMarker.Instance))
        {
            return MacAddress.TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, IPv4AddressMarker.Instance))
        {
            return new IPv4Address((uint)_Data).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, Eui64Marker.Instance))
        {
            return Eui64.TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, TimestampMarker.Instance))
        {
            return Timestamp.TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, IPv6AddressMarker.Instance))
        {
            return new IPv6Address(_Data, _Data1).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, UuidMarker.Instance))
        {
            return Values.Uuid.TryGetStringSize(format, provider, out size);
        }
        if (refValue is string str)
        {
            size = str.Length;
            return true;
        }
        if (refValue is LazyString)
        {
            // Evaluate the lazy string to determine its character count.
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue);
            size = ls.AsString.Length;
            return true;
        }
        if (refValue is byte[])
        {
            int length = (int)(_Data >> 32);
            return _TryGetHexFormattedByteCharCount(length, out size);
        }
        // Unreachable for instances built through the factory methods; mirrors the None answer of _ClassifyRef.
        size = 0;
        return true;
    }

    #endregion

    #region Char formatting helpers

    /// <summary>Writes "True" or "False" (the same text as <see cref="bool.ToString()"/>) if it fits.</summary>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    private bool _TryFormatBool(Span<char> destination, out int charsWritten)
    {
        ReadOnlySpan<char> text = _Data != 0
            ? "True"
            : "False";
        if (destination.Length < text.Length)
        {
            charsWritten = 0;
            return false;
        }
        text.CopyTo(destination);
        charsWritten = text.Length;
        return true;
    }

    /// <summary>Copies <paramref name="text"/> into the destination if it fits; writes nothing otherwise.</summary>
    /// <param name="text">The text to copy.</param>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">The text length; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    private static bool _TryFormatString(string text, Span<char> destination, out int charsWritten)
    {
        if (destination.Length < text.Length)
        {
            charsWritten = 0;
            return false;
        }
        text.CopyTo(destination);
        charsWritten = text.Length;
        return true;
    }

    /// <summary>
    /// Writes a Bytes value as uppercase hexadecimal pairs separated by single spaces ("0A FF 00"). Nothing is written
    /// unless the whole text fits.
    /// </summary>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="destination"/> is too small or the hex text exceeds <see cref="int.MaxValue"/> characters.
    /// </returns>
    private bool _TryFormatBytes(Span<char> destination, out int charsWritten)
    {
        // Size check first: the exact required length is computed in O(1) and checked once, so the loop below needs
        // no per-character bounds handling and never leaves a partially written destination. A length whose text
        // would exceed int.MaxValue chars is reported as "does not fit" rather than overflowing.
        int length = (int)(_Data >> 32);
        if (!_TryGetHexFormattedByteCharCount(length, out int required))
        {
            charsWritten = 0;
            return false;
        }
        ReadOnlySpan<byte> bytes = _ExtractBytesSpan();
        if (destination.Length < required)
        {
            charsWritten = 0;
            return false;
        }

        // Write "XX" per byte with a leading space for every byte except the first.
        int pos = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                destination[pos++] = ' ';
            }
            byte currentByte = bytes[i];
            destination[pos++] = _GetHexChar(currentByte >> 4);
            destination[pos++] = _GetHexChar(currentByte & 0xF);
        }
        charsWritten = pos;
        return true;
    }

    #endregion

    #region UTF-8 formatting helpers

    /// <summary>UTF-8 variant of <see cref="_TryFormatBool"/> using <c>u8</c> literals (no encoding step).</summary>
    /// <param name="destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    private bool _TryFormatBoolUtf8(Span<byte> destination, out int bytesWritten)
    {
        ReadOnlySpan<byte> text = _Data != 0
            ? "True"u8
            : "False"u8;
        if (destination.Length < text.Length)
        {
            bytesWritten = 0;
            return false;
        }
        text.CopyTo(destination);
        bytesWritten = text.Length;
        return true;
    }

    /// <summary>Encodes <paramref name="text"/> as UTF-8 into the destination if the encoded form fits; writes nothing otherwise.</summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="destination">Target span.</param>
    /// <param name="bytesWritten">The UTF-8 length; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    private static bool _TryFormatStringUtf8(string text, Span<byte> destination, out int bytesWritten)
    {
        // Count first so that a too-small destination is rejected without a partial write (costs one extra pass over the text).
        // Lone surrogates are replaced by U+FFFD by the encoder; the count already includes the replacement.
        int byteCount = Encoding.UTF8.GetByteCount(text);
        if (destination.Length < byteCount)
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = Encoding.UTF8.GetBytes(text, destination);
        return true;
    }

    /// <summary>UTF-8 twin of <see cref="_TryFormatBytes"/>: hex pairs separated by single spaces, ASCII only (one byte per character).</summary>
    /// <param name="destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small or the hex text exceeds <see cref="int.MaxValue"/> bytes.</returns>
    private bool _TryFormatBytesUtf8(Span<byte> destination, out int bytesWritten)
    {
        // Hex digits and spaces are ASCII, so the required byte count equals the character count.
        int length = (int)(_Data >> 32);
        if (!_TryGetHexFormattedByteCharCount(length, out int required))
        {
            bytesWritten = 0;
            return false;
        }
        ReadOnlySpan<byte> bytes = _ExtractBytesSpan();
        if (destination.Length < required)
        {
            bytesWritten = 0;
            return false;
        }
        int pos = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                destination[pos++] = (byte)' ';
            }
            byte currentByte = bytes[i];
            destination[pos++] = _GetHexByte(currentByte >> 4);
            destination[pos++] = _GetHexByte(currentByte & 0xF);
        }
        bytesWritten = pos;
        return true;
    }

    #endregion

    #region Shared helpers

    /// <summary>
    /// Size of the stack buffer that <see cref="ToString()"/> formats scalars into, in characters.
    /// </summary>
    /// <remarks>
    /// Largest default text of any scalar field type: IPv6 39, UUID 36, Timestamp 30, I64 20 (U64 20), F64 at most 24
    /// ('R' round-trip), Bool 5. Bytes and strings never take the stack path. Constant of the formats, not a tunable:
    /// it is not an external-input limit, so it is neither configurable nor a posture decision. 64 chars = 128 bytes of
    /// stack per ToString call, small enough to be safe at any call depth.
    /// </remarks>
    private const int _StackFormatLimit = 64;

    /// <summary>Extracts the bytes span from a Bytes-typed value. Only valid when <c>_Ref is byte[]</c>.</summary>
    /// <returns>The slice of the stored array that the value covers (offset in the low 32 bits of <c>_Data</c>, length in the high 32 bits).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> _ExtractBytesSpan()
    {
        byte[] array = (byte[])_Ref!; // Invariant: every caller checked the type is Bytes (_Ref is byte[]) before the call.
        int offset = (int)(_Data & 0xFFFFFFFF);
        int length = (int)(_Data >> 32);
        return new ReadOnlySpan<byte>(array, offset, length);
    }

    /// <summary>
    /// Computes the character count for space-separated hex formatting of <paramref name="byteCount"/> bytes.
    /// Returns <c>false</c> when the required size cannot be represented as a 32-bit signed integer.
    /// </summary>
    /// <param name="byteCount">The number of bytes; zero or negative gives 0 characters.</param>
    /// <param name="charCount">The character count (<c>3 * byteCount - 1</c>) when the method returns <see langword="true"/>; 0 otherwise.</param>
    /// <returns><see langword="false"/> when the count would exceed <see cref="int.MaxValue"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryGetHexFormattedByteCharCount(int byteCount, out int charCount)
    {
        if (byteCount <= 0)
        {
            charCount = 0;
            return true;
        }

        // Each byte needs two hex digits plus one separator, except the last (no trailing space): 3 * n - 1 characters.
        // Overflow guard: with n = floor((int.MaxValue + 1) / 3) = 715,827,882 the result 3 * n - 1 = 2,147,483,645
        // still fits in an int, whereas n + 1 would give 2,147,483,648. The long cast keeps the bound itself from overflowing.
        if (byteCount > (int)(((long)int.MaxValue + 1) / 3))
        {
            charCount = 0;
            return false;
        }

        charCount = byteCount * 3 - 1;
        return true;
    }

    /// <summary>Maps a nibble (0 to 15) to its uppercase hexadecimal digit. The caller guarantees the range (value is masked or shifted).</summary>
    /// <param name="value">The nibble, 0 to 15.</param>
    /// <returns>The character <c>0</c> to <c>9</c> or <c>A</c> to <c>F</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char _GetHexChar(int value) => (char)(value < 10
        ? '0' + value
        : 'A' + value - 10);

    /// <summary>UTF-8 (ASCII) variant of <see cref="_GetHexChar"/>.</summary>
    /// <param name="value">The nibble, 0 to 15.</param>
    /// <returns>The ASCII byte of <c>0</c> to <c>9</c> or <c>A</c> to <c>F</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte _GetHexByte(int value) => (byte)(value < 10
        ? '0' + value
        : 'A' + value - 10);

    /// <summary>The None value formats to the empty text: writes nothing and succeeds for any destination size.</summary>
    /// <param name="written">Always 0.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryFormatEmpty(out int written)
    {
        written = 0;
        return true;
    }

    #endregion
}
