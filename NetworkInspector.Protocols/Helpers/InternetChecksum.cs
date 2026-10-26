// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// RFC 1071 Internet Checksum (one's complement sum) with SIMD acceleration. Every path sums the same
/// network-order 16-bit words and keeps every carry, so the result does not depend on the hardware path.
/// Provides three tiers: Vector256 (AVX2), Vector128 (SSE2/NEON), scalar fallback.
/// Used for IPv4 header checksum and UDP/TCP checksum validation.
/// <para>
/// Design: the checksum runs over every validated packet, so the sum is accumulated in a <see cref="ulong"/> and the
/// carries are folded once at the end instead of after every word. The SIMD paths exist to cut the per-byte cost of
/// large TCP/UDP segments; short inputs (an IPv4 header is 20 to 60 bytes) use the narrower path because the vector
/// setup and the horizontal sum would not pay off. The thresholds (64 and 32 bytes) are two vector blocks each.
/// </para>
/// <para>
/// <b>Thread-safety:</b> all members are stateless and safe for concurrent use.
/// </para>
/// </summary>
internal static class InternetChecksum
{
    /// <summary>
    /// Vector blocks after which the u32 lanes move into the u64 sum: each block adds at most 2 × 0xFFFF to a lane,
    /// and 0x8000 × 2 × 0xFFFF = 0xFFFF0000 still fits a lane.
    /// </summary>
    private const int _BlocksPerFlush = 0x8000;

    /// <summary>
    /// Computes the one's complement checksum of the given data.
    /// Returns 0x0000 if the checksum is valid (when verifying a received packet).
    /// </summary>
    /// <param name="data">
    /// The bytes to sum, read as network-order 16-bit words. A trailing odd byte is padded with a zero byte on the
    /// right (RFC 1071). When verifying, include the received checksum field; when generating, zero it first.
    /// </param>
    /// <returns>The complemented 16-bit sum; 0x0000 when verifying a packet with a correct checksum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Compute(ReadOnlySpan<byte> data)
    {
        ulong sum;

        // Pick the widest path that has at least two full vector blocks to work on; every path returns the same sum.
        if (Vector256.IsHardwareAccelerated && data.Length >= 64)
        {
            sum = _ComputeVector256(data);
        }
        else if (Vector128.IsHardwareAccelerated && data.Length >= 32)
        {
            sum = _ComputeVector128(data);
        }
        else
        {
            sum = _ComputeScalar(data);
        }

        return _FoldAndFinalize(sum);
    }

    /// <summary>
    /// Computes the one's complement checksum for a pseudo-header + UDP/TCP segment.
    /// The pseudo-header sum is pre-computed from IP source/destination and protocol info.
    /// </summary>
    /// <param name="data">
    /// The UDP/TCP/ICMPv6 segment including its checksum field, summed as in <see cref="Compute"/>.
    /// </param>
    /// <param name="pseudoHeaderSum">
    /// The unfolded pseudo-header sum from <see cref="ComputeIPv4PseudoHeaderSum"/> or
    /// <see cref="ComputeIPv6PseudoHeaderSum"/>. It is passed as a number, not as bytes, so no pseudo-header buffer
    /// has to be built or copied in front of the segment.
    /// </param>
    /// <returns>The complemented 16-bit sum; 0x0000 when verifying a segment with a correct checksum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ComputeWithPseudoHeader(ReadOnlySpan<byte> data, ulong pseudoHeaderSum)
    {
        ulong sum;

        // Same tier selection and thresholds as Compute; the pseudo-header sum is added only after the data is summed.
        if (Vector256.IsHardwareAccelerated && data.Length >= 64)
        {
            sum = _ComputeVector256(data);
        }
        else if (Vector128.IsHardwareAccelerated && data.Length >= 32)
        {
            sum = _ComputeVector128(data);
        }
        else
        {
            sum = _ComputeScalar(data);
        }

        // End-around carry: a wrap past 2^64 is worth 1 in one's complement arithmetic (2^64 ≡ 1 mod 0xFFFF).
        ulong total = sum + pseudoHeaderSum;
        if (total < sum)
        {
            total++;
        }

        return _FoldAndFinalize(total);
    }

    /// <summary>
    /// Computes IPv4 pseudo-header sum for transport-layer checksum validation.
    /// Pseudo-header: src (4) + dst (4) + zero (1) + protocol (1) + length (2) = 12 bytes.
    /// <para>
    /// This overload extracts 16-bit words directly from the raw <see cref="uint"/>
    /// addresses via bit-shifts, avoiding stackalloc + BinaryPrimitives round-trips.
    /// </para>
    /// </summary>
    /// <param name="srcIp">Source IPv4 address as raw big-endian <see cref="uint"/>.</param>
    /// <param name="dstIp">Destination IPv4 address as raw big-endian <see cref="uint"/>.</param>
    /// <param name="protocol">IP protocol number (e.g. 6 for TCP, 17 for UDP).</param>
    /// <param name="transportLength">Transport-layer segment length in bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ComputeIPv4PseudoHeaderSum(
        uint srcIp, uint dstIp,
        byte protocol, ushort transportLength)
    {
        // Extract the 4 × 16-bit words directly from the 32-bit address values.
        // The raw address holds the first octet in its top byte (network byte order read as a number), so its upper and
        // lower halves are exactly the two network-order words the pseudo-header carries, matching the network-order
        // words the data paths sum. The one's complement sum is commutative, so the order of the additions is free.
        // The result stays unfolded and cannot overflow a ulong (six terms of at most 32 bits).
        ulong sum = (srcIp >> 16) + (srcIp & 0xFFFF)
                  + (dstIp >> 16) + (dstIp & 0xFFFF)
                  + protocol
                  + transportLength;
        return sum;
    }

    /// <summary>
    /// Computes IPv6 pseudo-header sum for transport-layer checksum validation.
    /// Pseudo-header: src (16) + dst (16) + upper-layer length (4) + zero (3) + next-header (1) = 40 bytes.
    /// <para>
    /// This overload extracts 16-bit words directly from the <see cref="ulong"/>
    /// high/low halves of the 128-bit addresses, avoiding stackalloc + BinaryPrimitives.
    /// </para>
    /// </summary>
    /// <param name="srcHigh">Source IPv6 address, upper 64 bits (bits 127..64).</param>
    /// <param name="srcLow">Source IPv6 address, lower 64 bits (bits 63..0).</param>
    /// <param name="dstHigh">Destination IPv6 address, upper 64 bits (bits 127..64).</param>
    /// <param name="dstLow">Destination IPv6 address, lower 64 bits (bits 63..0).</param>
    /// <param name="nextHeader">Next-header value (e.g. 6 for TCP, 17 for UDP, 58 for ICMPv6).</param>
    /// <param name="transportLength">Upper-layer packet length in bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ComputeIPv6PseudoHeaderSum(
        ulong srcHigh, ulong srcLow,
        ulong dstHigh, ulong dstLow,
        byte nextHeader, uint transportLength)
    {
        // Extract 16-bit words from each 64-bit half via bit-shifts.
        // Each 64-bit value yields 4 × 16-bit words. The high half holds the first 8 octets (network order), so the
        // top word of every half is the first word on the wire.
        ulong sum = _SumU64AsU16Words(srcHigh)
                  + _SumU64AsU16Words(srcLow)
                  + _SumU64AsU16Words(dstHigh)
                  + _SumU64AsU16Words(dstLow);

        // Upper-layer packet length (32-bit, as two 16-bit words)
        sum += (transportLength >> 16) & 0xFFFF;
        sum += transportLength & 0xFFFF;

        // Next-header (zero-padded to 16 bits): the pseudo-header carries 3 zero octets before it, so it is the low
        // octet of the last word (RFC 8200 section 8.1).
        sum += nextHeader;

        return sum;
    }

    /// <summary>
    /// Sums the four 16-bit words contained in a 64-bit value.
    /// Used for IPv6 pseudo-header computation from raw high/low address components.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _SumU64AsU16Words(ulong value) =>
        (value >> 48) + ((value >> 32) & 0xFFFF) + ((value >> 16) & 0xFFFF) + (value & 0xFFFF);

    /// <summary>Scalar one's complement accumulation, processing 4 bytes per iteration.</summary>
    private static ulong _ComputeScalar(ReadOnlySpan<byte> data)
    {
        // The ulong accumulator absorbs all carries: a span holds at most int.MaxValue bytes, i.e. fewer than 2^30
        // words of at most 0xFFFF, so the sum stays below 2^46 and never wraps. Folding is left to _FoldAndFinalize.
        ulong sum = 0;
        int offset = 0;
        int length = data.Length;

        // Process 4 bytes (two 16-bit words) per iteration (unrolled once to reduce loop overhead).
        while (offset + 3 < length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4;
        }

        // Process remaining 16-bit word
        if (offset + 1 < length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }

        // Handle trailing odd byte (padded with zero on the right)
        if (offset < length)
        {
            sum += (uint)data[offset] << 8;
        }

        return sum;
    }

    /// <summary>
    /// Vector256 (AVX2) accelerated accumulation — processes 32 bytes per iteration.
    /// Each 256-bit vector holds 16 × u16 values; these are widened to u32 and accumulated
    /// to avoid overflow within the vector lanes.
    /// </summary>
    private static ulong _ComputeVector256(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        int length = data.Length;

        // Accumulator: 8 × u32 lanes (avoids u16 overflow within the vector). Each block adds at most 2 × 0xFFFF
        // to a lane, so the lanes move into the u64 sum every _BlocksPerFlush blocks, before they could wrap.
        Vector256<uint> acc = Vector256<uint>.Zero;
        ulong sum = 0;
        int blocks = 0;

        // Process 32 bytes per iteration
        while (offset + 31 < length)
        {
            if (blocks == _BlocksPerFlush)
            {
                sum += Vector256.Sum(Vector256.WidenLower(acc)) + Vector256.Sum(Vector256.WidenUpper(acc));
                acc = Vector256<uint>.Zero;
                blocks = 0;
            }

            blocks++;
            // Load 32 bytes as 16 × u16
            Vector256<ushort> v = Vector256.Create<byte>(data[offset..]).AsUInt16();

            // The lanes were read in machine order (little-endian); swap each to the network-order word the
            // scalar path and the remainder read, so every path sums the same RFC 1071 words.
            if (BitConverter.IsLittleEndian)
            {
                v = Vector256.ShiftLeft(v, 8) | Vector256.ShiftRightLogical(v, 8);
            }

            // Widen lower and upper halves to u32 and accumulate
            (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(v);
            acc += lo;
            acc += hi;

            offset += 32;
        }

        // Horizontal sum of all 8 u32 lanes. Added lane by lane into the u64 sum: Vector256.Sum on the u32 lanes
        // would add in 32 bits and could wrap, which is the carry loss the block counter above prevents.
        for (int i = 0; i < Vector256<uint>.Count; i++)
        {
            sum += acc.GetElement(i);
        }

        // Process the remaining bytes (fewer than one vector block) with scalar reads. A trailing odd byte is the high
        // octet of a final word whose low octet is zero (RFC 1071 padding).
        while (offset + 1 < length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }

        if (offset < length)
        {
            sum += (uint)data[offset] << 8;
        }

        return sum;
    }

    /// <summary>
    /// Vector128 (SSE2/NEON) accelerated accumulation — processes 16 bytes per iteration.
    /// </summary>
    private static ulong _ComputeVector128(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        int length = data.Length;

        // Accumulator: 4 × u32 lanes, moved into the u64 sum before they could wrap (as in the Vector256 path).
        Vector128<uint> acc = Vector128<uint>.Zero;
        ulong sum = 0;
        int blocks = 0;

        // Process 16 bytes per iteration
        while (offset + 15 < length)
        {
            if (blocks == _BlocksPerFlush)
            {
                sum += Vector128.Sum(Vector128.WidenLower(acc)) + Vector128.Sum(Vector128.WidenUpper(acc));
                acc = Vector128<uint>.Zero;
                blocks = 0;
            }

            blocks++;
            // Load 16 bytes as 8 × u16
            Vector128<ushort> v = Vector128.Create<byte>(data[offset..]).AsUInt16();

            // Machine-order lanes to network-order words, as in the Vector256 path.
            if (BitConverter.IsLittleEndian)
            {
                v = Vector128.ShiftLeft(v, 8) | Vector128.ShiftRightLogical(v, 8);
            }

            // Widen to u32 and accumulate
            (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(v);
            acc += lo;
            acc += hi;

            offset += 16;
        }

        // Horizontal sum of all 4 u32 lanes, widened lane by lane into the u64 sum (see the Vector256 path).
        for (int i = 0; i < Vector128<uint>.Count; i++)
        {
            sum += acc.GetElement(i);
        }

        // Process remaining bytes with scalar
        while (offset + 1 < length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }

        if (offset < length)
        {
            sum += (uint)data[offset] << 8;
        }

        return sum;
    }

    /// <summary>
    /// Folds the 64-bit accumulator into 16-bit one's complement and returns the complement.
    /// Folding repeats until no carry is left above bit 15, so even sums near 2^64 (a large pseudo-header sum)
    /// keep every carry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort _FoldAndFinalize(ulong sum)
    {
        // Each pass replaces the sum by (high part + low 16 bits), which keeps its value modulo 0xFFFF (2^16 ≡ 1);
        // a ulong needs only a few passes. The loop (instead of two fixed folds) is what keeps sums near 2^64 correct.
        while ((sum >> 16) != 0)
        {
            sum = (sum >> 16) + (sum & 0xFFFF);
        }

        // The sum is now at most 0xFFFF, so the cast after the complement only drops the (all-ones) upper bits.
        return (ushort)~sum;
    }
}
