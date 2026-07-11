// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Pure managed LZ4 block codec (compression and decompression). No external dependencies.
/// Optimised for block sizes of 4 KB–50 MB (typical capture-file block range).
/// <para>
/// Implements the LZ4 block format as specified at
/// https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
/// </para>
/// <para>
/// <b>Algorithm — Compression (HC-style, one hash table pass):</b>
/// The compressor maintains a 64 K-entry hash table (Knuth multiplicative hash,
/// prime 2 654 435 761) keyed on the 4-byte value at each input position.  On
/// each step the table is probed with the current position, and if a match of at
/// least 4 bytes is found within a 64 KB backward window the match is extended
/// forward byte-by-byte.  All unmatched bytes between the anchor and the match
/// start are emitted as literals.  A token byte encodes the literal-run length
/// (upper nibble) and the match length minus 4 (lower nibble); values ≥ 15 use
/// an extra variable-length byte stream (multiples of 255 followed by a
/// remainder).  The 16-bit match offset is stored little-endian.  Acceleration
/// (skip step 1→16) is applied when no match is found to bound worst-case cost
/// on incompressible data.
/// </para>
/// <para>
/// <b>Algorithm — Decompression:</b>
/// Each sequence reads one token byte, decodes the literal and match lengths
/// using the same variable-length encoding, copies literals from the compressed
/// stream, then copies <c>matchLength</c> bytes from a back-reference
/// <c>offset</c> bytes before the current output position.  When
/// <c>offset &lt; matchLength</c> the copy overlaps itself and is performed
/// byte-by-byte to replicate the run-length-encoding effect (e.g. offset 1
/// repeats the last byte).  For non-overlapping copies
/// <see cref="Unsafe"/> (<c>CopyBlockUnaligned</c>) is used for maximum throughput.
/// </para>
/// <para>
/// <b>Thread safety:</b> All mutable state lives in a
/// <see cref="ThreadStaticAttribute"/>-backed hash table that is lazily
/// allocated per thread and cleared at the start of every <see cref="Compress"/>
/// call.  Multiple threads may call <see cref="Compress"/> and
/// <see cref="Decompress"/> concurrently without contention or external
/// synchronisation.
/// </para>
/// </summary>
public static class Lz4Codec
{
    // =========================================================================
    // LZ4 block-format constants
    // =========================================================================
    private const int _MinMatch = 4;          // minimum match length required by the format
    private const int _HashLog = 16;          // hash table: 2^16 = 65 536 entries
    private const int _HashSize = 1 << _HashLog;
    private const int _MaxInputSizePerBlock = 0x7E000000; // ~2 GB LZ4 hard limit
    private const int _MFlimit = 12;          // minimum input remaining to enter the main match loop
    private const int _LastLiterals = 5;      // last 5 input bytes are always emitted as literals

    // =========================================================================
    // Thread-local hash table
    // =========================================================================
    // A [ThreadStatic] int[] eliminates a 256 KB stackalloc that would exhaust
    // the default 1 MB thread stack in deeply-nested async state machines.
    //
    // Trade-offs vs ArrayPool<int>:
    //   + No Rent/Return ceremony; no try/finally required.
    //   + Zero pool contention; each thread owns its array exclusively.
    //   - Array stays alive until the thread exits — acceptable for thread-pool
    //     threads: one 256 KB array per pool thread.
    //   - Thread-affine; safe because Compress is fully synchronous.
    [ThreadStatic]
    private static int[]? _HashTable;

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Returns the maximum possible compressed size for <paramref name="inputSize"/> bytes.
    /// Allocate at least this many bytes for the <c>output</c> parameter of
    /// <see cref="Compress"/>.
    /// </summary>
    /// <param name="inputSize">Uncompressed input size in bytes.</param>
    /// <returns>Upper bound on compressed size in bytes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MaxCompressedSize(int inputSize) =>
        inputSize + (inputSize / 255) + 16;

    /// <summary>
    /// Compresses <paramref name="input"/> using the LZ4 block format.
    /// </summary>
    /// <param name="input">Source data to compress.</param>
    /// <param name="output">
    /// Destination buffer.  Must be at least <see cref="MaxCompressedSize"/> bytes.
    /// </param>
    /// <returns>
    /// The number of bytes written to <paramref name="output"/>, or <c>-1</c> if the
    /// compressed output would be larger than or equal to the input (caller should
    /// store the data uncompressed).
    /// </returns>
    public static int Compress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length == 0)
        {
            return 0;
        }

        if (input.Length > _MaxInputSizePerBlock)
        {
            return -1;
        }

        // Acquire the thread-local hash table (lazily allocated, reused across calls).
        // Clear it before use because position values from the previous call are stale.
        int[] hashTableArray = _HashTable ??= new int[_HashSize];
        Span<int> hashTable = hashTableArray.AsSpan(0, _HashSize);
        hashTable.Clear();

        int inputLength = input.Length;
        int outputPos = 0;
        int anchor = 0; // first byte of the current unmatched literal run
        int inputPos = 0;
        int matchLimit = inputLength - _MFlimit;

        if (inputLength < _MFlimit)
        {
            // Too short for any match — emit everything as literals.
            outputPos = _EmitLastLiterals(input, output, outputPos, anchor, inputLength);
            if (outputPos < 0)
            {
                return -1;
            }
            if (outputPos <= inputLength)
            {
                return outputPos;
            }
            return -1;
        }

        // Start one byte in so the first hash probe has a left-neighbour.
        inputPos++;
        int step = 1;

        while (inputPos < matchLimit)
        {
            int hash = _Hash(input, inputPos);
            int matchPos = hashTable[hash];
            hashTable[hash] = inputPos;

            // Reject: no prior entry, too far back (>64 KB), or 4-byte mismatch.
            if (matchPos <= 0 || inputPos - matchPos > 65535
                || !_SequenceEqual4(input, matchPos, inputPos))
            {
                inputPos += step;
                // Acceleration: widen step on misses (capped at 16) to amortise
                // cost on incompressible blocks.
                if (step < 16)
                {
                    step++;
                }
                continue;
            }

            step = 1; // reset on match

            int literalLength = inputPos - anchor;

            // Extend match forward byte-by-byte.
            // Stop at inputLength - _LastLiterals so the final 5 bytes are always
            // emitted as literals (required by the LZ4 block format).
            int matchLength = _MinMatch;
            int matchEnd = inputLength - _LastLiterals;
            int maxForward = Math.Min(matchEnd - inputPos, matchEnd - matchPos);
            while (matchLength < maxForward
                   && input[inputPos + matchLength] == input[matchPos + matchLength])
            {
                matchLength++;
            }

            int offset = inputPos - matchPos;

            outputPos = _EmitSequence(output, outputPos, input, anchor, literalLength, offset, matchLength);
            if (outputPos < 0)
            {
                return -1; // output buffer too small
            }

            inputPos += matchLength;
            anchor = inputPos;

            // Back-fill hash table for bytes inside the just-consumed match so
            // they can serve as future match candidates.  Filling every 2 bytes
            // (rather than a single entry) gives the compressor more candidates
            // on subsequent steps, improving ratio on repetitive data.
            if (inputPos < matchLimit)
            {
                hashTable[_Hash(input, inputPos - 2)] = inputPos - 2;
                hashTable[_Hash(input, inputPos - 1)] = inputPos - 1;
            }
        }

        // Emit remaining bytes as the final literal run (no trailing match).
        outputPos = _EmitLastLiterals(input, output, outputPos, anchor, inputLength);

        if (outputPos < 0)
        {
            return -1;
        }

        if (outputPos <= inputLength)
        {
            return outputPos;
        }
        return -1;
    }

    /// <summary>
    /// Decompresses a single LZ4 block from <paramref name="input"/> into
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="input">Compressed LZ4 block data (raw block format, no frame header).</param>
    /// <param name="output">
    /// Destination buffer.  Must be exactly the original (uncompressed) size.
    /// </param>
    /// <returns>
    /// The number of bytes written to <paramref name="output"/>, or <c>-1</c> on
    /// any format error (truncated data, invalid back-reference).
    /// </returns>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length == 0)
        {
            return 0;
        }

        int inputPos = 0;
        int outputPos = 0;
        int inputLength = input.Length;
        int outputLength = output.Length;

        // Each iteration processes one sequence: literals + optional match copy.
        while (inputPos < inputLength)
        {
            // ---------------------------------------------------------------
            // 1. Read token byte.
            // ---------------------------------------------------------------
            int token = input[inputPos++];
            int literalLength = (token >> 4) & 0xF;
            int matchLengthToken = token & 0xF;

            // ---------------------------------------------------------------
            // 2. Decode extended literal length.
            // ---------------------------------------------------------------
            if (literalLength == 15)
            {
                if (!_ReadVarLen(input, ref inputPos, ref literalLength))
                {
                    return -1; // truncated
                }
            }

            // ---------------------------------------------------------------
            // 3. Copy literals from compressed stream to output.
            // ---------------------------------------------------------------
            if (literalLength > 0)
            {
                if (inputPos + literalLength > inputLength || outputPos + literalLength > outputLength)
                {
                    return -1; // out of bounds
                }

                // Use Unsafe.CopyBlockUnaligned for bulk copy — fastest path for
                // large literal runs; no overlap is possible here.
                Unsafe.CopyBlockUnaligned(
                    ref output[outputPos],
                    ref Unsafe.AsRef(in input[inputPos]),
                    (uint)literalLength);

                inputPos += literalLength;
                outputPos += literalLength;
            }

            // The last sequence in a block has literals only (no match).
            if (inputPos >= inputLength)
            {
                break;
            }

            // ---------------------------------------------------------------
            // 4. Read 16-bit little-endian match offset.
            // ---------------------------------------------------------------
            if (inputPos + 2 > inputLength)
            {
                return -1;
            }

            int offset = input[inputPos] | (input[inputPos + 1] << 8);
            inputPos += 2;

            if (offset == 0 || outputPos < offset)
            {
                return -1; // invalid back-reference
            }

            // ---------------------------------------------------------------
            // 5. Decode extended match length (base is minMatch + token nibble).
            // ---------------------------------------------------------------
            int matchLength = matchLengthToken + _MinMatch;
            if (matchLengthToken == 15)
            {
                if (!_ReadVarLen(input, ref inputPos, ref matchLength))
                {
                    return -1;
                }
            }

            if (outputPos + matchLength > outputLength)
            {
                return -1;
            }

            // ---------------------------------------------------------------
            // 6. Copy match from back-reference in the already-written output.
            //
            // Two cases:
            //   a) Non-overlapping (offset >= matchLength): fast bulk copy via
            //      Unsafe.CopyBlockUnaligned.
            //   b) Overlapping (offset < matchLength): the copy intentionally
            //      reads bytes that were just written by the same copy — this
            //      implements run-length expansion (e.g. offset=1 repeats the
            //      last byte matchLength times).  Must be byte-by-byte.
            // ---------------------------------------------------------------
            int matchStart = outputPos - offset;

            if (offset >= matchLength)
            {
                // Non-overlapping: bulk copy.
                Unsafe.CopyBlockUnaligned(
                    ref output[outputPos],
                    ref output[matchStart],
                    (uint)matchLength);
            }
            else
            {
                // Overlapping: byte-by-byte to preserve run-length semantics.
                for (int i = 0; i < matchLength; i++)
                {
                    output[outputPos + i] = output[matchStart + i];
                }
            }

            outputPos += matchLength;
        }

        return outputPos;
    }

    // =========================================================================
    // Private helpers — compression
    // =========================================================================

    /// <summary>
    /// Computes a 16-bit hash index from the 4 bytes starting at <paramref name="pos"/>.
    /// Uses Knuth's multiplicative hash with prime 2 654 435 761.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _Hash(ReadOnlySpan<byte> data, int pos)
    {
        uint val = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        return (int)((val * 2654435761u) >> (32 - _HashLog));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the 4 bytes at <paramref name="pos1"/>
    /// and <paramref name="pos2"/> are identical.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _SequenceEqual4(ReadOnlySpan<byte> data, int pos1, int pos2) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[pos1..])
        == BinaryPrimitives.ReadUInt32LittleEndian(data[pos2..]);

    /// <summary>
    /// Writes one LZ4 literal+match sequence to <paramref name="output"/>.
    /// Returns the updated output position, or <c>-1</c> on overflow.
    /// </summary>
    private static int _EmitSequence(
        Span<byte> output, int outputPos,
        ReadOnlySpan<byte> input, int literalStart,
        int literalLength, int offset, int matchLength)
    {
        // Reserve space for the token byte.
        int tokenPos = outputPos++;
        if (outputPos > output.Length)
        {
            return -1;
        }

        int adjustedMatch = matchLength - _MinMatch;
        int litToken = Math.Min(literalLength, 15);
        int matchToken = Math.Min(adjustedMatch, 15);
        output[tokenPos] = (byte)((litToken << 4) | matchToken);

        // --- Extra literal-length bytes ---
        if (literalLength >= 15)
        {
            int remaining = literalLength - 15;
            while (remaining >= 255)
            {
                if (outputPos >= output.Length)
                {
                    return -1;
                }
                output[outputPos++] = 255;
                remaining -= 255;
            }
            if (outputPos >= output.Length)
            {
                return -1;
            }
            output[outputPos++] = (byte)remaining;
        }

        // --- Literal bytes ---
        if (outputPos + literalLength > output.Length)
        {
            return -1;
        }
        input.Slice(literalStart, literalLength).CopyTo(output[outputPos..]);
        outputPos += literalLength;

        // --- 16-bit little-endian offset ---
        if (outputPos + 2 > output.Length)
        {
            return -1;
        }
        output[outputPos++] = (byte)offset;
        output[outputPos++] = (byte)(offset >> 8);

        // --- Extra match-length bytes ---
        if (adjustedMatch >= 15)
        {
            int remaining = adjustedMatch - 15;
            while (remaining >= 255)
            {
                if (outputPos >= output.Length)
                {
                    return -1;
                }
                output[outputPos++] = 255;
                remaining -= 255;
            }
            if (outputPos >= output.Length)
            {
                return -1;
            }
            output[outputPos++] = (byte)remaining;
        }

        return outputPos;
    }

    /// <summary>
    /// Writes the terminal literal run (input bytes after the last match) to
    /// <paramref name="output"/>.  Returns the updated output position, or
    /// <c>-1</c> on overflow.
    /// </summary>
    private static int _EmitLastLiterals(
        ReadOnlySpan<byte> input, Span<byte> output,
        int outputPos, int anchor, int inputEnd)
    {
        int literalLength = inputEnd - anchor;
        if (literalLength == 0)
        {
            return outputPos;
        }

        int tokenPos = outputPos++;
        if (outputPos > output.Length)
        {
            return -1;
        }

        int litToken = Math.Min(literalLength, 15);
        output[tokenPos] = (byte)(litToken << 4);

        if (literalLength >= 15)
        {
            int remaining = literalLength - 15;
            while (remaining >= 255)
            {
                if (outputPos >= output.Length)
                {
                    return -1;
                }
                output[outputPos++] = 255;
                remaining -= 255;
            }
            if (outputPos >= output.Length)
            {
                return -1;
            }
            output[outputPos++] = (byte)remaining;
        }

        if (outputPos + literalLength > output.Length)
        {
            return -1;
        }
        input.Slice(anchor, literalLength).CopyTo(output[outputPos..]);
        outputPos += literalLength;

        return outputPos;
    }

    // =========================================================================
    // Private helpers — decompression
    // =========================================================================

    /// <summary>
    /// Reads the variable-length continuation of a length field.
    /// Each byte equal to 255 adds 255; the first byte less than 255 is added
    /// as the remainder.  The caller must pass the initial accumulated value
    /// (15 for literals, <c>_MinMatch + 15</c> for matches) in
    /// <paramref name="accumulated"/>; on return it holds the final length.
    /// Returns <see langword="false"/> if the input is truncated.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _ReadVarLen(ReadOnlySpan<byte> input, ref int inputPos, ref int accumulated)
    {
        int inputLength = input.Length;
        byte b;
        do
        {
            if (inputPos >= inputLength)
            {
                return false;
            }
            b = input[inputPos++];
            accumulated += b;
            // Guard against integer overflow caused by a crafted stream of 0xFF bytes.
            // Without this check a negative accumulated value would be cast to a huge
            // uint in Unsafe.CopyBlockUnaligned, causing an out-of-bounds heap write.
            if (accumulated < 0)
            {
                return false;
            }
        }
        while (b == 255);

        return true;
    }
}
