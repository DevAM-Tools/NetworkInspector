// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Manual protobuf wire format encoder. Zero allocations after warm-up.
/// Supports varint (LEB128), fixed32 (IPv4 addresses), fixed64 (doubles, EUI-64, address halves), packed fixed-width columns,
/// length-delimited, and zigzag sint64.
/// Writes to <see cref="PooledBuffer"/> for zero-allocation hot path.
/// <para>
/// Why hand-written instead of a protobuf library: the PBF layout is encoded straight from the packet field tree into
/// pooled buffers, per packet and per field value. A generated message class would need an object graph per packet,
/// which is the allocation this exporter avoids. The encoder implements only the wire types the format uses
/// (0 = varint, 1 = 64-bit, 2 = length-delimited, 5 = 32-bit). Field numbers must be positive and below 2^29 (the protobuf limit);
/// they come from <see cref="PbfFieldNumbers"/> and are not validated here.
/// </para>
/// <para>
/// Thread safety: stateless; safe from any thread as long as each caller uses its own <see cref="PooledBuffer"/>.
/// </para>
/// </summary>
internal static class ProtobufEncoder
{
    #region Varints and tags

    /// <summary>Maximum number of bytes a varint can occupy (ceil(64/7) = 10).</summary>
    private const int _MaxVarintLength = 10;

    /// <summary>Writes an unsigned varint (LEB128) to <paramref name="buffer"/>: 7 payload bits per byte, low group first.</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="value">The value; 1 to 10 bytes on the wire.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteVarint(ref PooledBuffer buffer, ulong value)
    {
        // The varint is assembled in a stack buffer and appended with a single Write, so the buffer's capacity check
        // runs once per number instead of once per byte. 7 payload bits go into each byte; the high bit (0x80) marks
        // "more bytes follow", least-significant group first.
        Span<byte> scratch = stackalloc byte[_MaxVarintLength];
        int pos = 0;
        while (value > 0x7F)
        {
            scratch[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }
        scratch[pos++] = (byte)value;
        buffer.Write(scratch[..pos]);
    }

    /// <summary>The number of bytes <see cref="WriteVarint"/> writes for <paramref name="value"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>1 to 10 bytes.</returns>
    /// <remarks>
    /// Perf: a length-delimited message whose content is a few small fields plus a large byte payload can write its length first
    /// from this size and then the parts straight into the output buffer, so the payload is copied once instead of twice (into a
    /// scratch buffer and then into the output). Related: <see cref="WriteVarint"/> (the size must match what it writes).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VarintSize(ulong value)
    {
        // Seven payload bits per byte; a value of 0 still takes one byte. The bit length of (value | 1) avoids a zero special case.
        int bitLength = 64 - BitOperations.LeadingZeroCount(value | 1);
        return (bitLength + 6) / 7;
    }

    /// <summary>Writes a protobuf field tag (field_number &lt;&lt; 3 | wire_type) as a varint.</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number; positive and below 2^29.</param>
    /// <param name="wireType">The wire type: 0 = varint, 1 = 64-bit, 2 = length-delimited, 5 = 32-bit.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteTag(ref PooledBuffer buffer, int fieldNumber, int wireType) =>
        WriteVarint(ref buffer, (ulong)((fieldNumber << 3) | wireType));

    /// <summary>Writes a varint field (tag + varint value).</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="value">The unsigned value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteVarintField(ref PooledBuffer buffer, int fieldNumber, ulong value)
    {
        WriteTag(ref buffer, fieldNumber, 0); // wire type 0 = varint
        WriteVarint(ref buffer, value);
    }

    /// <summary>Writes a sint64 field with zigzag encoding (tag + varint).</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="value">The signed value; small magnitudes of either sign stay short.</param>
    internal static void WriteSint64(ref PooledBuffer buffer, int fieldNumber, long value)
    {
        WriteTag(ref buffer, fieldNumber, 0);
        WriteVarint(ref buffer, ZigZagEncode(value));
    }

    /// <summary>
    /// Zigzag encodes a signed int64 for efficient variable-length encoding: 0, -1, 1, -2, 2 map to 0, 1, 2, 3, 4.
    /// A plain two's-complement negative would always occupy 10 varint bytes; zigzag keeps small magnitudes short
    /// regardless of sign, which matters for delta-encoded timestamps and parent node IDs of -1.
    /// </summary>
    /// <param name="value">The signed value.</param>
    /// <returns>The unsigned zigzag form, ready for <see cref="WriteVarint"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>Writes a bool field (tag + varint 0 or 1).</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="value">The value, written as 1 for <see langword="true"/> and 0 for <see langword="false"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteBool(ref PooledBuffer buffer, int fieldNumber, bool value) =>
        WriteVarintField(ref buffer, fieldNumber, value ? 1UL : 0UL);

    #endregion

    #region Length-delimited and fixed-width values

    /// <summary>
    /// Writes a length-delimited field (tag + length varint + data). Nested messages are written by encoding the
    /// child into its own buffer first, because the length prefix must precede the content and is unknown until the
    /// child is complete.
    /// </summary>
    /// <param name="buffer">The output buffer; grows on demand. The data must not alias this buffer's own array.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="data">The payload bytes (a nested message or raw bytes); may be empty.</param>
    internal static void WriteLengthDelimited(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<byte> data)
    {
        WriteTag(ref buffer, fieldNumber, 2); // wire type 2 = length-delimited
        WriteVarint(ref buffer, (ulong)data.Length);
        buffer.Write(data);
    }

    /// <summary>Writes a UTF-8 string field (tag + length + UTF-8 bytes).</summary>
    /// <param name="buffer">Output buffer; the UTF-8 bytes are encoded directly into it.</param>
    /// <param name="fieldNumber">Protobuf field number.</param>
    /// <param name="value">Text to encode. Lone surrogates are replaced by U+FFFD, exactly as <see cref="Encoding.UTF8"/> does.</param>
    internal static void WriteString(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<char> value)
    {
        // The exact UTF-8 length is known up front, so the length prefix can be written first and the text
        // encoded straight into the output buffer. No intermediate scratch buffer is needed, so nothing
        // outlives the exporter. GetByteCount and GetBytes apply the same replacement-fallback rules, so the
        // reserved span is always filled completely.
        // Rejected alternative: encode into a thread-static scratch buffer and copy; that keeps a buffer alive per
        // thread after the export ends and costs a second copy of every string. The price of this approach is a
        // second pass over the text (count, then encode), which is cheap next to the copy.
        // Reserve already advances Length, and the destination span is filled before any other write to the
        // buffer, so a growth of the buffer cannot invalidate it.
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteTag(ref buffer, fieldNumber, 2); // wire type 2 = length-delimited
        WriteVarint(ref buffer, (ulong)byteCount);
        Span<byte> destination = buffer.Reserve(byteCount);
        int written = Encoding.UTF8.GetBytes(value, destination);
        System.Diagnostics.Debug.Assert(written == byteCount, "UTF-8 GetBytes must write exactly GetByteCount bytes.");
    }

    /// <summary>Writes a double field as fixed64 (tag + 8 bytes IEEE 754).</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="value">The value; written little-endian whatever the host byte order.</param>
    internal static void WriteDouble(ref PooledBuffer buffer, int fieldNumber, double value)
    {
        WriteTag(ref buffer, fieldNumber, 1); // wire type 1 = 64-bit
        // Protobuf fixes fixed64 as little-endian regardless of the host byte order, hence the explicit conversion
        // (also correct on big-endian hosts).
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(scratch, value);
        buffer.Write(scratch);
    }

    /// <summary>Writes a fixed32 field (tag + 4 bytes little-endian), the wire form of an IPv4 address in the PBF value fields.</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="value">The unsigned value; written little-endian whatever the host byte order.</param>
    /// <remarks>
    /// Why fixed width: an address uses all 32 bits, so a varint would need 5 bytes for the upper half of the range and a reader
    /// could not index it; fixed32 is always 4 bytes, the size of the address.
    /// </remarks>
    internal static void WriteFixed32Field(ref PooledBuffer buffer, int fieldNumber, uint value)
    {
        WriteTag(ref buffer, fieldNumber, 5); // wire type 5 = 32-bit
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        buffer.Write(scratch);
    }

    /// <summary>Writes a fixed64 field (tag + 8 bytes little-endian), the wire form of an EUI-64 and of the halves of a 128-bit value.</summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number.</param>
    /// <param name="value">The unsigned value; written little-endian whatever the host byte order.</param>
    /// <remarks>
    /// Why fixed width: the halves of an IPv6 address or a UUID are uniformly distributed, so a varint would need 9 or 10 bytes
    /// for most of them; fixed64 is always 8.
    /// </remarks>
    internal static void WriteFixed64Field(ref PooledBuffer buffer, int fieldNumber, ulong value)
    {
        WriteTag(ref buffer, fieldNumber, 1); // wire type 1 = 64-bit
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(scratch, value);
        buffer.Write(scratch);
    }

    #endregion

    #region Packed fixed-width columns

    /// <summary>
    /// Starts a packed repeated fixed32 field: writes its tag and its length, and reserves the payload of
    /// <paramref name="count"/> values for the caller to fill.
    /// </summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number; the field is length-delimited (wire type 2) with <paramref name="count"/> * 4 bytes.</param>
    /// <param name="count">The number of values, 0 or more; 0 writes an empty field.</param>
    /// <returns>
    /// The payload, <paramref name="count"/> * 4 bytes. Value <c>i</c> goes to <c>payload[i * 4..]</c> through
    /// <see cref="BinaryPrimitives.WriteUInt32LittleEndian(Span{byte}, uint)"/> (protobuf fixes little-endian). The caller must fill
    /// it before the next write to <paramref name="buffer"/>, because growing the buffer moves its array.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="OverflowException"><paramref name="count"/> * 4 does not fit an <see cref="int"/> (a block is bounded far below).</exception>
    /// <remarks>
    /// Perf: a packed column needs its length up front, and the length is known from the row count, so the values are written
    /// straight into the output buffer: no scratch array and no second copy. Rejected alternative: a typed span parameter, which
    /// would force the caller to build a <c>uint[]</c> per column first.
    /// </remarks>
    internal static Span<byte> ReservePackedFixed32(ref PooledBuffer buffer, int fieldNumber, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int byteCount = checked(count * 4);
        WriteTag(ref buffer, fieldNumber, 2); // wire type 2 = length-delimited (packed)
        WriteVarint(ref buffer, (ulong)byteCount);
        return buffer.Reserve(byteCount);
    }

    /// <summary>
    /// Starts a packed repeated fixed64 field: writes its tag and its length, and reserves the payload of
    /// <paramref name="count"/> values for the caller to fill.
    /// </summary>
    /// <param name="buffer">The output buffer; grows on demand.</param>
    /// <param name="fieldNumber">The protobuf field number; the field is length-delimited (wire type 2) with <paramref name="count"/> * 8 bytes.</param>
    /// <param name="count">The number of values, 0 or more; 0 writes an empty field.</param>
    /// <returns>
    /// The payload, <paramref name="count"/> * 8 bytes. Value <c>i</c> goes to <c>payload[i * 8..]</c> through
    /// <see cref="BinaryPrimitives.WriteUInt64LittleEndian(Span{byte}, ulong)"/>. The caller must fill it before the next write to
    /// <paramref name="buffer"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="OverflowException"><paramref name="count"/> * 8 does not fit an <see cref="int"/>.</exception>
    /// <remarks>Perf: see <see cref="ReservePackedFixed32"/>.</remarks>
    internal static Span<byte> ReservePackedFixed64(ref PooledBuffer buffer, int fieldNumber, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int byteCount = checked(count * 8);
        WriteTag(ref buffer, fieldNumber, 2); // wire type 2 = length-delimited (packed)
        WriteVarint(ref buffer, (ulong)byteCount);
        return buffer.Reserve(byteCount);
    }

    #endregion
}
