// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using static NetworkInspector.Core.Fields.FieldTypeMarkers;

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Tagged union holding one of 13 field value types in 24 bytes.
/// <para>
/// <b>Layout (x64):</b><br/>
/// <c>_Data</c> (8 bytes) — inline value storage for numeric and address types, or packed
/// offset|length for Bytes. For 128-bit types (IPv6, Uuid) stores the high 64 bits.<br/>
/// <c>_Data1</c> (8 bytes) — second inline value for 128-bit types (low 64 bits). Zero for
/// all other types.<br/>
/// <c>_Ref</c> (8 bytes) — type discriminant via static marker singletons, or payload carrier:
/// <list type="bullet">
///   <item><c>null</c> → <see cref="FieldType.None"/></item>
///   <item>Static marker singleton (e.g. <see cref="I64Marker"/>) → inline type, value in <c>_Data</c></item>
///   <item><c>string</c> or boxed <see cref="ZeroAlloc.LazyString"/> → <see cref="FieldType.String"/></item>
///   <item><c>byte[]</c> → <see cref="FieldType.Bytes"/></item>
///   <item><see cref="IPv6AddressMarker"/> → 128-bit IPv6 address inline in <c>_Data</c>/<c>_Data1</c></item>
///   <item><see cref="UuidMarker"/> → 128-bit UUID inline in <c>_Data</c>/<c>_Data1</c></item>
/// </list>
/// </para>
/// <para>
/// Values are accessed exclusively through <c>TryGetAs*</c> methods which combine type
/// checking and value extraction in a single operation.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct FieldValueData : IEquatable<FieldValueData>, IComparable<FieldValueData>, ISpanFormattable, IUtf8SpanFormattable, IStringSize
{
    // Three fields: 8 + 8 + 8 = 24 bytes total on x64.
    // _Data1 is used only for 128-bit types (IPv6Address, Uuid); zero for all others.
    private readonly ulong _Data;
    private readonly ulong _Data1;
    private readonly object? _Ref;

    /// <summary>The default (empty / unset) field value. Represents <see cref="FieldType.None"/>.</summary>
    public static readonly FieldValueData None = default;

    /// <summary>Creates field value data with inline storage and a marker/payload reference.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FieldValueData(ulong data, object? reference)
    {
        _Data = data;
        _Data1 = 0;
        _Ref = reference;
    }

    /// <summary>Creates field value data with dual inline storage for 128-bit types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FieldValueData(ulong data, ulong data1, object? reference)
    {
        _Data = data;
        _Data1 = data1;
        _Ref = reference;
    }

    #region Factory methods

    /// <summary>Creates a <see cref="FieldType.Bool"/> field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewBool(bool value) => new(value ? 1UL : 0UL, BoolMarker.Instance);
    /// <summary>Creates a <see cref="FieldType.I64"/> (signed 64-bit integer) field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewI64(long value) => new((ulong)value, I64Marker.Instance);
    /// <summary>Creates a <see cref="FieldType.U64"/> (unsigned 64-bit integer) field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewU64(ulong value) => new(value, U64Marker.Instance);
    /// <summary>Creates a <see cref="FieldType.F64"/> (IEEE-754 double-precision) field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewF64(double value) => new(BitConverter.DoubleToUInt64Bits(value), F64Marker.Instance);

    /// <summary>Creates a string field value from a <see cref="string"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewString(string value) => new(0, value ?? string.Empty);

    /// <summary>
    /// Creates a string field value whose content is deferred until first access via <see cref="TryGetAsString"/>.
    /// The <see cref="ZeroAlloc.LazyString"/> is boxed so all copies of this <see cref="FieldValueData"/>
    /// share one heap object; <see cref="ZeroAlloc.LazyString.AsString"/> is called via
    /// <see cref="Unsafe.Unbox{T}"/> so the CAS-based caching persists to the heap-resident struct.
    /// </summary>
    internal static FieldValueData NewLazyString(LazyString lazy) => new(0, (object)lazy);

    /// <summary>
    /// Creates a string field value from a <see cref="ReadOnlyMemory{Char}"/>.
    /// The memory is materialized into a string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewString(ReadOnlyMemory<char> value) => new(0, new string(value.Span));

    /// <summary>Creates a <see cref="FieldType.Bytes"/> field value from a <see cref="ReadOnlyMemory{T}"/> of bytes.
    /// Attempts zero-copy via ArraySegment; falls back to a copy if the memory is not array-backed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewBytes(ReadOnlyMemory<byte> value)
    {
        if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment))
        {
            ulong packed = (ulong)(uint)segment.Offset | ((ulong)(uint)segment.Count << 32);
            return new(packed, segment.Array);
        }
        byte[] copy = value.ToArray();
        ulong packedCopy = (ulong)(uint)copy.Length << 32;
        return new(packedCopy, copy);
    }

    /// <summary>Creates a bytes field value directly from a <c>byte[]</c> (zero-copy).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewBytes(byte[] value)
    {
        ulong packed = (ulong)(uint)value.Length << 32;
        return new(packed, value);
    }

    /// <summary>Creates a <see cref="FieldType.MacAddress"/> field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewMacAddress(MacAddress value) => new(value.RawValue, MacAddressMarker.Instance);
    /// <summary>Creates a <see cref="FieldType.IPv4Address"/> field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewIPv4(IPv4Address value) => new(value.RawValue, IPv4AddressMarker.Instance);
    /// <summary>Creates an IPv6 field value. The 128-bit address is stored inline in <c>_Data</c>/<c>_Data1</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewIPv6(IPv6Address value) => new(value.High, value.Low, IPv6AddressMarker.Instance);
    /// <summary>Creates a <see cref="FieldType.Eui64"/> field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewEui64(Eui64 value) => new(value.RawValue, Eui64Marker.Instance);
    /// <summary>Creates a <see cref="FieldType.Uuid"/> field value. The 128-bit value is stored inline in <c>_Data</c>/<c>_Data1</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FieldValueData NewUuid(Values.Uuid value) => new(value.High, value.Low, UuidMarker.Instance);
    /// <summary>Creates a <see cref="FieldType.Timestamp"/> field value.</summary>
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
        get => ClassifyRef(_Ref);
    }

    /// <summary>
    /// Classifies the reference into a <see cref="FieldType"/>.
    /// Marker singletons are checked by identity (ReferenceEquals) for speed.
    /// Payload types use pattern matching on the runtime type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FieldType ClassifyRef(object? reference)
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

        // Should never happen with well-formed FieldValueData instances.
        return FieldType.None;
    }

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation
    // These are the only public accessors for extracting typed values.

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.Bool"/>, extracting the value.</summary>
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

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.String"/>, extracting the string.
    /// For lazy string values, evaluates and caches the factory on first call.</summary>
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
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(_Ref!);
            value = ls.AsString;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.Bytes"/>, extracting the value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetAsBytes(out ReadOnlyMemory<byte> value)
    {
        if (_Ref is byte[] array)
        {
            int offset = (int)(_Data & 0xFFFFFFFF);
            int length = (int)(_Data >> 32);
            value = new ReadOnlyMemory<byte>(array, offset, length);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Returns <c>true</c> if this value is <see cref="FieldType.MacAddress"/>, extracting the value.</summary>
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

    /// <summary>
    /// Extracts the string value from <paramref name="refValue"/>.
    /// Returns the string directly or evaluates the lazy string on demand.
    /// </summary>
    private static string ExtractString(object? refValue)
    {
        if (refValue is string str)
        {
            return str;
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue!);
            return ls.AsString;
        }
        return string.Empty;
    }

    #region Equality

    /// <inheritdoc/>
    public bool Equals(FieldValueData other)
    {
        // Fast path: if both _Ref point to the same object, compare _Data and _Data1.
        if (ReferenceEquals(_Ref, other._Ref))
        {
            return _Data == other._Data && _Data1 == other._Data1;
        }

        FieldType thisType = Type;
        FieldType otherType = other.Type;
        if (thisType != otherType)
        {
            return false;
        }
        return thisType switch
        {
            FieldType.None => true,
            FieldType.String => string.Equals(ExtractString(_Ref), ExtractString(other._Ref), StringComparison.Ordinal),
            FieldType.Bytes => ExtractBytesSpan().SequenceEqual(other.ExtractBytesSpan()),
            // IPv6 and Uuid are now inline: compare both _Data and _Data1
            FieldType.IPv6Address or FieldType.Uuid => _Data == other._Data && _Data1 == other._Data1,
            _ => _Data == other._Data,
        };
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FieldValueData other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        FieldType type = Type;
        return type switch
        {
            FieldType.String => HashCode.Combine(type, StringComparer.Ordinal.GetHashCode(ExtractString(_Ref))),
            FieldType.Bytes => BytesHashCode(),
            // IPv6 and Uuid are inline: hash both _Data and _Data1
            FieldType.IPv6Address or FieldType.Uuid => HashCode.Combine(type, _Data, _Data1),
            _ => HashCode.Combine(type, _Data),
        };
    }

    private int BytesHashCode()
    {
        HashCode hash = new();
        hash.Add((byte)FieldType.Bytes);
        hash.AddBytes(ExtractBytesSpan());
        return hash.ToHashCode();
    }

    #endregion

    #region IComparable

    /// <inheritdoc/>
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
                FieldType.String => string.Compare(ExtractString(_Ref), ExtractString(other._Ref), StringComparison.Ordinal),
                FieldType.Bytes => ExtractBytesSpan().SequenceCompareTo(other.ExtractBytesSpan()),
                FieldType.IPv6Address => new IPv6Address(_Data, _Data1).CompareTo(new IPv6Address(other._Data, other._Data1)),
                FieldType.Uuid => new Values.Uuid(_Data, _Data1).CompareTo(new Values.Uuid(other._Data, other._Data1)),
                FieldType.Timestamp => ((long)_Data).CompareTo((long)other._Data),
                _ => _Data.CompareTo(other._Data),
            };
        }

        // Cross-type numeric comparisons (I64, U64, F64)
        return (thisType, otherType) switch
        {
            (FieldType.I64, FieldType.U64) => CompareSignedToUnsigned((long)_Data, other._Data),
            (FieldType.I64, FieldType.F64) => ((double)(long)_Data).CompareTo(BitConverter.UInt64BitsToDouble(other._Data)),
            (FieldType.U64, FieldType.I64) => -CompareSignedToUnsigned((long)other._Data, _Data),
            (FieldType.U64, FieldType.F64) => ((double)_Data).CompareTo(BitConverter.UInt64BitsToDouble(other._Data)),
            (FieldType.F64, FieldType.I64) => BitConverter.UInt64BitsToDouble(_Data).CompareTo((double)(long)other._Data),
            (FieldType.F64, FieldType.U64) => BitConverter.UInt64BitsToDouble(_Data).CompareTo((double)other._Data),
            // Cross-type address comparisons
            (FieldType.MacAddress, FieldType.Eui64) => _Data.CompareTo(other._Data),
            (FieldType.Eui64, FieldType.MacAddress) => _Data.CompareTo(other._Data),
            (FieldType.IPv4Address, FieldType.IPv6Address) => -1,
            (FieldType.IPv6Address, FieldType.IPv4Address) => 1,
            // Incompatible types: order by type discriminant
            _ => thisType.CompareTo(otherType),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CompareSignedToUnsigned(long signed, ulong unsigned)
        => signed < 0 ? -1 : ((ulong)signed).CompareTo(unsigned);

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    public static bool operator ==(FieldValueData left, FieldValueData right) => left.Equals(right);
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    public static bool operator !=(FieldValueData left, FieldValueData right) => !left.Equals(right);

    #endregion

    #region ISpanFormattable

    /// <summary>
    /// Formats the value into a character span.
    /// Dispatches directly based on <c>_Ref</c> identity, avoiding double classification.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        object? refValue = _Ref;

        // Null → None
        if (refValue is null)
        {
            return TryFormatEmpty(out charsWritten);
        }

        // Marker singletons — ordered by frequency
        if (ReferenceEquals(refValue, U64Marker.Instance))
        {
            return _Data.TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, I64Marker.Instance))
        {
            return ((long)_Data).TryFormat(destination, out charsWritten, default, provider);
        }
        if (ReferenceEquals(refValue, BoolMarker.Instance))
        {
            return TryFormatBool(destination, out charsWritten);
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
            return TryFormatString(str, destination, out charsWritten);
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue!);
            return TryFormatString(ls.AsString, destination, out charsWritten);
        }
        if (refValue is byte[])
        {
            return TryFormatBytes(destination, out charsWritten);
        }

        charsWritten = 0;
        return false;
    }

    /// <summary>
    /// Formats the value into a UTF-8 byte span.
    /// Dispatches directly based on <c>_Ref</c> identity, avoiding double classification.
    /// </summary>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        object? refValue = _Ref;

        if (refValue is null)
        {
            return TryFormatEmpty(out bytesWritten);
        }

        if (ReferenceEquals(refValue, U64Marker.Instance))
        {
            return _Data.TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, I64Marker.Instance))
        {
            return ((long)_Data).TryFormat(utf8Destination, out bytesWritten, default, provider);
        }
        if (ReferenceEquals(refValue, BoolMarker.Instance))
        {
            return TryFormatBoolUtf8(utf8Destination, out bytesWritten);
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
            return TryFormatStringUtf8(str, utf8Destination, out bytesWritten);
        }
        if (refValue is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue!);
            return TryFormatStringUtf8(ls.AsString, utf8Destination, out bytesWritten);
        }
        if (refValue is byte[])
        {
            return TryFormatBytesUtf8(utf8Destination, out bytesWritten);
        }

        bytesWritten = 0;
        return false;
    }

    /// <summary>
    /// Returns the formatted string representation of the value.
    /// Delegates to <see cref="ToTempString"/> to avoid duplicated dispatch logic.
    /// </summary>
    public override string ToString()
    {
        // Fast path: None
        if (_Ref is null)
        {
            return string.Empty;
        }
        // Fast path: plain string — return stored string directly, no formatting needed
        if (_Ref is string str)
        {
            return str;
        }
        // Fast path: boxed LazyString — evaluate via Unsafe.Unbox so caching persists to the heap-resident struct
        if (_Ref is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(_Ref!);
            return ls.AsString;
        }
        using TempString temp = ToTempString();
        return temp.ToString();
    }

    /// <summary>Returns the formatted string representation of the value.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    #endregion

    #region IStringSize

    /// <summary>
    /// Tries to determine the number of characters needed to format this value.
    /// Returns <c>true</c> for types where the size can be determined without formatting;
    /// returns <c>false</c> for F64 whose character count depends on the actual value.
    /// Dispatches directly on <c>_Ref</c> to avoid double classification.
    /// </summary>
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
            size = _Data != 0 ? 4 : 5; // "True" or "False"
            return true;
        }
        if (ReferenceEquals(refValue, I64Marker.Instance) || ReferenceEquals(refValue, U64Marker.Instance))
        {
            size = 20; // upper bound for 64-bit integer
            return true;
        }
        if (ReferenceEquals(refValue, F64Marker.Instance))
        {
            size = 0;
            return false; // F64 size depends on actual value
        }
        if (ReferenceEquals(refValue, MacAddressMarker.Instance))
        {
            return new MacAddress(_Data).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, IPv4AddressMarker.Instance))
        {
            return new IPv4Address((uint)_Data).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, Eui64Marker.Instance))
        {
            return new Eui64(_Data).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, TimestampMarker.Instance))
        {
            return new Timestamp((long)_Data).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, IPv6AddressMarker.Instance))
        {
            return new IPv6Address(_Data, _Data1).TryGetStringSize(format, provider, out size);
        }
        if (ReferenceEquals(refValue, UuidMarker.Instance))
        {
            return new Values.Uuid(_Data, _Data1).TryGetStringSize(format, provider, out size);
        }
        if (refValue is string str)
        {
            size = str.Length;
            return true;
        }
        if (refValue is LazyString)
        {
            // Evaluate the lazy string to determine its character count.
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(refValue!);
            size = ls.AsString.Length;
            return true;
        }
        if (refValue is byte[])
        {
            int length = (int)(_Data >> 32);
            size = length > 0 ? length * 3 - 1 : 0;
            return true;
        }
        // Should not happen with well-formed instances
        size = 0;
        return true;
    }

    #endregion

    #region Zero-allocation formatting

    /// <summary>
    /// Returns a <see cref="TempString"/> containing the formatted value.
    /// Uses <see cref="ZA.String(FieldValueData)"/> for zero-allocation formatting via
    /// the <see cref="ISpanFormattable"/> implementation.
    /// The caller is responsible for disposing the returned <see cref="TempString"/>.
    /// </summary>
    public TempString ToTempString()
    {
        // Fast path: None — empty result without buffer acquisition
        if (_Ref is null)
        {
            return new TempString([], 0, false);
        }
        // Fast path: plain string — copy chars into ZA-managed buffer directly
        if (_Ref is string str)
        {
            char[] buffer = ZeroAllocHelper.AcquireCharBuffer(str.Length, out bool isThreadStatic);
            str.CopyTo(buffer);
            return new TempString(buffer, str.Length, isThreadStatic);
        }
        // Fast path: boxed LazyString — evaluate via Unsafe.Unbox so caching persists to the heap-resident struct, then copy
        if (_Ref is LazyString)
        {
            ref LazyString ls = ref Unsafe.Unbox<LazyString>(_Ref!);
            string evaluated = ls.AsString;
            char[] lazyBuffer = ZeroAllocHelper.AcquireCharBuffer(evaluated.Length, out bool lazyIsThreadStatic);
            evaluated.CopyTo(lazyBuffer);
            return new TempString(lazyBuffer, evaluated.Length, lazyIsThreadStatic);
        }
        // General path: use ZA.String which leverages ISpanFormattable + IStringSize
        return ZA.String(this);
    }

    #endregion

    #region Char formatting helpers

    private bool TryFormatBool(Span<char> destination, out int charsWritten)
    {
        ReadOnlySpan<char> text = _Data != 0 ? "True" : "False";
        if (destination.Length < text.Length)
        {
            charsWritten = 0;
            return false;
        }
        text.CopyTo(destination);
        charsWritten = text.Length;
        return true;
    }

    private static bool TryFormatString(string text, Span<char> destination, out int charsWritten)
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

    private bool TryFormatBytes(Span<char> destination, out int charsWritten)
    {
        ReadOnlySpan<byte> bytes = ExtractBytesSpan();
        int required = bytes.Length > 0 ? bytes.Length * 3 - 1 : 0;
        if (destination.Length < required)
        {
            charsWritten = 0;
            return false;
        }
        int pos = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                destination[pos++] = ' ';
            }
            byte currentByte = bytes[i];
            destination[pos++] = GetHexChar(currentByte >> 4);
            destination[pos++] = GetHexChar(currentByte & 0xF);
        }
        charsWritten = pos;
        return true;
    }

    #endregion

    #region UTF-8 formatting helpers

    private bool TryFormatBoolUtf8(Span<byte> destination, out int bytesWritten)
    {
        ReadOnlySpan<byte> text = _Data != 0 ? "True"u8 : "False"u8;
        if (destination.Length < text.Length)
        {
            bytesWritten = 0;
            return false;
        }
        text.CopyTo(destination);
        bytesWritten = text.Length;
        return true;
    }

    private static bool TryFormatStringUtf8(string text, Span<byte> destination, out int bytesWritten)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        if (destination.Length < byteCount)
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = Encoding.UTF8.GetBytes(text, destination);
        return true;
    }

    private bool TryFormatBytesUtf8(Span<byte> destination, out int bytesWritten)
    {
        ReadOnlySpan<byte> bytes = ExtractBytesSpan();
        int required = bytes.Length > 0 ? bytes.Length * 3 - 1 : 0;
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
            destination[pos++] = GetHexByte(currentByte >> 4);
            destination[pos++] = GetHexByte(currentByte & 0xF);
        }
        bytesWritten = pos;
        return true;
    }

    #endregion

    #region Shared helpers

    /// <summary>Extracts the bytes span from a Bytes-typed value. Only valid when <c>_Ref is byte[]</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> ExtractBytesSpan()
    {
        byte[] array = (byte[])_Ref!;
        int offset = (int)(_Data & 0xFFFFFFFF);
        int length = (int)(_Data >> 32);
        return new ReadOnlySpan<byte>(array, offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char GetHexChar(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetHexByte(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFormatEmpty(out int written)
    {
        written = 0;
        return true;
    }
    #endregion
}
