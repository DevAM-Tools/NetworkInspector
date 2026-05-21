// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalPdu;

/// <summary>
/// Bit-level signal extraction engine for decoding signals from raw PDU bytes.
/// Supports big-endian and little-endian byte order, signed/unsigned integers,
/// and IEEE 754 float32/float64 reinterpretation.
/// <para>
/// Algorithm:
/// 1. Given start_bit and bit_length, compute the byte range containing the signal.
/// 2. Extract the raw bits in the specified byte order.
/// 3. Apply sign extension for signed integer types.
/// 4. For float types, reinterpret the raw bits as IEEE 754.
/// 5. Apply linear scaling: physical = raw × factor + offset.
/// </para>
/// </summary>
internal static class SignalDecoder
{
    /// <summary>
    /// Extracts a raw unsigned integer value from a byte span using bit-level addressing.
    /// </summary>
    /// <param name="data">The PDU payload bytes.</param>
    /// <param name="startBit">The starting bit position.</param>
    /// <param name="bitLength">Number of bits to extract (1-64).</param>
    /// <param name="bigEndian">True for big-endian (Motorola) byte order, false for little-endian (Intel).</param>
    /// <returns>The extracted raw unsigned value, or 0 if the bit range exceeds the data.</returns>
    internal static ulong ExtractRawValue(ReadOnlySpan<byte> data, int startBit, int bitLength, bool bigEndian)
    {
        if (bitLength <= 0 || bitLength > 64)
        {
            return 0;
        }

        if (bigEndian)
        {
            return ExtractBigEndian(data, startBit, bitLength);
        }

        return ExtractLittleEndian(data, startBit, bitLength);
    }

    /// <summary>
    /// Decodes a signal value with full scaling and type handling.
    /// Returns the physical value as a double.
    /// </summary>
    /// <param name="data">The PDU payload bytes.</param>
    /// <param name="signal">The signal definition with bit position, type, and scaling.</param>
    /// <returns>The decoded physical value.</returns>
    internal static double DecodeSignal(ReadOnlySpan<byte> data, SignalDefinition signal)
    {
        ulong raw = ExtractRawValue(data, signal.StartBit, signal.BitLength, signal.IsBigEndian);

        double value = signal.DataType switch
        {
            string s when s.Equals("signed", StringComparison.OrdinalIgnoreCase) => ApplySigned(raw, signal.BitLength),
            string s when s.Equals("float32", StringComparison.OrdinalIgnoreCase) && signal.BitLength == 32 => BitConverter.Int32BitsToSingle((int)raw),
            string s when s.Equals("float64", StringComparison.OrdinalIgnoreCase) && signal.BitLength == 64 => BitConverter.Int64BitsToDouble((long)raw),
            _ => raw, // "unsigned" or default
        };

        // Apply linear scaling: physical = raw * factor + offset
        return value * signal.Factor + signal.Offset;
    }

    /// <summary>
    /// Extracts the raw unscaled value for display purposes.
    /// </summary>
    internal static ulong ExtractRaw(ReadOnlySpan<byte> data, SignalDefinition signal)
        => ExtractRawValue(data, signal.StartBit, signal.BitLength, signal.IsBigEndian);

    /// <summary>
    /// Extracts the raw mux selector value.
    /// </summary>
    internal static ulong ExtractMuxValue(ReadOnlySpan<byte> data, MuxSignalDefinition mux)
        => ExtractRawValue(data, mux.StartBit, mux.BitLength, mux.IsBigEndian);

    /// <summary>
    /// Applies sign extension to a raw value based on the bit length.
    /// </summary>
    private static double ApplySigned(ulong raw, int bitLength)
    {
        // Check if the sign bit is set
        if ((raw & (1UL << (bitLength - 1))) != 0)
        {
            // Sign-extend to 64 bits: fill upper bits with 1s
            ulong mask = ulong.MaxValue << bitLength;
            return (long)(raw | mask);
        }
        return (long)raw;
    }

    /// <summary>
    /// Extracts bits in big-endian (Motorola) byte order.
    /// In Motorola order, start_bit is the MSB position.
    /// Bit numbering: byte 0 contains bits 7..0, byte 1 contains bits 15..8, etc.
    /// </summary>
    private static ulong ExtractBigEndian(ReadOnlySpan<byte> data, int startBit, int bitLength)
    {
        // Big-endian (Motorola): start_bit is the MSB position
        // Convert to byte offset and bit offset within that byte
        int bytePos = startBit / 8;
        int bitPos = startBit % 8;

        ulong result = 0;
        int bitsRemaining = bitLength;

        // Extract from MSB to LSB
        while (bitsRemaining > 0 && bytePos < data.Length)
        {
            // Available bits from current position to the right edge of the byte
            int available = bitPos + 1;
            int bitsToTake = Math.Min(available, bitsRemaining);

            // Extract the bits
            int shift = bitPos - bitsToTake + 1;
            byte mask = (byte)((1 << bitsToTake) - 1);
            byte extracted = (byte)((data[bytePos] >> shift) & mask);

            result = (result << bitsToTake) | extracted;
            bitsRemaining -= bitsToTake;

            // Move to next byte, start from MSB (bit 7)
            bytePos++;
            bitPos = 7;
        }

        return result;
    }

    /// <summary>
    /// Extracts bits in little-endian (Intel) byte order.
    /// In Intel order, start_bit is the LSB position.
    /// Bit numbering: bit 0 is the LSB of byte 0, bit 8 is the LSB of byte 1, etc.
    /// </summary>
    private static ulong ExtractLittleEndian(ReadOnlySpan<byte> data, int startBit, int bitLength)
    {
        ulong result = 0;
        int currentBit = startBit;

        for (int i = 0; i < bitLength; i++)
        {
            int byteIndex = currentBit / 8;
            int bitIndex = currentBit % 8;

            if (byteIndex < data.Length)
            {
                if ((data[byteIndex] & (1 << bitIndex)) != 0)
                {
                    result |= 1UL << i;
                }
            }

            currentBit++;
        }

        return result;
    }
}
