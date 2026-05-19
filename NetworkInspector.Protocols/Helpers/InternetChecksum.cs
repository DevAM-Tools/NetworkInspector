// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Numerics;
using System.Runtime.Intrinsics;

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// RFC 1071 Internet Checksum (one's complement sum) with SIMD acceleration.
/// Provides three tiers: Vector256 (AVX2), Vector128 (SSE2/NEON), scalar fallback.
/// Used for IPv4 header checksum and UDP/TCP checksum validation.
/// </summary>
internal static class InternetChecksum
{
    /// <summary>
    /// Computes the one's complement checksum of the given data.
    /// Returns 0x0000 if the checksum is valid (when verifying a received packet).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Compute(ReadOnlySpan<byte> data)
    {
        ulong sum;

        if (Vector256.IsHardwareAccelerated && data.Length >= 64)
        {
            sum = ComputeVector256(data);
        }
        else if (Vector128.IsHardwareAccelerated && data.Length >= 32)
        {
            sum = ComputeVector128(data);
        }
        else
        {
            sum = ComputeScalar(data);
        }

        return FoldAndFinalize(sum);
    }

    /// <summary>
    /// Computes the one's complement checksum for a pseudo-header + UDP/TCP segment.
    /// The pseudo-header sum is pre-computed from IP source/destination and protocol info.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort ComputeWithPseudoHeader(ReadOnlySpan<byte> data, ulong pseudoHeaderSum)
    {
        ulong sum;

        if (Vector256.IsHardwareAccelerated && data.Length >= 64)
        {
            sum = ComputeVector256(data);
        }
        else if (Vector128.IsHardwareAccelerated && data.Length >= 32)
        {
            sum = ComputeVector128(data);
        }
        else
        {
            sum = ComputeScalar(data);
        }

        sum += pseudoHeaderSum;
        return FoldAndFinalize(sum);
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
        // No byte conversion needed — the checksum is endian-neutral at the word level
        // because both halves are summed and the one's complement sum is commutative.
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
        // Each 64-bit value yields 4 × 16-bit words.
        ulong sum = SumU64AsU16Words(srcHigh)
                  + SumU64AsU16Words(srcLow)
                  + SumU64AsU16Words(dstHigh)
                  + SumU64AsU16Words(dstLow);

        // Upper-layer packet length (32-bit, as two 16-bit words)
        sum += (transportLength >> 16) & 0xFFFF;
        sum += transportLength & 0xFFFF;

        // Next-header (zero-padded to 16 bits)
        sum += nextHeader;

        return sum;
    }

    /// <summary>
    /// Sums the four 16-bit words contained in a 64-bit value.
    /// Used for IPv6 pseudo-header computation from raw high/low address components.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SumU64AsU16Words(ulong value) =>
        (value >> 48) + ((value >> 32) & 0xFFFF) + ((value >> 16) & 0xFFFF) + (value & 0xFFFF);

    /// <summary>Scalar one's complement accumulation, processing 4 bytes per iteration.</summary>
    private static ulong ComputeScalar(ReadOnlySpan<byte> data)
    {
        ulong sum = 0;
        int offset = 0;
        int length = data.Length;

        // Process 4 bytes (two 16-bit words) per iteration
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
    private static ulong ComputeVector256(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        int length = data.Length;

        // Accumulator: 8 × u32 lanes (avoids u16 overflow within the vector)
        Vector256<uint> acc = Vector256<uint>.Zero;

        // Process 32 bytes per iteration
        while (offset + 31 < length)
        {
            // Load 32 bytes as 16 × u16
            Vector256<ushort> v = Vector256.Create<byte>(data[offset..]).AsUInt16();

            // Widen lower and upper halves to u32 and accumulate
            (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(v);
            acc += lo;
            acc += hi;

            offset += 32;
        }

        // Horizontal sum of all 8 u32 lanes
        ulong sum = 0;
        for (int i = 0; i < Vector256<uint>.Count; i++)
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
    /// Vector128 (SSE2/NEON) accelerated accumulation — processes 16 bytes per iteration.
    /// </summary>
    private static ulong ComputeVector128(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        int length = data.Length;

        // Accumulator: 4 × u32 lanes
        Vector128<uint> acc = Vector128<uint>.Zero;

        // Process 16 bytes per iteration
        while (offset + 15 < length)
        {
            // Load 16 bytes as 8 × u16
            Vector128<ushort> v = Vector128.Create<byte>(data[offset..]).AsUInt16();

            // Widen to u32 and accumulate
            (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(v);
            acc += lo;
            acc += hi;

            offset += 16;
        }

        // Horizontal sum of all 4 u32 lanes
        ulong sum = 0;
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
    /// Repeated folding handles carry chains from very large packets.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort FoldAndFinalize(ulong sum)
    {
        // Fold 64→32
        sum = (sum >> 32) + (sum & 0xFFFF_FFFF);
        // Fold 32→16 (twice to handle carry)
        sum = (sum >> 16) + (sum & 0xFFFF);
        sum = (sum >> 16) + (sum & 0xFFFF);
        return (ushort)~sum;
    }
}
