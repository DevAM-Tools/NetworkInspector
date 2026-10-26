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
/// <b>Algorithm — Compression (fast single-probe LZ4, one hash table pass, not LZ4_HC):</b>
/// The compressor maintains a 64 K-entry hash table (Knuth multiplicative hash,
/// prime 2 654 435 761) keyed on the 4-byte value at each input position.  On
/// each step the table is probed with the current position, and if a match of at
/// least 4 bytes is found within a 64 KB backward window the match is extended
/// forward byte-by-byte.  Only the most recent position per hash slot is kept (no
/// chains), which makes the pass fast but finds fewer matches than a high-compression
/// mode.  All unmatched bytes between the anchor and the match
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
/// <c>offset &lt; matchLength</c> the copy overlaps itself and replicates the
/// run-length-encoding effect (e.g. offset 1 repeats the last byte); it is done in
/// doubling block copies instead of a byte loop (see the comment at the copy).
/// All copies use <see cref="Unsafe"/> (<c>CopyBlockUnaligned</c>) for maximum throughput.
/// </para>
/// <para>
/// <b>Security (untrusted input):</b> <see cref="Decompress"/> reads blocks from capture files that may be hostile or
/// corrupt.  Threats are out-of-bounds reads and writes through crafted lengths and offsets, integer overflow of the
/// length accumulators, and memory exhaustion.  Mitigations: every read and write is checked with an unsigned
/// bounds test before the unchecked copy, the back-reference must lie inside the output already produced, the
/// variable-length accumulators are overflow-checked, and the output size is bounded by the buffer the caller
/// supplies (a decompression bomb cannot write more than the caller allocated).  Residual risk: a truncated block that
/// ends on a literal run is indistinguishable from a complete one, so callers must compare the returned size with the
/// uncompressed size recorded in their container (the BLF reader does).
/// </para>
/// <para>
/// <b>Thread safety:</b> All mutable state lives in a
/// <see cref="ThreadStaticAttribute"/>-backed hash table that is lazily
/// allocated per thread and cleared at the start of every <see cref="Compress"/>
/// call.  Multiple threads may call <see cref="Compress"/> and
/// <see cref="Decompress"/> concurrently without contention or external
/// synchronisation.
/// </para>
/// <para>
/// <b>Memory trade-off:</b> The thread-local hash table is 65 536 × 4 bytes (~256 KB)
/// per thread that ever calls <see cref="Compress"/>. The array is retained until the
/// thread exits — there is no pool return. This avoids <see cref="ArrayPool{T}"/> rent/return
/// overhead and stack exhaustion from a 256 KB <c>stackalloc</c>, but increases steady-state
/// memory when many thread-pool threads compress over their lifetime (~256 KB × active compressors).
/// Each array is above the ~85 KB large-object-heap threshold, so it is allocated once per thread and then reused for
/// the thread's lifetime instead of being allocated per call.
/// Acceptable for dedicated parse/compress worker threads; monitor pool thread count under load.
/// </para>
/// </summary>
public static class Lz4Codec
{
    #region Constants

    private const int _MinMatch = 4;          // minimum match length required by the format
    private const int _HashLog = 16;          // hash table: 2^16 = 65 536 entries
    private const int _HashSize = 1 << _HashLog;
    private const int _MaxInputSizePerBlock = 0x7E000000; // 2 113 929 216 bytes: LZ4_MAX_INPUT_SIZE of the reference implementation
    private const int _MFlimit = 12;          // block format: the last match must start at least 12 bytes before the end of the block
    private const int _LastLiterals = 5;      // block format: the last 5 input bytes are always emitted as literals

    #endregion

    #region Fields

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

    #endregion

    #region Public API

    /// <summary>
    /// Returns the maximum possible compressed size for <paramref name="inputSize"/> bytes.
    /// Allocate at least this many bytes for the <c>output</c> parameter of
    /// <see cref="Compress"/>.
    /// </summary>
    /// <param name="inputSize">Uncompressed input size in bytes.</param>
    /// <returns>
    /// Upper bound on compressed size in bytes, or <c>-1</c> when
    /// <paramref name="inputSize"/> exceeds the LZ4 per-block input limit.
    /// </returns>
    public static int MaxCompressedSize(int inputSize)
    {
        if ((uint)inputSize > (uint)_MaxInputSizePerBlock)
        {
            return -1;
        }

        return inputSize + (inputSize / 255) + 16;
    }

    /// <summary>
    /// Compresses <paramref name="input"/> using the LZ4 block format.
    /// </summary>
    /// <param name="input">Source data to compress.</param>
    /// <param name="output">
    /// Destination buffer.  Must be at least <see cref="MaxCompressedSize"/> bytes.
    /// </param>
    /// <returns>
    /// The number of bytes written to <paramref name="output"/> (0 for empty input), or <c>-1</c> if the
    /// compressed output would be larger than the input (caller should store the data uncompressed), the
    /// input exceeds the per-block limit, or <paramref name="output"/> is too small. An output of exactly the
    /// input size is returned as compressed. Input shorter than 12 bytes always yields <c>-1</c>: it cannot
    /// contain a match and a literal-only block is larger than its input.
    /// </returns>
    /// <remarks>
    /// Expected failures are reported through the return value, not exceptions, because this runs once per block of a
    /// capture and incompressible data is a normal outcome. No parameter validation beyond that is needed: a
    /// <paramref name="output"/> smaller than <see cref="MaxCompressedSize"/> is handled safely (every write is
    /// bounds-checked) but may return <c>-1</c> for compressible data.
    /// </remarks>
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

        int inputLength = input.Length;
        int matchLimit = inputLength - _MFlimit;

        // Too short for any match, so the only possible block is all literals, and that is always larger than the input
        // (token byte plus the payload). The answer is therefore -1 whether or not such a block would fit into the output.
        // Encoding it first, as an earlier version did, wrote bytes into the caller's buffer that nobody can use and cleared
        // the hash table for nothing. Nothing is written to the output for such an input.
        if (inputLength < _MFlimit)
        {
            return -1;
        }

        // Acquire the thread-local hash table (lazily allocated, reused across calls).
        // Clear it before use because position values from the previous call are stale. The clear also establishes the
        // "slot is empty" encoding the match loop relies on: position 0 means no entry (see matchPos <= 0 below).
        int[] hashTableArray = _HashTable ??= new int[_HashSize];
        Span<int> hashTable = hashTableArray.AsSpan(0, _HashSize);
        hashTable.Clear();

        int outputPos = 0;
        int anchor = 0; // first byte of the current unmatched literal run
        int inputPos = 0;

        // Start at position 1: the table is zero-filled and 0 means "empty slot", so position 0 can never be stored as a
        // match candidate. (Byte 0 simply becomes part of the first literal run.)
        inputPos++;
        int step = 1;

        // Main match loop. Invariant: bytes [anchor, inputPos) are not yet emitted. Stops at matchLimit because the block
        // format forbids a match starting in the last 12 bytes; the tail is flushed as literals after the loop.
        while (inputPos < matchLimit)
        {
            int hash = _Hash(input, inputPos);
            int matchPos = hashTable[hash];
            hashTable[hash] = inputPos;

            // Reject: no prior entry (0 = empty slot), too far back (the offset field is 16 bits, so at most 65 535),
            // or a hash collision (the 4 bytes differ).
            if (matchPos <= 0 || inputPos - matchPos > 65535
                || !_SequenceEqual4(input, matchPos, inputPos))
            {
                inputPos += step;
                // Acceleration: widen step on misses (capped at 16) to amortise
                // cost on incompressible blocks. The step grows by one on every miss, so a long run without matches
                // skips up to 15 positions per probe; that trades some ratio on sparse matches for a bounded
                // worst-case time on incompressible data. A match resets the step.
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

            // Back-fill the hash table with the last two positions of the just-consumed match
            // (inputPos - 2 and inputPos - 1) so they can serve as future match candidates; the positions inside the
            // match were skipped by the jump above. Two entries rather than none gives the compressor more
            // candidates on subsequent steps, improving ratio on repetitive data. Guarded by matchLimit because
            // _Hash reads 4 bytes.
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
    /// Destination buffer.  Should be exactly the original (uncompressed) size: a larger buffer is accepted and only
    /// its first <c>return value</c> bytes are written, a smaller one makes the call fail with <c>-1</c>.
    /// Its length is the upper bound on what hostile input can make this method write.
    /// </param>
    /// <returns>
    /// The number of bytes written to <paramref name="output"/> (0 for empty input), or <c>-1</c> on
    /// any format error (truncated data, invalid back-reference, output buffer too small).
    /// Callers must compare a non-negative result with the expected uncompressed size.
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
                if (!_HasInputSpace(inputPos, literalLength, inputLength)
                    || !_HasOutputSpace(outputPos, literalLength, outputLength))
                {
                    return -1; // out of bounds
                }

                // Use Unsafe.CopyBlockUnaligned for bulk copy — fastest path for
                // large literal runs; no overlap is possible here (compressed input and output are distinct buffers).
                // Security: the unchecked copy is only reached after _HasInputSpace / _HasOutputSpace proved that
                // literalLength bytes exist in both buffers; the indexers below bounds-check just the first byte.
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
            if (!_HasInputSpace(inputPos, 2, inputLength))
            {
                return -1;
            }

            int offset = input[inputPos] | (input[inputPos + 1] << 8);
            inputPos += 2;

            // Offset 0 is invalid in the format, and a back-reference may only point into output already produced
            // (outputPos < offset would read before the start of the buffer).
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

            if (!_HasOutputSpace(outputPos, matchLength, outputLength))
            {
                return -1;
            }

            // ---------------------------------------------------------------
            // 6. Copy match from back-reference in the already-written output.
            //
            // Two cases:
            //   a) Non-overlapping (offset >= matchLength): fast bulk copy via
            //      Unsafe.CopyBlockUnaligned.
            //   b) Overlapping (offset < matchLength): the result repeats the
            //      last `offset` bytes (run-length expansion; offset=1 repeats
            //      the last byte matchLength times). The bytes from matchStart
            //      are periodic with period `offset`, so each block copies the
            //      whole readable periodic region (`available` bytes) to its end:
            //      the region doubles per step and no single copy reads what it
            //      writes (source ends at matchStart + available = destination).
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
                // Overlapping: doubling block copies; log2(matchLength / offset) + 1 copies instead of a byte loop.
                // available + block never exceeds matchLength + offset, far below int.MaxValue.
                int available = offset;
                int copied = 0;
                while (copied < matchLength)
                {
                    int block = Math.Min(available, matchLength - copied);
                    Unsafe.CopyBlockUnaligned(
                        ref output[outputPos + copied],
                        ref output[matchStart],
                        (uint)block);
                    copied += block;
                    available += block;
                }
            }

            outputPos += matchLength;
        }

        return outputPos;
    }

    #endregion

    #region Compression Helpers

    /// <summary>
    /// Computes a 16-bit hash index from the 4 bytes starting at <paramref name="pos"/>.
    /// Uses Knuth's multiplicative hash with prime 2 654 435 761: the product wraps modulo 2^32 on purpose and the
    /// top 16 bits (the best mixed ones) form the index. Reads little-endian so the hash of a given byte sequence is
    /// the same on every platform. Callers guarantee that four bytes are available at <paramref name="pos"/>.
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
        if (!_HasOutputSpace(outputPos, literalLength, output.Length))
        {
            return -1;
        }
        input.Slice(literalStart, literalLength).CopyTo(output[outputPos..]);
        outputPos += literalLength;

        // --- 16-bit little-endian offset ---
        if (!_HasOutputSpace(outputPos, 2, output.Length))
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

        if (!_HasOutputSpace(outputPos, literalLength, output.Length))
        {
            return -1;
        }
        input.Slice(anchor, literalLength).CopyTo(output[outputPos..]);
        outputPos += literalLength;

        return outputPos;
    }

    #endregion

    #region Decompression Helpers

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

    /// <summary>
    /// Whether <paramref name="length"/> bytes starting at <paramref name="position"/> fit inside a buffer of
    /// <paramref name="bufferLength"/>. Formulated as <c>length &lt;= bufferLength - position</c> in unsigned
    /// arithmetic so that a huge or negative length cannot overflow an addition and wrap into range (a negative value
    /// becomes a huge uint and is rejected). Identical to <c>_HasOutputSpace</c>; the two names document which buffer
    /// is being checked at each call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _HasInputSpace(int position, int length, int bufferLength) =>
        (uint)position <= (uint)bufferLength
        && (uint)length <= (uint)bufferLength - (uint)position;

    /// <summary>Output-buffer twin of <c>_HasInputSpace</c>; see there for the overflow reasoning.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _HasOutputSpace(int position, int length, int bufferLength) =>
        (uint)position <= (uint)bufferLength
        && (uint)length <= (uint)bufferLength - (uint)position;

    #endregion
}
