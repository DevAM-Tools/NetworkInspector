// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// CRC-32 (IEEE 802.3) computation using the standard polynomial 0xEDB88320 (reflected).
/// Used for Ethernet FCS validation.
/// </summary>
internal static class Crc32
{
    /// <summary>Precomputed lookup table for bytewise CRC-32 computation.</summary>
    private static readonly uint[] Table = GenerateTable();

    /// <summary>
    /// Computes the CRC-32 over a span of bytes.
    /// </summary>
    /// <param name="data">The data to compute the CRC over.</param>
    /// <returns>The CRC-32 value.</returns>
    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)(crc ^ data[i]);
            crc = (crc >> 8) ^ Table[index];
        }

        return crc ^ 0xFFFF_FFFF;
    }

    /// <summary>
    /// Generates the 256-entry CRC-32 lookup table using the IEEE 802.3 polynomial.
    /// </summary>
    private static uint[] GenerateTable()
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
