// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Manual protobuf wire format encoder. Zero allocations after warm-up.
/// Supports varint (LEB128), fixed32, fixed64, length-delimited, and zigzag sint64.
/// Writes to <see cref="PooledBuffer"/> for zero-allocation hot path.
/// </summary>
internal static class ProtobufEncoder
{
    /// <summary>Maximum number of bytes a varint can occupy (ceil(64/7) = 10).</summary>
    private const int MaxVarintLength = 10;

    // -----------------------------------------------------------------------
    // Thread-local UTF-8 scratch buffer for WriteString
    // -----------------------------------------------------------------------
    // Strings shorter than ≈256 UTF-8 bytes are encoded into a stackalloc buffer.
    // Strings requiring more space use this [ThreadStatic] byte[] which grows to
    // accommodate the largest string seen on a given thread and is then reused.
    //
    // Trade-offs vs ArrayPool<byte>:
    //   + No Rent/Return ceremony; no try/finally in the caller.
    //   + No pool contention; each thread owns its buffer exclusively.
    //   - Buffer stays alive until the thread exits; on thread-pool threads this
    //     is acceptable (one small byte[] per worker).
    //   - Thread-affine; WriteString is fully synchronous, so no await-point risk.
    [ThreadStatic]
    private static byte[]? _Utf8Scratch;

    /// <summary>Writes an unsigned varint (LEB128) to buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteVarint(ref PooledBuffer buffer, ulong value)
    {
        Span<byte> scratch = stackalloc byte[MaxVarintLength];
        int pos = 0;
        while (value > 0x7F)
        {
            scratch[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }
        scratch[pos++] = (byte)value;
        buffer.Write(scratch[..pos]);
    }

    /// <summary>Writes a protobuf field tag (field_number &lt;&lt; 3 | wire_type).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteTag(ref PooledBuffer buffer, int fieldNumber, int wireType) =>
        WriteVarint(ref buffer, (ulong)((fieldNumber << 3) | wireType));

    /// <summary>Writes a varint field (tag + varint value).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteVarintField(ref PooledBuffer buffer, int fieldNumber, ulong value)
    {
        WriteTag(ref buffer, fieldNumber, 0); // wire type 0 = varint
        WriteVarint(ref buffer, value);
    }

    /// <summary>Writes a length-delimited field (tag + length varint + data).</summary>
    internal static void WriteLengthDelimited(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<byte> data)
    {
        WriteTag(ref buffer, fieldNumber, 2); // wire type 2 = length-delimited
        WriteVarint(ref buffer, (ulong)data.Length);
        buffer.Write(data);
    }

    /// <summary>Writes a sint64 field with zigzag encoding (tag + varint).</summary>
    internal static void WriteSint64(ref PooledBuffer buffer, int fieldNumber, long value)
    {
        WriteTag(ref buffer, fieldNumber, 0);
        WriteVarint(ref buffer, ZigZagEncode(value));
    }

    /// <summary>Zigzag encodes a signed int64 for efficient variable-length encoding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>Writes a UTF-8 string field (tag + length + UTF-8 bytes).</summary>
    internal static void WriteString(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<char> value)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (maxBytes <= 256)
        {
            // Small strings: use the stack (no allocation, no pool).
            Span<byte> utf8 = stackalloc byte[maxBytes];
            int written = Encoding.UTF8.GetBytes(value, utf8);
            WriteLengthDelimited(ref buffer, fieldNumber, utf8[..written]);
        }
        else
        {
            // Large strings: use a thread-local scratch buffer that grows to the
            // maximum ever seen on this thread. Avoids ArrayPool rent/return and
            // the new byte[] allocation from the original code (HIGH-2 fix).
            if (_Utf8Scratch is null || _Utf8Scratch.Length < maxBytes)
            {
                _Utf8Scratch = new byte[maxBytes];
            }
            int written = Encoding.UTF8.GetBytes(value, _Utf8Scratch);
            WriteLengthDelimited(ref buffer, fieldNumber, _Utf8Scratch.AsSpan(0, written));
        }
    }

    /// <summary>Writes a bool field (tag + varint 0 or 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteBool(ref PooledBuffer buffer, int fieldNumber, bool value) =>
        WriteVarintField(ref buffer, fieldNumber, value ? 1UL : 0UL);

    /// <summary>Writes a double field as fixed64 (tag + 8 bytes IEEE 754).</summary>
    internal static void WriteDouble(ref PooledBuffer buffer, int fieldNumber, double value)
    {
        WriteTag(ref buffer, fieldNumber, 1); // wire type 1 = 64-bit
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(scratch, value);
        buffer.Write(scratch);
    }

}
