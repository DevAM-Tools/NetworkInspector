// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// xoroshiro128++ pseudo-random number generator by Blackman &amp; Vigna (2019).
/// <para>
/// Maintains 128 bits of state, passes all BigCrush and PractRand tests, period 2^128−1.
/// Scrambler: <c>result = rotl(s0 + s1, 17) + s0</c>
/// </para>
/// <para>
/// <see cref="FillBytes"/> uses 4-stream parallelism (AVX2, SSE2/NEON, or scalar ILP).
/// All paths produce <b>identical bytes for the same seed</b> — the SIMD variants are
/// a vectorized form of the same computation, not an algorithmic difference.
/// The bulk output (≥ 32 bytes) is not equivalent to sequential <see cref="NextU64"/> calls.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Xoroshiro128PlusPlus
{
    #region Constants

    // Rotation/shift amounts from the xoroshiro128++ reference implementation.
    // These values ensure the characteristic polynomial is primitive over GF(2),
    // giving the generator its full period of 2^128 − 1.
    private const int _ScramblerRotation = 17; // rotl(s0 + s1, 17)
    private const int _StateRotationA = 49; // rotl(s0, 49)
    private const int _StateShift = 21; // s1 << 21
    private const int _StateRotationB = 28; // rotl(s1, 28)

    // Minimum buffer length that justifies 4-stream parallel generation.
    // One iteration produces 4 × 8 = 32 bytes.
    private const int _BulkThreshold = 32;

    #endregion

    #region Fields

    /// <summary>First state word (s0).</summary>
    private ulong _S0;

    /// <summary>Second state word (s1).</summary>
    private ulong _S1;

    /// <summary>Test hook: forces the scalar bulk-fill branch in <see cref="FillBytes"/>.</summary>
    internal static bool ForceScalarBulkFillForTesting;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new instance seeded with <paramref name="seed"/>.
    /// Uses two _SplitMix64 rounds to expand the seed into two decorrelated state words.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Xoroshiro128PlusPlus(ulong seed)
    {
        _S0 = _SplitMix64(ref seed);
        _S1 = _SplitMix64(ref seed);

        // (0, 0) is the only fixed point of xoroshiro128. Guard against it.
        if (_S0 == 0 && _S1 == 0)
        {
            _S0 = 1;
        }
    }

    #endregion

    #region Core Generation

    /// <summary>
    /// Produces the next 64-bit pseudo-random value and advances the state.
    /// </summary>
    /// <returns>A uniformly distributed 64-bit value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextU64()
    {
        ulong s0 = _S0;
        ulong s1 = _S1;

        // ++ scrambler: rotl(s0 + s1, 17) + s0
        ulong result = BitOperations.RotateLeft(s0 + s1, _ScramblerRotation) + s0;

        // State update
        s1 ^= s0;
        _S0 = BitOperations.RotateLeft(s0, _StateRotationA) ^ s1 ^ (s1 << _StateShift);
        _S1 = BitOperations.RotateLeft(s1, _StateRotationB);

        return result;
    }

    /// <summary>Produces the next 32-bit value (top 32 bits of a 64-bit draw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextU32() => (uint)(NextU64() >> 32);

    /// <summary>Produces the next 16-bit value (top 16 bits of a 64-bit draw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort NextU16() => (ushort)(NextU64() >> 48);

    /// <summary>Produces the next 8-bit value (top 8 bits of a 64-bit draw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NextU8() => (byte)(NextU64() >> 56);

    #endregion

    #region Bounded Generation

    /// <summary>
    /// Returns a uniformly distributed integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).
    /// Uses Lemire's nearly-divisionless method (arXiv 1805.10941) — no modulo bias.
    /// Returns <paramref name="minInclusive"/> for degenerate or inverted ranges.
    /// The full <see cref="int"/> domain is supported, including
    /// <see cref="int.MinValue"/> … <see cref="int.MaxValue"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextRange(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            return minInclusive;
        }

        // Widen before subtract: int.MinValue…int.MaxValue overflows in unchecked int arithmetic.
        ulong range = (ulong)((long)maxExclusive - (long)minInclusive);
        ulong x = NextU64();
        // High 64 bits of (x × range) give a value uniform in [0, range).
        ulong high = Math.BigMul(x, range, out ulong low);

        // Rejection step: eliminates the tiny biased region at the low end.
        // The branch fires with probability ≤ range / 2^64; negligible for typical ranges.
        if (low < range)
        {
            ulong threshold = unchecked(0UL - range) % range;
            while (low < threshold)
            {
                x = NextU64();
                high = Math.BigMul(x, range, out low);
            }
        }

        // high ∈ [0, range) so minInclusive + high fits in int for any valid int interval.
        return (int)((long)minInclusive + (long)high);
    }

    #endregion

    #region Bulk Generation

    /// <summary>
    /// Fills <paramref name="buffer"/> with pseudo-random bytes.
    /// <para>
    /// For buffers ≥ 32 bytes, three sub-streams are derived and all four streams
    /// advance in parallel (AVX2, SSE2/NEON, or scalar ILP), producing 32 bytes per
    /// iteration. Output is byte-for-byte identical on every platform for a given seed.
    /// </para>
    /// <para>
    /// Buffers ≥ 32 bytes produce output that is <em>not</em> equivalent to sequential
    /// <see cref="NextU64"/> calls. Do not interleave the two if byte-exact sequences matter.
    /// </para>
    /// </summary>
    /// <param name="buffer">Target span to fill with random bytes.</param>
    public void FillBytes(Span<byte> buffer)
    {
        if (buffer.Length < _BulkThreshold)
        {
            _FillBytesSequential(buffer);
            return;
        }

        _DeriveSubStreams(
            out ulong s0B, out ulong s1B,
            out ulong s0C, out ulong s1C,
            out ulong s0D, out ulong s1D);

        // All three paths produce identical byte sequences for the same seed.
        if (!ForceScalarBulkFillForTesting && Vector256.IsHardwareAccelerated)
        {
            _FillBytesVector256(buffer, s0B, s1B, s0C, s1C, s0D, s1D);
        }
        else if (!ForceScalarBulkFillForTesting && Vector128.IsHardwareAccelerated)
        {
            _FillBytesVector128(buffer, s0B, s1B, s0C, s1C, s0D, s1D);
        }
        else
        {
            _FillBytesScalar4(buffer, s0B, s1B, s0C, s1C, s0D, s1D);
        }
    }

    // ─── Sub-stream derivation ───────────────────────────────────────────────

    /// <summary>
    /// Derives 3 independent sub-streams (B, C, D) from the current state via
    /// <see cref="_SplitMix64"/> seeded with <c>_S0 ^ _S1</c>.
    /// Stream A (the instance state) is not returned — callers use <see cref="_S0"/>/<see cref="_S1"/> directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _DeriveSubStreams(
        out ulong s0B, out ulong s1B,
        out ulong s0C, out ulong s1C,
        out ulong s0D, out ulong s1D)
    {
        ulong mix = _S0 ^ _S1;

        s0B = _SplitMix64(ref mix);
        s1B = _SplitMix64(ref mix);
        s0C = _SplitMix64(ref mix);
        s1C = _SplitMix64(ref mix);
        s0D = _SplitMix64(ref mix);
        s1D = _SplitMix64(ref mix);

        // Guard against the (0, 0) fixed point. _SplitMix64 makes this
        // astronomically unlikely; these are pure safety checks.
        if ((s0B | s1B) == 0)
        {
            s0B = 1;
        }
        if ((s0C | s1C) == 0)
        {
            s0C = 1;
        }
        if ((s0D | s1D) == 0)
        {
            s0D = 1;
        }
    }

    // ─── AVX2 / Vector256 — 4 lanes × u64 per iteration ─────────────────────

    /// <summary>
    /// Fills the buffer using Vector256 operations (AVX2 on x86).
    /// All 4 xoroshiro128++ streams are packed into Vector256&lt;ulong&gt; registers.
    /// Each iteration produces 4 × 8 = 32 bytes via a single vector store.
    /// </summary>
    private void _FillBytesVector256(
        Span<byte> buffer,
        ulong s0B, ulong s1B,
        ulong s0C, ulong s1C,
        ulong s0D, ulong s1D)
    {
        // Lanes: [A=0, B=1, C=2, D=3] — element 0 maps to the lowest address on store.
        Vector256<ulong> vs0 = Vector256.Create(_S0, s0B, s0C, s0D);
        Vector256<ulong> vs1 = Vector256.Create(_S1, s1B, s1C, s1D);

        ref byte bufRef = ref MemoryMarshal.GetReference(buffer);
        int offset = 0;

        while (offset + _BulkThreshold <= buffer.Length)
        {
            // xoroshiro128++ scrambler: result = rotl(s0 + s1, 17) + s0
            Vector256<ulong> sum = Vector256.Add(vs0, vs1);
            Vector256<ulong> result = Vector256.Add(_RotateLeft256(sum, _ScramblerRotation), vs0);

            // xoroshiro128 state update (element-wise on all 4 lanes):
            //   t = s1 ^ s0
            //   s0 = rotl(s0, 49) ^ t ^ (t << 21)
            //   s1 = rotl(t, 28)
            Vector256<ulong> t = Vector256.Xor(vs1, vs0);
            vs0 = Vector256.Xor(
                Vector256.Xor(_RotateLeft256(vs0, _StateRotationA), t),
                Vector256.ShiftLeft(t, _StateShift));
            vs1 = _RotateLeft256(t, _StateRotationB);

            // Write 32 bytes: element 0 (stream A) at lowest address.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset), result);
            offset += _BulkThreshold;
        }

        // Propagate stream A's final state; discard B, C, D.
        _S0 = vs0.GetElement(0);
        _S1 = vs1.GetElement(0);

        _FillBytesSequential(buffer[offset..]);
    }

    // ─── SSE2 / NEON / Vector128 — 2 × Vector128 (4 lanes total) ────────────

    /// <summary>
    /// Fills the buffer using Vector128 operations (SSE2 on x86, NEON on ARM).
    /// The 4 streams are split into two pairs: (A, B) and (C, D).
    /// Each iteration performs two Vector128 scrambler + state-update passes
    /// and writes 32 bytes total (16 + 16).
    /// </summary>
    private void _FillBytesVector128(
        Span<byte> buffer,
        ulong s0B, ulong s1B,
        ulong s0C, ulong s1C,
        ulong s0D, ulong s1D)
    {
        // A+B in lo, C+D in hi. Element 0 = stream A, element 1 = stream B.
        Vector128<ulong> vs0Lo = Vector128.Create(_S0, s0B);
        Vector128<ulong> vs1Lo = Vector128.Create(_S1, s1B);
        Vector128<ulong> vs0Hi = Vector128.Create(s0C, s0D);
        Vector128<ulong> vs1Hi = Vector128.Create(s1C, s1D);

        ref byte bufRef = ref MemoryMarshal.GetReference(buffer);
        int offset = 0;

        while (offset + _BulkThreshold <= buffer.Length)
        {
            // ── Streams A, B: scrambler + state update ──
            Vector128<ulong> sumLo = Vector128.Add(vs0Lo, vs1Lo);
            Vector128<ulong> resultLo = Vector128.Add(_RotateLeft128(sumLo, _ScramblerRotation), vs0Lo);

            Vector128<ulong> tLo = Vector128.Xor(vs1Lo, vs0Lo);
            vs0Lo = Vector128.Xor(
                Vector128.Xor(_RotateLeft128(vs0Lo, _StateRotationA), tLo),
                Vector128.ShiftLeft(tLo, _StateShift));
            vs1Lo = _RotateLeft128(tLo, _StateRotationB);

            // ── Streams C, D: scrambler + state update ──
            Vector128<ulong> sumHi = Vector128.Add(vs0Hi, vs1Hi);
            Vector128<ulong> resultHi = Vector128.Add(_RotateLeft128(sumHi, _ScramblerRotation), vs0Hi);

            Vector128<ulong> tHi = Vector128.Xor(vs1Hi, vs0Hi);
            vs0Hi = Vector128.Xor(
                Vector128.Xor(_RotateLeft128(vs0Hi, _StateRotationA), tHi),
                Vector128.ShiftLeft(tHi, _StateShift));
            vs1Hi = _RotateLeft128(tHi, _StateRotationB);

            // Write 32 bytes: first 16 = (A, B), next 16 = (C, D).
            // This matches the Vector256 memory layout exactly.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset), resultLo);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset + 16), resultHi);
            offset += _BulkThreshold;
        }

        // Stream A is element 0 of the Lo pair.
        _S0 = vs0Lo.GetElement(0);
        _S1 = vs1Lo.GetElement(0);

        _FillBytesSequential(buffer[offset..]);
    }

    // ─── Scalar 4-stream — ILP-friendly, no SIMD required ────────────────────

    /// <summary>
    /// Scalar path: all 4 xoroshiro128++ streams are advanced using plain scalar
    /// operations. The 4 independent state updates have no data dependencies
    /// between them, allowing the CPU's out-of-order engine to pipeline them
    /// and fill execution-port bubbles — typically achieving ~2–3× the throughput
    /// of a single-stream loop on modern superscalar CPUs.
    /// </summary>
    private void _FillBytesScalar4(
        Span<byte> buffer,
        ulong s0B, ulong s1B,
        ulong s0C, ulong s1C,
        ulong s0D, ulong s1D)
    {
        // Stream A uses instance state read into locals (avoids aliasing overhead
        // from repeated loads/stores of _S0/_S1 through the struct 'this' pointer).
        ulong s0A = _S0, s1A = _S1;

        ref byte bufRef = ref MemoryMarshal.GetReference(buffer);
        int offset = 0;

        while (offset + _BulkThreshold <= buffer.Length)
        {
            // ── Scrambler: compute output for all 4 streams ──────────────
            ulong rA = BitOperations.RotateLeft(s0A + s1A, _ScramblerRotation) + s0A;
            ulong rB = BitOperations.RotateLeft(s0B + s1B, _ScramblerRotation) + s0B;
            ulong rC = BitOperations.RotateLeft(s0C + s1C, _ScramblerRotation) + s0C;
            ulong rD = BitOperations.RotateLeft(s0D + s1D, _ScramblerRotation) + s0D;

            // ── State update: 4 independent chains (no inter-dependency) ─
            ulong tA = s1A ^ s0A;
            s0A = BitOperations.RotateLeft(s0A, _StateRotationA) ^ tA ^ (tA << _StateShift);
            s1A = BitOperations.RotateLeft(tA, _StateRotationB);

            ulong tB = s1B ^ s0B;
            s0B = BitOperations.RotateLeft(s0B, _StateRotationA) ^ tB ^ (tB << _StateShift);
            s1B = BitOperations.RotateLeft(tB, _StateRotationB);

            ulong tC = s1C ^ s0C;
            s0C = BitOperations.RotateLeft(s0C, _StateRotationA) ^ tC ^ (tC << _StateShift);
            s1C = BitOperations.RotateLeft(tC, _StateRotationB);

            ulong tD = s1D ^ s0D;
            s0D = BitOperations.RotateLeft(s0D, _StateRotationA) ^ tD ^ (tD << _StateShift);
            s1D = BitOperations.RotateLeft(tD, _StateRotationB);

            // Write 32 bytes: A, B, C, D at offsets 0, 8, 16, 24 — same order as SIMD paths.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset), rA);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset + 8), rB);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset + 16), rC);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset + 24), rD);
            offset += _BulkThreshold;
        }

        // Propagate stream A's final state.
        _S0 = s0A;
        _S1 = s1A;

        _FillBytesSequential(buffer[offset..]);
    }

    // ─── Sequential scalar — single stream, for short buffers and tails ──────

    /// <summary>
    /// Fills the buffer using sequential <see cref="NextU64"/> calls (single stream).
    /// Used for buffers shorter than <see cref="_BulkThreshold"/> and for the 0–31
    /// byte remainder after the 4-stream bulk loop.
    /// </summary>
    private void _FillBytesSequential(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        ref byte bufRef = ref MemoryMarshal.GetReference(buffer);
        int offset = 0;

        // Process full u64s (8 bytes each).
        while (offset + 8 <= buffer.Length)
        {
            // Write in native byte order — matches the bulk SIMD/scalar4 paths.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bufRef, offset), NextU64());
            offset += 8;
        }

        // Remaining 1–7 bytes: draw one u64 and extract byte by byte (LSB first).
        if (offset < buffer.Length)
        {
            ulong tail = NextU64();
            int remaining = buffer.Length - offset;
            for (int i = 0; i < remaining; i++)
            {
                Unsafe.Add(ref bufRef, offset + i) = (byte)(tail >> (i * 8));
            }
        }
    }

    #endregion

    #region SIMD Helpers

    // ─── Vector rotate-left ──────────────────────────────────────────────────
    // No hardware rotate intrinsic exists for packed u64 lanes, so we implement
    // rotl(x, k) as (x << k) | (x >> (64 - k)) using shift + OR.

    /// <summary>Bitwise rotate-left on each u64 lane of a <see cref="Vector256{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> _RotateLeft256(Vector256<ulong> v, int k) =>
        Vector256.BitwiseOr(
            Vector256.ShiftLeft(v, k),
            Vector256.ShiftRightLogical(v, 64 - k));

    /// <summary>Bitwise rotate-left on each u64 lane of a <see cref="Vector128{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> _RotateLeft128(Vector128<ulong> v, int k) =>
        Vector128.BitwiseOr(
            Vector128.ShiftLeft(v, k),
            Vector128.ShiftRightLogical(v, 64 - k));

    #endregion

    #region Seed Utilities

    /// <summary>
    /// Derives a deterministic per-frame seed from a master seed and a frame index.
    /// Applies the MurmurHash3 64-bit finalizer (Stafford variant 13) to
    /// <c>masterSeed ^ frameId</c> for excellent avalanche across all bits.
    /// </summary>
    /// <param name="masterSeed">The source-level master seed.</param>
    /// <param name="frameId">Zero-based frame index.</param>
    /// <returns>A seed suitable for <c>new Xoroshiro128PlusPlus(seed)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DeriveFrameSeed(ulong masterSeed, ulong frameId)
    {
        ulong x = masterSeed ^ frameId;
        x ^= x >> 30;
        x = unchecked(x * 0xBF58476D1CE4E5B9UL); // Stafford mix13 constant 1
        x ^= x >> 27;
        x = unchecked(x * 0x94D049BB133111EBUL); // Stafford mix13 constant 2
        return x ^ (x >> 31);
    }

    /// <summary>
    /// _SplitMix64: advances <paramref name="state"/> by ⌊2^64/φ⌋ and applies the
    /// MurmurHash3 64-bit finalizer. Used for constructor seeding and sub-stream derivation.
    /// </summary>
    /// <param name="state">Mutable state, advanced by one step on each call.</param>
    /// <returns>A well-mixed 64-bit value derived from the updated state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _SplitMix64(ref ulong state)
    {
        state = unchecked(state + 0x9E3779B97F4A7C15UL); // ⌊2^64/φ⌋ — golden ratio constant
        ulong z = state;
        // MurmurHash3 64-bit finalizer (Stafford variant 13)
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    #endregion
}
