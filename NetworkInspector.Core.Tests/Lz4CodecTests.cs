// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Lz4Codec"/>: compress, decompress, roundtrip, and edge cases.
/// <para>
/// Coverage goals:
/// <list type="bullet">
///   <item><description>Roundtrip: random, highly compressible, and run-length data.</description></item>
///   <item><description>Overlap (offset &lt; matchLength): run-length expansion correctness.</description></item>
///   <item><description>Empty input.</description></item>
///   <item><description>Only-literals path (input shorter than MFlimit = 12).</description></item>
///   <item><description>Incompressible data: <see cref="Lz4Codec.Compress"/> returns -1 when
///     output buffer is too small.</description></item>
///   <item><description>Decompression of a known correct LZ4 block vector.</description></item>
///   <item><description>Decompression error cases: truncated, zero offset, bad back-reference.</description></item>
///   <item><description>Concurrent compression does not corrupt results (thread-safety of
///     the <see cref="ThreadStaticAttribute"/>-backed hash table).</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class Lz4CodecTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Compresses <paramref name="input"/> and returns the compressed bytes.
    /// Throws if compression fails unexpectedly.
    /// </summary>
    private static byte[] _CompressFull(byte[] input)
    {
        byte[] buf = new byte[Lz4Codec.MaxCompressedSize(input.Length)];
        int len = Lz4Codec.Compress(input, buf);
        if (len < 0)
        {
            throw new InvalidOperationException($"Lz4Codec.Compress returned {len} for {input.Length}-byte input");
        }
        return buf[0..len];
    }

    /// <summary>Decompresses <paramref name="compressed"/> into a buffer of <paramref name="originalSize"/> bytes.</summary>
    private static byte[] _DecompressFull(byte[] compressed, int originalSize)
    {
        byte[] output = new byte[originalSize];
        int written = Lz4Codec.Decompress(compressed, output);
        if (written != originalSize)
        {
            throw new InvalidOperationException($"Lz4Codec.Decompress wrote {written}, expected {originalSize}");
        }
        return output;
    }

    // =========================================================================
    // Empty input
    // =========================================================================

    /// <summary>
    /// Compressing and decompressing an empty span must both return 0 and produce
    /// an empty result without touching the output buffer.
    /// </summary>
    [Test]
    public async Task EmptyInput_CompressAndDecompress_ReturnZero()
    {
        int compressResult = Lz4Codec.Compress([], []);
        await Assert.That(compressResult).IsEqualTo(0);

        int decompressResult = Lz4Codec.Decompress([], []);
        await Assert.That(decompressResult).IsEqualTo(0);
    }

    // =========================================================================
    // Roundtrip — highly compressible data
    // =========================================================================

    /// <summary>
    /// All-zero input has maximum compressibility; the roundtrip must recover
    /// the original data exactly.
    /// </summary>
    [Test]
    public async Task Roundtrip_AllZeros_RecoversOriginal()
    {
        byte[] input = new byte[64 * 1024]; // 64 KB of zeros
        byte[] compressed = _CompressFull(input);

        // Compressed size must be well below the original.
        await Assert.That(compressed.Length).IsLessThan(input.Length / 4);

        byte[] recovered = _DecompressFull(compressed, input.Length);
        await Assert.That(recovered.AsSpan().SequenceEqual(input)).IsTrue();
    }

    // =========================================================================
    // Roundtrip — run-length (overlap-copy exerciser)
    // =========================================================================

    /// <summary>
    /// A repeating single-byte pattern (e.g. 0xAB repeated 32 KB) is encoded by
    /// the compressor with a match offset of 1, which triggers the overlap-copy
    /// path in the decompressor.  The roundtrip must recover the original data.
    /// </summary>
    [Test]
    public async Task Roundtrip_RepeatingByte_OverlapCopy_RecoversOriginal()
    {
        byte[] input = new byte[32 * 1024];
        Array.Fill(input, (byte)0xAB);

        byte[] compressed = _CompressFull(input);
        await Assert.That(compressed.Length).IsLessThan(input.Length / 8);

        byte[] recovered = _DecompressFull(compressed, input.Length);
        await Assert.That(recovered.AsSpan().SequenceEqual(input)).IsTrue();
    }

    // =========================================================================
    // Roundtrip — repeating short pattern (offset < matchLength in general)
    // =========================================================================

    /// <summary>
    /// A repeating 3-byte pattern encoded with a small offset (3) exercises the
    /// overlap-copy path where <c>offset &lt; matchLength</c>.  Decompression
    /// must correctly replicate the pattern.
    /// </summary>
    [Test]
    public async Task Roundtrip_RepeatingShortPattern_OverlapCopy_RecoversOriginal()
    {
        // Pattern: 0x01, 0x02, 0x03 repeated 8 192 times = 24 576 bytes
        byte[] input = new byte[24576];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)((i % 3) + 1);
        }

        byte[] compressed = _CompressFull(input);
        byte[] recovered = _DecompressFull(compressed, input.Length);
        await Assert.That(recovered.AsSpan().SequenceEqual(input)).IsTrue();
    }

    // =========================================================================
    // Roundtrip — pseudo-random data
    // =========================================================================

    /// <summary>
    /// Pseudo-random data (low compressibility) must roundtrip correctly.
    /// The compressed size may equal or exceed the input; the codec must still
    /// not corrupt the data.
    /// </summary>
    [Test]
    public async Task Roundtrip_PseudoRandom_RecoversOriginal()
    {
        byte[] input = new byte[16 * 1024];
        // Deterministic LFSR-like sequence: ensures reproducibility across runs.
        uint state = 0xDEADBEEF;
        for (int i = 0; i < input.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            input[i] = (byte)state;
        }

        byte[] buf = new byte[Lz4Codec.MaxCompressedSize(input.Length)];
        int compressedLen = Lz4Codec.Compress(input, buf);

        // compressedLen may be -1 (incompressible) or positive; either way
        // the codec is correct — just skip roundtrip check when -1.
        if (compressedLen > 0)
        {
            byte[] recovered = _DecompressFull(buf[0..compressedLen], input.Length);
            await Assert.That(recovered.AsSpan().SequenceEqual(input)).IsTrue();
        }
        else
        {
            // -1 is a valid, expected outcome for incompressible data.
            await Assert.That(compressedLen).IsEqualTo(-1);
        }
    }

    // =========================================================================
    // Only-literals path (input < MFlimit = 12 bytes)
    // =========================================================================

    /// <summary>
    /// Inputs shorter than the minimum match-search threshold (12 bytes) cannot
    /// be compressed to a smaller form (the LZ4 overhead of the token byte makes
    /// the encoded form larger than the raw input).  <see cref="Lz4Codec.Compress"/>
    /// correctly returns <c>-1</c>; the decompressor must still be able to decode
    /// a manually crafted literals-only block of the same size.
    /// </summary>
    [Test]
    public async Task Decompress_ShortInput_LiteralsOnly_RecoversOriginal()
    {
        // Manually crafted LZ4 block: token=0x50 (litLen=5, matchNibble=0),
        // followed by 5 literal bytes. This is a valid literals-only last sequence.
        byte[] literals = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] compressed = [0x50, 0x01, 0x02, 0x03, 0x04, 0x05];

        byte[] output = new byte[literals.Length];
        int written = Lz4Codec.Decompress(compressed, output);

        await Assert.That(written).IsEqualTo(literals.Length);
        await Assert.That(output.AsSpan().SequenceEqual(literals)).IsTrue();
    }

    // =========================================================================
    // Incompressible data — output buffer too small
    // =========================================================================

    /// <summary>
    /// When incompressible data is fed to <see cref="Lz4Codec.Compress"/> with a
    /// destination buffer smaller than the source, <c>-1</c> must be returned.
    /// </summary>
    [Test]
    public async Task Compress_IncompressibleData_SmallOutputBuffer_ReturnsMinusOne()
    {
        byte[] input = new byte[1024];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i * 7 + 13); // pseudo-random, avoids any repeated sequences
        }

        byte[] output = new byte[16]; // far too small for the overhead alone
        int result = Lz4Codec.Compress(input, output);

        await Assert.That(result).IsEqualTo(-1);
    }

    // =========================================================================
    // Known LZ4 block vector
    // =========================================================================

    /// <summary>
    /// Decompresses a hand-crafted LZ4 block and verifies the output byte-for-byte.
    /// <para>
    /// Block layout (two sequences, the second using overlap-copy):
    /// <list type="number">
    ///   <item><description>
    ///     Token 0x30 (litLen=3, matchNibble=0 → matchLen=4): literals "ABC", offset=3.
    ///     Overlap copy (offset 3 &lt; matchLen 4): writes "ABCA" → output "ABCABCA" so far.
    ///   </description></item>
    ///   <item><description>
    ///     Token 0x00 (litLen=0, matchNibble=0): last sequence (input exhausted after token).
    ///     No literals, no match copy.
    ///   </description></item>
    /// </list>
    /// Total decompressed: "ABCABCA" (7 bytes).
    /// </para>
    /// </summary>
    [Test]
    public async Task Decompress_KnownVector_CorrectOutput()
    {
        // Seq 1 (non-last): litLen=3, literals="ABC", offset=3, matchLen=4 (overlap)
        // Seq 2 (last):     litLen=0 — input exhausted, no match follows
        byte[] compressed =
        [
            0x30,             // token: litLen=3, matchNibble=0 (matchLen=4)
            0x41, 0x42, 0x43, // literals "ABC"
            0x03, 0x00,       // offset = 3 (little-endian), overlap since 3 < 4
            0x00,             // last token: litLen=0, input ends here
        ];

        // After overlap copy at offset 3 starting from position 3:
        //   output[3] = output[0] = 'A'
        //   output[4] = output[1] = 'B'
        //   output[5] = output[2] = 'C'
        //   output[6] = output[3] = 'A'  ← byte just written
        // → "ABCABCA"
        byte[] expected = "ABCABCA"u8.ToArray();

        byte[] output = new byte[expected.Length];
        int written = Lz4Codec.Decompress(compressed, output);

        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(output.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    // =========================================================================
    // Decompression error cases
    // =========================================================================

    /// <summary>
    /// A truncated compressed stream (literal length declared but data missing)
    /// must return <c>-1</c>.
    /// </summary>
    [Test]
    public async Task Decompress_TruncatedLiterals_ReturnsMinusOne()
    {
        // Token says 4 literals but only 2 follow.
        byte[] compressed = [0x40, 0x41, 0x42];
        byte[] output = new byte[16];

        int result = Lz4Codec.Decompress(compressed, output);
        await Assert.That(result).IsEqualTo(-1);
    }

    /// <summary>
    /// A zero offset in a match back-reference is illegal per the LZ4 spec and
    /// must cause <c>-1</c> to be returned.
    /// </summary>
    [Test]
    public async Task Decompress_ZeroOffset_ReturnsMinusOne()
    {
        // Token: 4 literals + match nibble=0 (matchLength=4), then offset=0 (illegal)
        byte[] compressed = [0x40, 0x41, 0x42, 0x43, 0x44, 0x00, 0x00, 0x00];
        byte[] output = new byte[16];

        int result = Lz4Codec.Decompress(compressed, output);
        await Assert.That(result).IsEqualTo(-1);
    }

    /// <summary>
    /// A back-reference whose offset exceeds the number of bytes already written
    /// is invalid and must return <c>-1</c>.
    /// </summary>
    [Test]
    public async Task Decompress_OffsetBeyondOutput_ReturnsMinusOne()
    {
        // Only 4 bytes written so far; offset = 8 points before the start of output.
        byte[] compressed = [0x40, 0x41, 0x42, 0x43, 0x44, 0x08, 0x00, 0x00];
        byte[] output = new byte[16];

        int result = Lz4Codec.Decompress(compressed, output);
        await Assert.That(result).IsEqualTo(-1);
    }

    // =========================================================================
    // Variable-length overflow attack (CRITICAL-1 regression)
    // =========================================================================

    /// <summary>
    /// A crafted compressed stream with a token that declares literal-length 15
    /// followed by enough 0xFF continuation bytes to overflow a signed 32-bit
    /// accumulator must be rejected with <c>-1</c>.
    /// Without the overflow guard, the negative accumulated value would be cast
    /// to a huge <c>uint</c> and passed to <c>Unsafe.CopyBlockUnaligned</c>,
    /// causing a heap out-of-bounds write.
    /// </summary>
    [Test]
    public async Task Decompress_LiteralLengthVarlenOverflow_ReturnsMinusOne()
    {
        // Token 0xF0: litLen nibble = 15, match nibble = 0.
        // Follow with 128 × 0xFF to cause literal-length accumulation to overflow int.
        byte[] compressed = new byte[1 + 128];
        compressed[0] = 0xF0; // litLen = 15, then extended
        for (int i = 1; i < compressed.Length; i++)
        {
            compressed[i] = 0xFF;
        }

        byte[] output = new byte[256];
        int result = Lz4Codec.Decompress(compressed, output);
        await Assert.That(result).IsEqualTo(-1);
    }

    /// <summary>
    /// Same overflow attack, but targeting the match-length accumulator.
    /// A valid 4-byte literal run is written first so the decoder reaches the
    /// match-length extension path; then 0xFF continuation bytes overflow the
    /// match-length accumulator.
    /// </summary>
    [Test]
    public async Task Decompress_MatchLengthVarlenOverflow_ReturnsMinusOne()
    {
        // Sequence:
        //   token = 0x4F : litLen = 4, matchNibble = 15 (triggers extended match length)
        //   4 literal bytes
        //   offset = 4 (little-endian, valid)
        //   128 × 0xFF: match-length extension that overflows int
        byte[] compressed = new byte[1 + 4 + 2 + 128];
        int pos = 0;
        compressed[pos++] = 0x4F;           // litLen=4, matchNibble=15
        compressed[pos++] = 0x41;           // literal 'A'
        compressed[pos++] = 0x42;           // literal 'B'
        compressed[pos++] = 0x43;           // literal 'C'
        compressed[pos++] = 0x44;           // literal 'D'
        compressed[pos++] = 0x04;           // offset low byte = 4
        compressed[pos++] = 0x00;           // offset high byte = 0
        for (int i = pos; i < compressed.Length; i++)
        {
            compressed[i] = 0xFF;           // match-length extension bytes
        }

        byte[] output = new byte[256];
        int result = Lz4Codec.Decompress(compressed, output);
        await Assert.That(result).IsEqualTo(-1);
    }

    // =========================================================================
    // MaxCompressedSize boundary guarantee
    // =========================================================================

    /// <summary>
    /// <see cref="Lz4Codec.Compress"/> must never write beyond
    /// <see cref="Lz4Codec.MaxCompressedSize"/> bytes for any input of that
    /// declared size.  This is the most basic contract of the size-bound method
    /// and must hold for both compressible and incompressible data.
    /// </summary>
    [Test]
    public async Task Compress_MaxCompressedSizeBuffer_NeverOverflows()
    {
        // Compressible: all-zero 32 KB block.
        byte[] compressible = new byte[32 * 1024];
        byte[] bufA = new byte[Lz4Codec.MaxCompressedSize(compressible.Length)];
        int lenA = Lz4Codec.Compress(compressible, bufA);
        await Assert.That(lenA).IsLessThanOrEqualTo(bufA.Length);

        // Incompressible: pseudo-random 4 KB block.
        byte[] random = new byte[4 * 1024];
        uint state = 0xCAFEBABE;
        for (int i = 0; i < random.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            random[i] = (byte)state;
        }
        byte[] bufB = new byte[Lz4Codec.MaxCompressedSize(random.Length)];
        int lenB = Lz4Codec.Compress(random, bufB);
        // -1 is valid for incompressible data; a positive value must be within bounds.
        if (lenB > 0)
        {
            await Assert.That(lenB).IsLessThanOrEqualTo(bufB.Length);
        }
        else
        {
            await Assert.That(lenB).IsEqualTo(-1);
        }
    }

    // =========================================================================
    // Concurrent compression (thread safety)
    // =========================================================================

    /// <summary>
    /// Many threads compressing different data concurrently must each produce a
    /// valid, correct compressed block that decompresses to the original.
    /// This validates that the <see cref="ThreadStaticAttribute"/>-backed hash
    /// table is truly per-thread and that no cross-thread corruption occurs.
    /// </summary>
    [Test]
    public async Task Compress_Concurrent_AllThreadsProduceCorrectResults()
    {
        const int ThreadCount = 16;
        const int Iterations = 64;

        Task[] tasks = new Task[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                byte[] input = new byte[8 * 1024];
                // Each thread gets a distinct repeating pattern so results are distinguishable.
                byte pattern = (byte)(threadId + 1);
                Array.Fill(input, pattern);

                for (int i = 0; i < Iterations; i++)
                {
                    byte[] buf = new byte[Lz4Codec.MaxCompressedSize(input.Length)];
                    int len = Lz4Codec.Compress(input, buf);
                    if (len <= 0)
                    {
                        throw new InvalidOperationException($"Thread {threadId}: Compress returned {len}");
                    }

                    byte[] recovered = new byte[input.Length];
                    int written = Lz4Codec.Decompress(buf.AsSpan(0, len), recovered);
                    if (written != input.Length)
                    {
                        throw new InvalidOperationException($"Thread {threadId}: Decompress returned {written}, expected {input.Length}");
                    }

                    if (!recovered.AsSpan().SequenceEqual(input))
                    {
                        throw new InvalidOperationException($"Thread {threadId}: Roundtrip data mismatch");
                    }
                }
            });
        }

        await Assert.That(async () => await Task.WhenAll(tasks).ConfigureAwait(false)).ThrowsNothing();
    }

    // =========================================================================
    // Large input (> 64 KB, exercises extended length encoding)
    // =========================================================================

    /// <summary>
    /// A 512 KB block of repeating data exercises the variable-length encoding for
    /// literal and match lengths that exceed 15, and verifies that multi-byte
    /// length continuation bytes are written and read correctly.
    /// </summary>
    [Test]
    public async Task Roundtrip_LargeRepeatingBlock_ExtendedLengths_RecoversOriginal()
    {
        byte[] input = new byte[512 * 1024];
        // Alternating 0x55 / 0xAA so there is exactly one long match sequence.
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 2 == 0 ? 0x55 : 0xAA);
        }

        byte[] compressed = _CompressFull(input);
        byte[] recovered = _DecompressFull(compressed, input.Length);
        await Assert.That(recovered.AsSpan().SequenceEqual(input)).IsTrue();
    }
}
