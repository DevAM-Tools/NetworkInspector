// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Core;

/// <summary>
/// Zero-allocation network checksum computations.
/// All methods work directly on <see cref="ReadOnlySpan{T}"/> or <see cref="Span{T}"/>.
/// <para>
/// The core <see cref="OnesComplement"/> method uses hardware-accelerated SIMD when available:
/// <list type="bullet">
///   <item><description>Vector256 (AVX2 on x86, 256-bit NEON on ARM): 32 bytes per iteration</description></item>
///   <item><description>Vector128 (SSE2 on x86, NEON on ARM): 16 bytes per iteration</description></item>
///   <item><description>Scalar fallback: 8-byte unrolled loop for all platforms</description></item>
/// </list>
/// All paths produce identical checksum results — the SIMD paths are a vectorized form
/// of the same RFC 1071 one's-complement accumulation.
/// </para>
/// <para>
/// The pseudo-header methods (<see cref="PseudoHeaderIPv4"/>, <see cref="PseudoHeaderIPv6"/>)
/// use incremental accumulation: the pseudo-header fields are summed directly into a running
/// accumulator without copying them into a temporary buffer. This avoids stackalloc overhead
/// and is safe for any segment size (no stack overflow risk for jumbo frames).
/// </para>
/// </summary>
public static class ChecksumUtils
{
    #region Public API

    /// <summary>
    /// Computes the one's-complement checksum (RFC 1071) over the given data.
    /// Handles odd-length data by treating the last byte as a high byte (padded with zero).
    /// Uses Vector256, Vector128, or scalar accumulation depending on hardware support.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The 16-bit one's-complement checksum (already inverted, ready to write).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort OnesComplement(ReadOnlySpan<byte> data)
    {
        uint sum;

        // Dispatch to the widest available SIMD path. The JIT eliminates the
        // branches for unsupported ISAs at compile time (IsHardwareAccelerated is a JIT constant).
        if (Vector256.IsHardwareAccelerated && data.Length >= Vector256<byte>.Count)
        {
            sum = _AccumulateVector256(data);
        }
        else if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            sum = _AccumulateVector128(data);
        }
        else
        {
            sum = _AccumulateScalar(data);
        }

        return _Fold32To16(sum);
    }

    /// <summary>
    /// Computes the IPv4 header checksum. The header's checksum field must be
    /// set to zero before calling this method.
    /// </summary>
    /// <param name="header">The complete IPv4 header (20–60 bytes).</param>
    /// <returns>The 16-bit header checksum, ready to write to bytes 10–11.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort IPv4Header(ReadOnlySpan<byte> header) => OnesComplement(header);

    /// <summary>
    /// Computes the TCP or UDP checksum over an IPv4 pseudo-header + transport segment.
    /// Uses incremental accumulation — no temporary buffer allocation required.
    /// </summary>
    /// <param name="srcIp">Source IPv4 address (4 bytes).</param>
    /// <param name="dstIp">Destination IPv4 address (4 bytes).</param>
    /// <param name="protocol">IP protocol number (6 for TCP, 17 for UDP).</param>
    /// <param name="segment">The complete transport segment (header + payload).</param>
    /// <returns>The 16-bit checksum, ready to write to the transport checksum field.</returns>
    /// <remarks>
    /// External-input boundary: <paramref name="srcIp"/> and <paramref name="dstIp"/>
    /// must each be at least 4 bytes. Both lengths are validated at entry via
    /// <see cref="ArgumentOutOfRangeException"/>; callers must not bypass these
    /// checks. This is the trust boundary for the address spans — do not move the
    /// validation downstream.
    /// </remarks>
    public static ushort PseudoHeaderIPv4(
        ReadOnlySpan<byte> srcIp,
        ReadOnlySpan<byte> dstIp,
        byte protocol,
        ReadOnlySpan<byte> segment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(srcIp.Length, 4, nameof(srcIp));
        ArgumentOutOfRangeException.ThrowIfLessThan(dstIp.Length, 4, nameof(dstIp));

        // Accumulate the IPv4 pseudo-header fields directly into the running sum.
        // Layout: srcIP(4) + dstIP(4) + zero(1) + proto(1) + segLen(2) = 12 bytes.
        // This avoids stackalloc + CopyTo for the entire segment.
        uint sum = 0;

        // Source IP: 2 × 16-bit big-endian words
        sum += (uint)(srcIp[0] << 8 | srcIp[1]);
        sum += (uint)(srcIp[2] << 8 | srcIp[3]);

        // Destination IP: 2 × 16-bit big-endian words
        sum += (uint)(dstIp[0] << 8 | dstIp[1]);
        sum += (uint)(dstIp[2] << 8 | dstIp[3]);

        // Zero byte + protocol: forms one 16-bit word (0x00 | protocol)
        sum += protocol;

        // Segment length as 16-bit big-endian word
        sum += (uint)segment.Length;

        // Add the transport segment checksum (raw partial sum, not yet folded/inverted)
        sum += _PartialSum(segment);

        return _Fold32To16(sum);
    }

    /// <summary>
    /// Computes the TCP, UDP, or ICMPv6 checksum over an IPv6 pseudo-header + transport segment.
    /// Uses incremental accumulation — no temporary buffer allocation required.
    /// </summary>
    /// <param name="srcIp">Source IPv6 address (16 bytes).</param>
    /// <param name="dstIp">Destination IPv6 address (16 bytes).</param>
    /// <param name="nextHeader">Next header value (6 for TCP, 17 for UDP, 58 for ICMPv6).</param>
    /// <param name="segment">The complete upper-layer segment (header + payload).</param>
    /// <returns>The 16-bit checksum.</returns>
    /// <remarks>
    /// External-input boundary: <paramref name="srcIp"/> and <paramref name="dstIp"/>
    /// must each be at least 16 bytes. Both lengths are validated at entry via
    /// <see cref="ArgumentOutOfRangeException"/>; callers must not bypass these
    /// checks. This is the trust boundary for the address spans — do not move the
    /// validation downstream.
    /// </remarks>
    public static ushort PseudoHeaderIPv6(
        ReadOnlySpan<byte> srcIp,
        ReadOnlySpan<byte> dstIp,
        byte nextHeader,
        ReadOnlySpan<byte> segment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(srcIp.Length, 16, nameof(srcIp));
        ArgumentOutOfRangeException.ThrowIfLessThan(dstIp.Length, 16, nameof(dstIp));

        // Accumulate the IPv6 pseudo-header fields directly into the running sum.
        // Layout: srcIP(16) + dstIP(16) + upperLayerLen(4) + zeros(3) + nextHdr(1) = 40 bytes.
        uint sum = 0;

        // Source IPv6 address: 8 × 16-bit big-endian words (manually unrolled so
        // the JIT emits a straight-line accumulation on this per-frame hot path).
        sum += (uint)(srcIp[0] << 8 | srcIp[1]);
        sum += (uint)(srcIp[2] << 8 | srcIp[3]);
        sum += (uint)(srcIp[4] << 8 | srcIp[5]);
        sum += (uint)(srcIp[6] << 8 | srcIp[7]);
        sum += (uint)(srcIp[8] << 8 | srcIp[9]);
        sum += (uint)(srcIp[10] << 8 | srcIp[11]);
        sum += (uint)(srcIp[12] << 8 | srcIp[13]);
        sum += (uint)(srcIp[14] << 8 | srcIp[15]);

        // Destination IPv6 address: 8 × 16-bit big-endian words (unrolled).
        sum += (uint)(dstIp[0] << 8 | dstIp[1]);
        sum += (uint)(dstIp[2] << 8 | dstIp[3]);
        sum += (uint)(dstIp[4] << 8 | dstIp[5]);
        sum += (uint)(dstIp[6] << 8 | dstIp[7]);
        sum += (uint)(dstIp[8] << 8 | dstIp[9]);
        sum += (uint)(dstIp[10] << 8 | dstIp[11]);
        sum += (uint)(dstIp[12] << 8 | dstIp[13]);
        sum += (uint)(dstIp[14] << 8 | dstIp[15]);

        // Upper-layer length as 32-bit value split into 2 × 16-bit words.
        // For standard frames (< 64KB) the high word is always 0.
        uint segLen = (uint)segment.Length;
        sum += segLen >> 16;   // high 16 bits (0 for segments < 64KB)
        sum += segLen & 0xFFFF; // low 16 bits

        // Three zero bytes + nextHeader byte form two 16-bit words: 0x0000 and (0x00 | nextHeader)
        sum += nextHeader;

        // Add the transport segment checksum (raw partial sum, not yet folded/inverted)
        sum += _PartialSum(segment);

        return _Fold32To16(sum);
    }

    #endregion

    #region Partial Sum (raw accumulation without fold/invert)

    /// <summary>
    /// Computes the raw one's-complement partial sum over the given data.
    /// Returns the 32-bit running sum without folding or inverting — suitable
    /// for combining multiple partial sums incrementally.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _PartialSum(ReadOnlySpan<byte> data)
    {
        if (Vector256.IsHardwareAccelerated && data.Length >= Vector256<byte>.Count)
        {
            return _AccumulateVector256(data);
        }

        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            return _AccumulateVector128(data);
        }

        return _AccumulateScalar(data);
    }

    #endregion

    #region Fold Helper

    /// <summary>
    /// Folds a 32-bit accumulator into a 16-bit one's-complement checksum.
    /// Repeatedly adds carry bits until the result fits in 16 bits, then inverts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort _Fold32To16(uint sum)
    {
        // Two iterations are sufficient for a 32-bit accumulator:
        // after the first fold the maximum value is 0x1FFFE, so the
        // second fold produces at most 0xFFFF + 1 = 0x10000.
        // A third fold handles that final carry bit.
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    #endregion

    #region Vector256 Path (AVX2 / 256-bit NEON)

    /// <summary>
    /// Accumulates 16-bit big-endian word sums using 256-bit SIMD vectors.
    /// Processes 32 bytes (16 words) per iteration for ~4× throughput over scalar.
    /// </summary>
    /// <remarks>
    /// Algorithm per iteration:
    /// 1. Load 32 bytes into a Vector256&lt;byte&gt;, split into two 128-bit halves
    /// 2. Byte-swap adjacent pairs in each half to convert big-endian u16 to native order
    /// 3. Recombine and reinterpret as Vector256&lt;ushort&gt; (16 native-endian words)
    /// 4. Widen to 2 × Vector256&lt;uint&gt; and accumulate into u32 lanes to prevent overflow
    /// 5. After all iterations, horizontally sum the 8 u32 lanes and handle tail bytes
    ///
    /// Note: Vector256.Shuffle uses full 32-byte indexing (indices 0-31), not per-lane.
    /// We shuffle each 128-bit half independently with Vector128.Shuffle to ensure each
    /// half's byte-swap references its own bytes, not the other half's.
    /// </remarks>
    private static uint _AccumulateVector256(ReadOnlySpan<byte> data)
    {
        // Accumulator holds 8 × uint lanes.
        // Maximum iterations before u32 overflow: 2^32 / (0xFFFF × 16) ≈ 4096 iterations.
        // At 32 bytes/iteration that's ~128 KB — far beyond any frame. Safe without mid-loop folding.
        Vector256<uint> vSum = Vector256<uint>.Zero;

        int i = 0;
        int vectorEnd = data.Length - Vector256<byte>.Count + 1;

        ref byte dataRef = ref MemoryMarshal.GetReference(data);

        // Shuffle mask for byte-swapping adjacent pairs within a 128-bit lane:
        // [b0,b1,b2,b3,...] → [b1,b0,b3,b2,...] converts big-endian u16 to native little-endian.
        Vector128<byte> swapMask128 = Vector128.Create(
            (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);

        while (i < vectorEnd)
        {
            // Load 32 raw bytes containing 16 big-endian u16 words
            Vector256<byte> raw = Vector256.LoadUnsafe(ref dataRef, (nuint)i);

            // Byte-swap each 128-bit half independently. Vector128.Shuffle uses lane-local
            // indices (0-15), so each half correctly swaps its own bytes.
            Vector128<byte> loSwapped = Vector128.Shuffle(raw.GetLower(), swapMask128);
            Vector128<byte> hiSwapped = Vector128.Shuffle(raw.GetUpper(), swapMask128);

            // Recombine into a 256-bit vector of native-endian u16 words
            Vector256<ushort> words = Vector256.Create(loSwapped, hiSwapped).AsUInt16();

            // Widen 16 × u16 to 2 × 8 × u32 and accumulate to prevent u16 overflow
            (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(words);
            vSum += lo;
            vSum += hi;

            i += Vector256<byte>.Count;
        }

        // Horizontally sum the 8 uint lanes
        uint sum = Vector256.Sum(vSum);

        // Handle remaining bytes with the scalar tail
        sum += _AccumulateScalarTail(data, i);

        return sum;
    }

    #endregion

    #region Vector128 Path (SSE2 / NEON)

    /// <summary>
    /// Accumulates 16-bit big-endian word sums using 128-bit SIMD vectors.
    /// Processes 16 bytes per iteration for ~2× throughput over scalar.
    /// Same algorithm as <see cref="_AccumulateVector256"/> at half width.
    /// </summary>
    private static uint _AccumulateVector128(ReadOnlySpan<byte> data)
    {
        Vector128<uint> vSum = Vector128<uint>.Zero;

        int i = 0;
        int vectorEnd = data.Length - Vector128<byte>.Count + 1; // 16 bytes per iteration

        ref byte dataRef = ref MemoryMarshal.GetReference(data);

        // Shuffle mask to byte-swap adjacent pairs within each 128-bit lane
        Vector128<byte> swapMask = Vector128.Create(
            (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);

        while (i < vectorEnd)
        {
            Vector128<byte> raw = Vector128.LoadUnsafe(ref dataRef, (nuint)i);

            // Byte-swap to convert big-endian u16 words to native representation
            Vector128<ushort> words = Vector128.Shuffle(raw, swapMask).AsUInt16();

            // Widen 8 × u16 to 2 × 4 × u32 and accumulate
            (Vector128<uint> wordsLo, Vector128<uint> wordsHi) = Vector128.Widen(words);
            vSum += wordsLo;
            vSum += wordsHi;

            i += Vector128<byte>.Count;
        }

        // Horizontally sum the 4 uint lanes
        uint sum = Vector128.Sum(vSum);

        // Handle remaining bytes with the scalar tail
        sum += _AccumulateScalarTail(data, i);

        return sum;
    }

    #endregion

    #region Scalar Path

    /// <summary>
    /// Scalar accumulation of 16-bit big-endian words with 4× unrolling (8 bytes/iteration).
    /// Used as the fallback when no SIMD is available, or for small inputs below the vector threshold.
    /// </summary>
    private static uint _AccumulateScalar(ReadOnlySpan<byte> data)
        => _AccumulateScalarTail(data, 0);

    /// <summary>
    /// Scalar accumulation starting from a given byte offset. Used for both the
    /// full scalar path and as the tail handler after SIMD vector processing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _AccumulateScalarTail(ReadOnlySpan<byte> data, int i)
    {
        uint sum = 0;

        // Sum 16-bit words. Unroll by processing 4 words (8 bytes) at a time
        // for better throughput on modern CPUs with instruction-level parallelism.
        int fastEnd = data.Length - 7;
        while (i < fastEnd)
        {
            sum += (uint)(data[i] << 8 | data[i + 1]);
            sum += (uint)(data[i + 2] << 8 | data[i + 3]);
            sum += (uint)(data[i + 4] << 8 | data[i + 5]);
            sum += (uint)(data[i + 6] << 8 | data[i + 7]);
            i += 8;
        }

        // Handle remaining full 16-bit words
        while (i + 1 < data.Length)
        {
            sum += (uint)(data[i] << 8 | data[i + 1]);
            i += 2;
        }

        // Handle trailing odd byte — treated as high byte with zero low byte
        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        return sum;
    }

    #endregion
}
