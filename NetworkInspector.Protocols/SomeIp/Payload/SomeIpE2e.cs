// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Payload;

/// <summary>
/// The end-to-end protection header of a SOME/IP payload and the checksum of profiles 04 and 44.
/// <para>
/// <b>Layouts (big endian, directly behind the SOME/IP header):</b> profiles 04 and 44 carry a 12-byte header: length (16 bit),
/// counter (16 bit), data id (32 bit), CRC (32 bit). Profile 08 carries a 16-byte header: CRC, length, counter and data id, all 32
/// bit. The serialized parameters follow the header. A profile the build does not know consumes no header, because a guessed
/// header length would misalign every inner byte.
/// </para>
/// <para>
/// <b>Checksum (CRC-32/AUTOSAR):</b> polynomial 0xF4ACFB13, reflected input and output, initial value and final XOR
/// 0xFFFFFFFF; the check value of the ASCII bytes "123456789" is 0x1697D06A. The table-driven form (one 256-entry table, one lookup
/// per byte) is used instead of a bit-by-bit loop that would spend eight steps per byte; slicing is not used because the check
/// runs only when the user switched <c>someip.verify_e2e_crc</c> on. The hardware CRC instructions compute CRC-32C, a different
/// polynomial, so they cannot produce this value. The same reasoning as the Ethernet CRC applies: the table is built once, costs
/// 1 KiB, and is read-only afterwards.
/// </para>
/// <para>
/// <b>Input regions of the checksum, in order:</b> (1) the eight SOME/IP header bytes from the client id to the return code, with the
/// TP flag cleared in the message type so that a segmented message and its unsegmented form give the same value; (2) the first eight
/// bytes of the E2E header (length, counter, data id; the stored CRC is not part of its own input); (3) the payload bytes behind the
/// 12-byte E2E header. Verification applies to profiles 04 and 44 only: profile 08 uses a different construction whose check value
/// the pinned parameters cannot derive, and a guessed validity would be worse than none.
/// </para>
/// <para><b>Thread-safety:</b> stateless apart from the table, which the type initializer builds once and nobody writes again.</para>
/// </summary>
internal static class SomeIpE2e
{
    #region Constants

    /// <summary>Header size of profiles 04 and 44 in bytes (format fact: 2 + 2 + 4 + 4).</summary>
    internal const int Header12 = 12;

    /// <summary>Header size of profile 08 in bytes (format fact: four 32-bit fields).</summary>
    internal const int Header16 = 16;

    /// <summary>Reflected form of the polynomial 0xF4ACFB13: the register is shifted right, so the polynomial is mirrored.</summary>
    private const uint _ReflectedPolynomial = 0xC8DF_352F;

    /// <summary>Initial value of the register and value of the final XOR (both all ones, the parameters of the profile).</summary>
    private const uint _AllOnes = 0xFFFF_FFFF;

    /// <summary>The 256-entry lookup table: entry <c>i</c> is the register after feeding the byte <c>i</c> through eight steps.</summary>
    private static readonly uint[] _Table = _GenerateTable();

    #endregion

    #region Header

    /// <summary>
    /// The size of the E2E header of a profile.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <returns>12 for profiles 04 and 44, 16 for profile 08, 0 for no protection and for an unknown profile.</returns>
    internal static int HeaderSize(SomeIpE2eProfile profile) => profile switch
    {
        SomeIpE2eProfile.Profile04 or SomeIpE2eProfile.Profile44 => Header12,
        SomeIpE2eProfile.Profile08 => Header16,
        _ => 0,
    };

    /// <summary>The display name of a profile ("04", "44", "08").</summary>
    /// <param name="profile">The profile.</param>
    internal static string ProfileName(SomeIpE2eProfile profile) => profile switch
    {
        SomeIpE2eProfile.Profile04 => "04",
        SomeIpE2eProfile.Profile44 => "44",
        SomeIpE2eProfile.Profile08 => "08",
        _ => "",
    };

    /// <summary>
    /// Packs the SOME/IP header bytes that the checksum covers (client id to return code) into one word, with the TP flag cleared.
    /// One word instead of eight bytes so a populator that needs it can carry it without an array.
    /// </summary>
    /// <param name="header">The header of the message; for a segmented message the header of the completing segment.</param>
    /// <returns>The eight bytes in network order, as a big-endian number.</returns>
    internal static ulong PackHeaderRegion(in SomeIpHeader header)
    {
        // Client id and session id, then protocol version, interface version, message type without the TP flag, return code.
        // The ACK flag stays: it is part of the message type the sender protected.
        byte messageType = (byte)(header.MessageType & ~SomeIpHeader.TpFlag);
        return ((ulong)header.ClientId << 48)
            | ((ulong)header.SessionId << 32)
            | ((ulong)header.ProtocolVersion << 24)
            | ((ulong)header.InterfaceVersion << 16)
            | ((ulong)messageType << 8)
            | header.ReturnCode;
    }

    #endregion

    #region Checksum

    /// <summary>Feeds bytes into a running checksum. Chaining two calls equals one call over the concatenation.</summary>
    /// <param name="state">The register; start with <see cref="Start"/>.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The new register.</returns>
    internal static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        // Reflected CRC: bytes enter least-significant bit first, which is why the table is indexed by the low byte of the register.
        uint crc = state;
        for (int i = 0; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ _Table[(byte)(crc ^ data[i])];
        }

        return crc;
    }

    /// <summary>The initial register.</summary>
    internal static uint Start() => _AllOnes;

    /// <summary>Applies the final XOR.</summary>
    /// <param name="state">The register after the last <see cref="Update"/>.</param>
    internal static uint Finish(uint state) => state ^ _AllOnes;

    /// <summary>
    /// Computes the checksum of a profile 04 or 44 message over its three input regions.
    /// </summary>
    /// <param name="headerRegion">The eight SOME/IP header bytes, packed by <see cref="PackHeaderRegion"/>.</param>
    /// <param name="protectedPayload">The payload including the 12-byte E2E header; at least 12 bytes.</param>
    /// <returns>The checksum the sender should have stored.</returns>
    internal static uint ComputeProfile04(ulong headerRegion, ReadOnlySpan<byte> protectedPayload)
    {
        Span<byte> region = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(region, headerRegion);
        uint state = Update(Start(), region);
        state = Update(state, protectedPayload[..8]);
        state = Update(state, protectedPayload[Header12..]);
        return Finish(state);
    }

    private static uint[] _GenerateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ _ReflectedPolynomial
                    : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }

    #endregion
}
