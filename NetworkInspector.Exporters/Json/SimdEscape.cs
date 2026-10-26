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
/// continuation bytes) pass through unmodified. This is exactly the set RFC 8259 requires to be escaped:
/// DEL (0x7F) and U+2028/U+2029 are legal inside a JSON string and are deliberately left as they are.
/// </para>
/// <para>
/// Why SIMD: the typical string contains nothing to escape, so the common case is a membership test over many bytes
/// followed by one bulk copy. A vector compare answers that for 16 or 32 bytes at once, where a byte loop pays one
/// branch per byte and one <c>Write</c> call per unescaped run. Strings that need escaping fall back to a
/// per-special-byte path, so the result is byte-for-byte identical on every code path.
/// </para>
/// <para>
/// Thread safety: stateless; safe from any thread as long as each caller uses its own <see cref="PooledBuffer"/>.
/// </para>
/// </summary>
internal static class SimdEscape
{
    /// <summary>
    /// Pre-built two-character escape sequences for quote, backslash and the control characters JSON has short forms for.
    /// UTF-8 literal properties are served from static data, so reading them never allocates.
    /// </summary>
    private static ReadOnlySpan<byte> _EscapeQuote => "\\\""u8;
    private static ReadOnlySpan<byte> _EscapeBackslash => "\\\\"u8;
    private static ReadOnlySpan<byte> _EscapeNewline => "\\n"u8;
    private static ReadOnlySpan<byte> _EscapeReturn => "\\r"u8;
    private static ReadOnlySpan<byte> _EscapeTab => "\\t"u8;
    private static ReadOnlySpan<byte> _EscapeBackspace => "\\b"u8;
    private static ReadOnlySpan<byte> _EscapeFormFeed => "\\f"u8;

    /// <summary>Hex digits for \uXXXX encoding of uncommon control characters.</summary>
    private static ReadOnlySpan<byte> _HexDigits => "0123456789abcdef"u8;

    /// <summary>
    /// Escapes JSON special characters in the input and appends the result to the buffer.
    /// Characters requiring escape: <c>"</c>, <c>\</c>, and control chars 0x00–0x1F.
    /// The input must already be UTF-8; it is not validated and bytes of 0x80 or above are copied unchanged.
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

        // Pick the widest vector path that the hardware supports and the input can fill at least once.
        // IsHardwareAccelerated is a JIT-time constant, so the untaken branches are removed from the compiled code.
        // The method is marked for aggressive inlining so this small dispatch costs no call for short strings.
        // Inputs shorter than one vector (and every tail of a vector path) use the scalar loop, because a partial
        // vector load would read past the end of the span.
        if (Vector256.IsHardwareAccelerated && input.Length >= 32)
        {
            _EscapeVector256(ref buffer, input);
        }
        else if (Vector128.IsHardwareAccelerated && input.Length >= 16)
        {
            _EscapeVector128(ref buffer, input);
        }
        else
        {
            _EscapeScalar(ref buffer, input);
        }
    }

    /// <summary>
    /// Processes 32 bytes at a time using 256-bit vectors.
    /// For each chunk, checks if any byte needs escaping. If none do, copies
    /// the entire 32-byte block directly. Otherwise, copies the safe prefix
    /// and escapes the first special character, then continues. The final partial chunk (fewer than 32 bytes)
    /// is handled by the scalar fallback.
    /// </summary>
    private static void _EscapeVector256(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
    {
        // Comparison vectors for characters requiring JSON escaping
        Vector256<byte> vQuote = Vector256.Create((byte)0x22);     // '"'
        Vector256<byte> vBackslash = Vector256.Create((byte)0x5C); // '\\'
        // ' ' — everything below needs escape. LessThan on a vector of bytes is an unsigned compare, so bytes of 0x80
        // and above (UTF-8 lead and continuation bytes) are correctly NOT flagged as control characters.
        Vector256<byte> vSpace = Vector256.Create((byte)0x20);

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
                // Slow path: copy safe prefix, escape special char, advance. Each comparison lane is all ones when it
                // matched, so the most-significant-bit mask has bit k set exactly when byte k needs escaping and the
                // trailing zero count is the length of the safe prefix. Only the first special byte is handled per
                // iteration; the next iteration reloads at the new offset, which keeps the code branch-light and is
                // fine because strings with many escapes are rare. The mask is never zero here (checked above), so
                // TrailingZeroCount is below 32 and input[i] stays in range.
                uint mask = needsEscape.ExtractMostSignificantBits();
                int safeCount = BitOperations.TrailingZeroCount(mask);
                if (safeCount > 0)
                {
                    buffer.Write(input.Slice(i, safeCount));
                }
                i += safeCount;
                _WriteEscapedByte(ref buffer, input[i]);
                i++;
            }
        }

        // Handle remaining bytes with scalar fallback
        if (i < input.Length)
        {
            _EscapeScalar(ref buffer, input.Slice(i));
        }
    }

    /// <summary>
    /// Processes 16 bytes at a time using 128-bit vectors.
    /// Same algorithm as <see cref="_EscapeVector256"/> but with half the width: it serves hardware without 256-bit
    /// vectors (SSE2, AdvSimd) and inputs of 16 to 31 bytes. See the 256-bit variant for the reasoning behind each step.
    /// </summary>
    private static void _EscapeVector128(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
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
                _WriteEscapedByte(ref buffer, input[i]);
                i++;
            }
        }

        if (i < input.Length)
        {
            _EscapeScalar(ref buffer, input.Slice(i));
        }
    }

    /// <summary>
    /// Byte-by-byte scalar fallback for short inputs or remainder after SIMD processing.
    /// Copies safe ranges in bulk where possible to minimize per-byte overhead.
    /// </summary>
    private static void _EscapeScalar(ref PooledBuffer buffer, ReadOnlySpan<byte> input)
    {
        // Tracks the start of the current run of bytes that need no escaping so each run is appended with one bulk
        // copy instead of one Write call per byte.
        int start = 0;
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
                _WriteEscapedByte(ref buffer, b);
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
    /// uncommon control characters get the \u00XX form (lowercase hex digits, as RFC 8259 allows).
    /// Callers pass only quote, backslash or a byte below 0x20; any other byte would be mis-encoded as a \u00XX escape.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _WriteEscapedByte(ref PooledBuffer buffer, byte b)
    {
        switch (b)
        {
            case 0x22:
                buffer.Write(_EscapeQuote);
                break;       // "
            case 0x5C:
                buffer.Write(_EscapeBackslash);
                break;   // backslash
            case 0x0A:
                buffer.Write(_EscapeNewline);
                break;     // \n
            case 0x0D:
                buffer.Write(_EscapeReturn);
                break;      // \r
            case 0x09:
                buffer.Write(_EscapeTab);
                break;         // \t
            case 0x08:
                buffer.Write(_EscapeBackspace);
                break;   // \b
            case 0x0C:
                buffer.Write(_EscapeFormFeed);
                break;    // \f
            default:
                // Uncommon control character → \u00XX. JSON has no short form for these, and they may not appear raw.
                // The 6-byte stack buffer is the exact length of the escape, so no allocation is involved.
                Span<byte> escape = stackalloc byte[6];
                escape[0] = (byte)'\\';
                escape[1] = (byte)'u';
                escape[2] = (byte)'0';
                escape[3] = (byte)'0';
                escape[4] = _HexDigits[b >> 4];
                escape[5] = _HexDigits[b & 0x0F];
                buffer.Write(escape);
                break;
        }
    }
}
