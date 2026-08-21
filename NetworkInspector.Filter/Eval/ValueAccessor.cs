// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

#region Kind

/// <summary>How a value predicate derives its input from a raw field value.</summary>
internal enum ValueAccessorKind : byte
{
    /// <summary>Use the field value unchanged.</summary>
    Direct = 0,

    /// <summary>Take a half-open byte slice, e.g. <c>eth.src[0:3]</c>.</summary>
    Slice = 1,

    /// <summary>Take the value's byte length, e.g. <c>len(udp.payload)</c>.</summary>
    Length = 2,
}

#endregion

#region Accessor

/// <summary>
/// Describes which fields a predicate reads and how each raw value is transformed before the
/// predicate sees it. One accessor is built per operand at compile time and captured as a
/// constant by the emitted delegate.
/// <para>
/// <b>Slice buffer.</b> Slicing needs a contiguous destination. Because filters are
/// single-threaded (see <see cref="IFilter"/>), the accessor owns one reusable buffer sized to
/// the slice width instead of allocating per packet. The resulting <see cref="FieldValueData"/>
/// aliases that buffer and is only valid until the next slice on the same accessor, which is
/// exactly the lifetime the predicate needs.
/// </para>
/// </summary>
internal sealed class ValueAccessor
{
    #region Fields

    private readonly byte[] _SliceBuffer;

    #endregion

    #region Construction

    private ValueAccessor(FieldId[] fields, ValueAccessorKind kind, int sliceStart, int sliceEnd)
    {
        Fields = fields;
        Kind = kind;
        SliceStart = sliceStart;
        SliceEnd = sliceEnd;
        _SliceBuffer = kind == ValueAccessorKind.Slice ? new byte[sliceEnd - sliceStart] : [];
    }

    /// <summary>Creates an accessor that passes raw field values through.</summary>
    public static ValueAccessor Direct(FieldId[] fields) =>
        new(fields, ValueAccessorKind.Direct, 0, 0);

    /// <summary>Creates a byte-slice accessor over the half-open range <c>[start, end)</c>.</summary>
    public static ValueAccessor Slice(FieldId[] fields, int start, int end) =>
        new(fields, ValueAccessorKind.Slice, start, end);

    /// <summary>Creates an accessor that yields the value's byte length.</summary>
    public static ValueAccessor Length(FieldId[] fields) =>
        new(fields, ValueAccessorKind.Length, 0, 0);

    #endregion

    #region Properties

    /// <summary>The canonical fields to read; more than one for an alias group.</summary>
    public FieldId[] Fields { get; }

    /// <summary>The transformation applied to each raw value.</summary>
    public ValueAccessorKind Kind { get; }

    /// <summary>Inclusive slice start.</summary>
    public int SliceStart { get; }

    /// <summary>Exclusive slice end.</summary>
    public int SliceEnd { get; }

    #endregion

    #region Transformation

    /// <summary>
    /// Applies the accessor's transformation.
    /// Returns <see langword="false"/> when the raw value cannot supply it — for example a slice
    /// past the end of a byte field, or a length request on a value with no defined width. A
    /// failed transformation makes the occurrence a non-match rather than an error, matching the
    /// "missing data never matches" rule of the language.
    /// </summary>
    public bool TryTransform(in FieldValueData raw, out FieldValueData transformed)
    {
        switch (Kind)
        {
            case ValueAccessorKind.Direct:
                transformed = raw;
                return true;

            case ValueAccessorKind.Length:
                return _TryLength(raw, out transformed);

            default:
                return _TrySlice(raw, out transformed);
        }
    }

    private bool _TrySlice(in FieldValueData raw, out FieldValueData transformed)
    {
        if (raw.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
        {
            return _TryCopySlice(bytes.Span, out transformed);
        }

        Span<byte> fixedBuffer = stackalloc byte[16];
        if (!_TryWriteFixedWidth(raw, fixedBuffer, out int written))
        {
            transformed = default;
            return false;
        }

        return _TryCopySlice(fixedBuffer[..written], out transformed);
    }

    private bool _TryCopySlice(ReadOnlySpan<byte> source, out FieldValueData transformed)
    {
        if (SliceEnd > source.Length)
        {
            transformed = default;
            return false;
        }

        source[SliceStart..SliceEnd].CopyTo(_SliceBuffer);
        transformed = FieldValueData.NewBytes(_SliceBuffer);
        return true;
    }

    private static bool _TryLength(in FieldValueData raw, out FieldValueData transformed)
    {
        if (raw.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
        {
            transformed = FieldValueData.NewU64((ulong)bytes.Length);
            return true;
        }

        if (raw.Type == FieldType.String && raw.TryGetAsString(out string text))
        {
            transformed = FieldValueData.NewU64((ulong)text.Length);
            return true;
        }

        int width = _FixedWidth(raw.Type);
        if (width < 0)
        {
            transformed = default;
            return false;
        }

        transformed = FieldValueData.NewU64((ulong)width);
        return true;
    }

    #endregion

    #region Byte layout

    /// <summary>
    /// Writes the network-order bytes of a fixed-width value so slices work on addresses as well
    /// as on raw byte fields.
    /// </summary>
    private static bool _TryWriteFixedWidth(in FieldValueData raw, Span<byte> destination, out int written)
    {
        written = 0;

        switch (raw.Type)
        {
            case FieldType.MacAddress:
            {
                _ = raw.TryGetAsMacAddress(out MacAddress mac);
                ulong value = mac.RawValue;
                for (int i = 0; i < 6; i++)
                {
                    destination[i] = (byte)(value >> ((5 - i) * 8));
                }
                written = 6;
                return true;
            }

            case FieldType.IPv4Address:
            {
                _ = raw.TryGetAsIPv4(out IPv4Address address);
                BinaryPrimitives.WriteUInt32BigEndian(destination, address.RawValue);
                written = 4;
                return true;
            }

            case FieldType.IPv6Address:
            {
                _ = raw.TryGetAsIPv6(out IPv6Address address);
                BinaryPrimitives.WriteUInt64BigEndian(destination, address.High);
                BinaryPrimitives.WriteUInt64BigEndian(destination[8..], address.Low);
                written = 16;
                return true;
            }

            case FieldType.Eui64:
            {
                _ = raw.TryGetAsEui64(out Eui64 value);
                BinaryPrimitives.WriteUInt64BigEndian(destination, value.RawValue);
                written = 8;
                return true;
            }

            default:
                return false;
        }
    }

    private static int _FixedWidth(FieldType type) => type switch
    {
        FieldType.Bool => 1,
        FieldType.I64 or FieldType.U64 or FieldType.F64 or FieldType.Timestamp or FieldType.Eui64 => 8,
        FieldType.MacAddress => 6,
        FieldType.IPv4Address => 4,
        FieldType.IPv6Address or FieldType.Uuid => 16,
        _ => -1,
    };

    #endregion
}

#endregion
