// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// CRC-32 (IEEE 802.3) computation using the standard polynomial 0xEDB88320 (reflected).
/// Used for Ethernet FCS validation.
/// <para>
/// Algorithm: the classic table-driven bytewise CRC (one 256-entry table, one lookup per input byte) rather than a
/// bit-by-bit loop, which would take eight shift/XOR steps per byte. Slicing-by-N is not used. The hardware CRC32
/// instructions of x86 and ARM compute CRC-32C, a different polynomial, so they cannot produce this checksum. The
/// check only runs when the <c>eth.assume_fcs</c> setting is enabled.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> the table is built once in the type initializer and never modified; safe for concurrent use.</para>
/// </remarks>
internal static class Crc32
{
    /// <summary>Precomputed lookup table for bytewise CRC-32 computation.</summary>
    private static readonly uint[] _Table = _GenerateTable();

    /// <summary>
    /// Computes the CRC-32 over a span of bytes.
    /// </summary>
    /// <param name="data">The data to compute the CRC over.</param>
    /// <returns>The CRC-32 value (the standard CRC-32 as used by Ethernet, zip and others, e.g. 0xCBF43926 for "123456789").</returns>
    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        // Reflected CRC-32: the register starts as all ones and is inverted at the end (per IEEE 802.3), and bytes
        // are fed least-significant bit first, which is why the table is indexed by the low byte of the register.
        // The cast to byte keeps exactly the low 8 bits (intentional truncation, no overflow concern).
        uint crc = 0xFFFF_FFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)(crc ^ data[i]);
            crc = (crc >> 8) ^ _Table[index];
        }

        return crc ^ 0xFFFF_FFFF;
    }

    /// <summary>
    /// Generates the 256-entry CRC-32 lookup table using the IEEE 802.3 polynomial. Entry <c>i</c> is the register
    /// after feeding the byte <c>i</c> through eight shift/XOR steps. Runs once from the type initializer.
    /// </summary>
    private static uint[] _GenerateTable()
    {
        const uint Polynomial = 0xEDB8_8320;
        uint[] table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ Polynomial
                    : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
