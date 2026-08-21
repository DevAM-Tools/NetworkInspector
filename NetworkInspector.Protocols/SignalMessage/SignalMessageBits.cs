// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Single source of truth for signal bit layout: span computation (compile-time),
/// unchecked extraction (parse hot path), and raw/physical conversion.
/// </summary>
/// <remarks>
/// <see cref="GetEndByteExclusive"/> and <see cref="ExtractRawUnchecked"/> share the same
/// Motorola/Intel walk order so compile-time required byte length matches runtime extraction.
/// Little-endian 64-bit values that are not byte-aligned need 9 payload bytes; those are
/// assembled with two <see cref="ulong"/>s so a C# <c>&lt;&lt; 64</c> (masked to 0) cannot
/// corrupt the low byte.
/// </remarks>
internal static class SignalMessageBits
{
    #region Span / Length (compile-time)

    /// <summary>
    /// Returns the exclusive end byte index required to read a signal
    /// (<c>startBit</c>, <c>bitLength</c>, endian).
    /// </summary>
    /// <param name="startBit">Signal start bit (MSB for big-endian, LSB for little-endian).</param>
    /// <param name="bitLength">Number of bits (1–64).</param>
    /// <param name="bigEndian"><see langword="true"/> for Motorola order.</param>
    /// <returns>Exclusive end byte index (0 when <paramref name="bitLength"/> is not positive).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetEndByteExclusive(int startBit, int bitLength, bool bigEndian)
    {
        if (bitLength <= 0)
        {
            return 0;
        }

        if (bigEndian)
        {
            // Motorola: start at MSB, walk toward lower significance within the byte, then next byte.
            int bytePos = startBit / 8;
            int bitPos = startBit % 8;
            int bitsRemaining = bitLength;
            while (bitsRemaining > 0)
            {
                int available = bitPos + 1;
                int bitsToTake = available < bitsRemaining ? available : bitsRemaining;
                bitsRemaining -= bitsToTake;
                bytePos++;
                bitPos = 7;
            }

            return bytePos;
        }

        // Intel: contiguous bits from startBit upward.
        int lastBit = startBit + bitLength - 1;
        return (lastBit / 8) + 1;
    }

    /// <summary>
    /// Maximum exclusive end byte over all provided signal bit spans.
    /// </summary>
    internal static int ComputeRequiredByteLength(ReadOnlySpan<SignalInfo> signals)
    {
        int required = 0;
        for (int i = 0; i < signals.Length; i++)
        {
            ref readonly SignalInfo s = ref signals[i];
            int end = GetEndByteExclusive(s.StartBit, s.BitLength, s.BigEndian);
            if (end > required)
            {
                required = end;
            }
        }

        return required;
    }

    /// <summary>
    /// Maximum exclusive end byte including an optional mux selector and every mux-group signal.
    /// </summary>
    internal static int ComputeRequiredByteLength(
        ReadOnlySpan<SignalInfo> staticSignals,
        bool hasMux,
        in SignalInfo muxSignal,
        ReadOnlySpan<SignalInfo[]> muxGroups)
    {
        int required = ComputeRequiredByteLength(staticSignals);
        if (hasMux)
        {
            int muxEnd = GetEndByteExclusive(muxSignal.StartBit, muxSignal.BitLength, muxSignal.BigEndian);
            if (muxEnd > required)
            {
                required = muxEnd;
            }
        }

        for (int g = 0; g < muxGroups.Length; g++)
        {
            SignalInfo[] group = muxGroups[g];
            int groupEnd = ComputeRequiredByteLength(group);
            if (groupEnd > required)
            {
                required = groupEnd;
            }
        }

        return required;
    }

    /// <summary>
    /// Computes <c>maxRaw = (1 &lt;&lt; bitLength) - 1</c> with a safe 64-bit special case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong MaxRawForBitLength(int bitLength)
    {
        if (bitLength >= 64)
        {
            return ulong.MaxValue;
        }

        if (bitLength <= 0)
        {
            return 0;
        }

        return (1UL << bitLength) - 1UL;
    }

    #endregion

    #region Extraction / Physical (parse hot path)

    /// <summary>
    /// Extracts raw signal bits into a <see cref="ulong"/> without length checks.
    /// </summary>
    /// <param name="data">Payload span; must cover the signal's byte range.</param>
    /// <param name="signal">Signal descriptor.</param>
    /// <returns>Raw unsigned bit pattern in the low <see cref="SignalInfo.BitLength"/> bits.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ExtractRawUnchecked(ReadOnlySpan<byte> data, in SignalInfo signal)
    {
        if (signal.BigEndian)
        {
            return _ExtractBigEndianUnchecked(data, signal.StartBit, signal.BitLength);
        }

        return _ExtractLittleEndianUnchecked(data, signal.StartBit, signal.BitLength);
    }

    /// <summary>
    /// Converts an unsigned raw <see cref="ulong"/> to a physical double:
    /// <c>raw × factor + offset</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double ToPhysical(ulong raw, in SignalInfo signal)
        => (raw * signal.Factor) + signal.Offset;

    /// <summary>
    /// Little-endian (Intel) unchecked extract: startBit is the LSB position.
    /// Byte-aligned 8/16/32/64-bit signals use a single load. Unaligned 64-bit signals
    /// span 9 bytes and are assembled with a low <see cref="ulong"/> plus the 9th byte
    /// shifted into the high <c>bitOffset</c> bits — never <c>ulong &lt;&lt; 64</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _ExtractLittleEndianUnchecked(ReadOnlySpan<byte> data, int startBit, int bitLength)
    {
        int byteIndex = startBit / 8;
        int bitOffset = startBit % 8;

        // Byte-aligned common widths: one load, no mask loop.
        if (bitOffset == 0)
        {
            if (bitLength == 8)
            {
                return data[byteIndex];
            }

            if (bitLength == 16)
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(byteIndex, 2));
            }

            if (bitLength == 32)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(byteIndex, 4));
            }

            if (bitLength == 64)
            {
                return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(byteIndex, 8));
            }
        }

        int bytesNeeded = (bitOffset + bitLength + 7) / 8;
        int lowCount = bytesNeeded > 8 ? 8 : bytesNeeded;
        ulong low = 0;
        for (int i = 0; i < lowCount; i++)
        {
            // i is 0..7 so i*8 is 0..56 — within ulong shift range (counts masked to 6 bits).
            low |= (ulong)data[byteIndex + i] << (i * 8);
        }

        ulong value = low >> bitOffset;
        if (bytesNeeded > 8)
        {
            // 9th byte holds the bits that spill above bit 63 before the right shift.
            // After `low >> bitOffset`, those bits belong at the top: shift 64 - bitOffset.
            ulong ninth = data[byteIndex + 8];
            value |= ninth << (64 - bitOffset);
        }

        if (bitLength < 64)
        {
            value &= (1UL << bitLength) - 1UL;
        }

        return value;
    }

    /// <summary>
    /// Big-endian (Motorola) unchecked extract: startBit is the MSB position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _ExtractBigEndianUnchecked(ReadOnlySpan<byte> data, int startBit, int bitLength)
    {
        int bytePos = startBit / 8;
        int bitPos = startBit % 8;
        ulong result = 0;
        int bitsRemaining = bitLength;

        while (bitsRemaining > 0)
        {
            int available = bitPos + 1;
            int bitsToTake = available < bitsRemaining ? available : bitsRemaining;
            int shift = bitPos - bitsToTake + 1;
            byte mask = (byte)((1 << bitsToTake) - 1);
            byte extracted = (byte)((data[bytePos] >> shift) & mask);
            result = (result << bitsToTake) | extracted;
            bitsRemaining -= bitsToTake;
            bytePos++;
            bitPos = 7;
        }

        return result;
    }

    #endregion
}
