// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// SIMD-accelerated JSON string escaping. Appends escaped UTF-8 bytes (without
/// surrounding quotes) to a <see cref="PooledBuffer"/>.
/// <para>
/// Uses <see cref="Vector256{T}"/> (AVX2-class, 32 bytes/iteration) when available,
/// falls back to <see cref="Vector128{T}"/> (SSE2/AdvSimd, 16 bytes/iteration),
/// then to a scalar loop. Only ASCII control characters (0x00–0x1F), backslash,
/// and double-quote require escaping; all other bytes (including multi-byte UTF-8
/// continuation bytes) pass through unmodified.
/// </para>
/// </summary>
internal static class SimdEscape
{
    /// <summary>Pre-built two-character escape sequences for common control characters.</summary>
    private static ReadOnlySpan<byte> EscapeQuote => "\\\""u8;
    private static ReadOnlySpan<byte> EscapeBackslash => "\\\\"u8;
    private static ReadOnlySpan<byte> EscapeNewline => "\\n"u8;
    private static ReadOnlySpan<byte> EscapeReturn => "\\r"u8;
    private static ReadOnlySpan<byte> EscapeTab => "\\t"u8;
    private static ReadOnlySpan<byte> EscapeBackspace => "\\b"u8;
    private static ReadOnlySpan<byte> EscapeFormFeed => "\\f"u8;

    /// <summary>Hex digits for \uXXXX encoding of uncommon control characters.</summary>
    private static ReadOnlySpan<byte> HexDigits => "0123456789abcdef"u8;

    /// <summary>
    /// Escapes JSON special characters in the input and appends the result to the buffer.
    /// Characters requiring escape: <c>"</c>, <c>\</c>, and control chars 0x00–0x1F.
    /// </summary>
    /// <param name="buffer">The buffer to append escaped bytes to.</param>
    /// <param name="input">UTF-8 encoded input bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EscapeJsonStringTo(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        if (Vector256.IsHardwareAccelerated && input.Length >= 32)
        {
            EscapeVector256(ref buffer, input);
        }
        else if (Vector128.IsHardwareAccelerated && input.Length >= 16)
        {
            EscapeVector128(ref buffer, input);
        }
        else
        {
            EscapeScalar(ref buffer, input);
        }
    }

    /// <summary>
    /// Processes 32 bytes at a time using 256-bit vectors.
    /// For each chunk, checks if any byte needs escaping. If none do, copies
    /// the entire 32-byte block directly. Otherwise, copies the safe prefix
    /// and escapes the first special character, then continues.
    /// </summary>
    private static void EscapeVector256(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
    {
        // Comparison vectors for characters requiring JSON escaping
        Vector256<byte> vQuote = Vector256.Create((byte)0x22);     // '"'
        Vector256<byte> vBackslash = Vector256.Create((byte)0x5C); // '\\'
        Vector256<byte> vSpace = Vector256.Create((byte)0x20);     // ' ' — everything below needs escape

        int i = 0;
        while (i + 32 <= input.Length)
        {
            Vector256<byte> chunk = Vector256.LoadUnsafe(
                ref MemoryMarshal.GetReference(input.Slice(i)));

            // Build a mask of bytes needing escape: control chars OR quote OR backslash
            Vector256<byte> needsEscape = Vector256.BitwiseOr(
                Vector256.BitwiseOr(
                    Vector256.Equals(chunk, vQuote),
                    Vector256.Equals(chunk, vBackslash)),
                Vector256.LessThan(chunk, vSpace));

            if (needsEscape == Vector256<byte>.Zero)
            {
                // Fast path: no escaping needed — copy 32 bytes directly
                buffer.Write(input.Slice(i, 32));
                i += 32;
            }
            else
            {
                // Slow path: copy safe prefix, escape special char, advance
                uint mask = needsEscape.ExtractMostSignificantBits();
                int safeCount = BitOperations.TrailingZeroCount(mask);
                if (safeCount > 0)
                {
                    buffer.Write(input.Slice(i, safeCount));
                }
                i += safeCount;
                WriteEscapedByte(ref buffer, input[i]);
                i++;
            }
        }

        // Handle remaining bytes with scalar fallback
        if (i < input.Length)
        {
            EscapeScalar(ref buffer, input.Slice(i));
        }
    }

    /// <summary>
    /// Processes 16 bytes at a time using 128-bit vectors.
    /// Same algorithm as <see cref="EscapeVector256"/> but with half the width.
    /// </summary>
    private static void EscapeVector128(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
    {
        Vector128<byte> vQuote = Vector128.Create((byte)0x22);
        Vector128<byte> vBackslash = Vector128.Create((byte)0x5C);
        Vector128<byte> vSpace = Vector128.Create((byte)0x20);

        int i = 0;
        while (i + 16 <= input.Length)
        {
            Vector128<byte> chunk = Vector128.LoadUnsafe(
                ref MemoryMarshal.GetReference(input.Slice(i)));

            Vector128<byte> needsEscape = Vector128.BitwiseOr(
                Vector128.BitwiseOr(
                    Vector128.Equals(chunk, vQuote),
                    Vector128.Equals(chunk, vBackslash)),
                Vector128.LessThan(chunk, vSpace));

            if (needsEscape == Vector128<byte>.Zero)
            {
                buffer.Write(input.Slice(i, 16));
                i += 16;
            }
            else
            {
                uint mask = needsEscape.ExtractMostSignificantBits();
                int safeCount = BitOperations.TrailingZeroCount(mask);
                if (safeCount > 0)
                {
                    buffer.Write(input.Slice(i, safeCount));
                }
                i += safeCount;
                WriteEscapedByte(ref buffer, input[i]);
                i++;
            }
        }

        if (i < input.Length)
        {
            EscapeScalar(ref buffer, input.Slice(i));
        }
    }

    /// <summary>
    /// Byte-by-byte scalar fallback for short inputs or remainder after SIMD processing.
    /// Copies safe ranges in bulk where possible to minimize per-byte overhead.
    /// </summary>
    private static void EscapeScalar(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
    {
        int start = 0; // start of current safe range
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == 0x22 || b == 0x5C || b < 0x20)
            {
                // Flush the safe range before this special character
                if (i > start)
                {
                    buffer.Write(input.Slice(start, i - start));
                }
                WriteEscapedByte(ref buffer, b);
                start = i + 1;
            }
        }

        // Flush remaining safe bytes
        if (start < input.Length)
        {
            buffer.Write(input.Slice(start));
        }
    }

    /// <summary>
    /// Writes the JSON escape sequence for a single byte.
    /// Common characters get two-character sequences (\n, \t, etc.);
    /// uncommon control characters get the \u00XX form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEscapedByte(ref PooledBuffer buffer, byte b)
    {
        switch (b)
        {
            case 0x22:
                buffer.Write(EscapeQuote);
                break;       // "
            case 0x5C:
                buffer.Write(EscapeBackslash);
                break;   // \.
            case 0x0A:
                buffer.Write(EscapeNewline);
                break;     // \n
            case 0x0D:
                buffer.Write(EscapeReturn);
                break;      // \r
            case 0x09:
                buffer.Write(EscapeTab);
                break;         // \t
            case 0x08:
                buffer.Write(EscapeBackspace);
                break;   // \b
            case 0x0C:
                buffer.Write(EscapeFormFeed);
                break;    // \f
            default:
                // Uncommon control character → \u00XX
                Span<byte> escape = stackalloc byte[6];
                escape[0] = (byte)'\\';
                escape[1] = (byte)'u';
                escape[2] = (byte)'0';
                escape[3] = (byte)'0';
                escape[4] = HexDigits[b >> 4];
                escape[5] = HexDigits[b & 0x0F];
                buffer.Write(escape);
                break;
        }
    }
}
