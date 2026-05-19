// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Ethernet Frame Check Sequence (FCS) trailer — 4-byte IEEE 802.3 CRC-32
/// over everything in the frame up to but not including the FCS slot.
/// </summary>
/// <remarks>
/// <para>
/// In most paths (raw sockets, PCAP capture without FCS, hardware offload)
/// the FCS is stripped or computed by the NIC.  This trailer is for the
/// niche cases that DO need an FCS in the byte buffer (PCAP-with-FCS,
/// hardware loopback, protocol-conformance vectors).
/// </para>
/// <para>
/// Use <see cref="Crc32"/> (default-constructed) as the trailer instance.
/// </para>
/// <para>Thread safety: stateless; safe to share.</para>
/// </remarks>
public readonly struct EthernetFcs : ITrailerLayer
{
    /// <summary>Width of the FCS in bytes.</summary>
    public const int Size = 4;

    /// <summary>IEEE 802.3 CRC-32 polynomial in reversed (LSB-first) form.</summary>
    private const uint Polynomial = 0xEDB88320u;

    /// <summary>Pre-computed 256-entry CRC-32 lookup table (IEEE 802.3 polynomial).</summary>
    private static readonly uint[] _Table = BuildTable();

    /// <summary>Default-constructed FCS trailer using IEEE 802.3 CRC-32.</summary>
    public static EthernetFcs Crc32 => default;

    /// <inheritdoc />
    public int TrailerSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Size;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTrailer(Span<byte> frame, int payloadEnd)
    {
        // Compute over everything before the FCS slot, then write LE.
        ReadOnlySpan<byte> data = frame[..payloadEnd];
        uint crc = ComputeCrc32(data);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(payloadEnd, Size), crc);
    }

    /// <summary>Computes the IEEE 802.3 CRC-32 of <paramref name="data"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint[] table = _Table;
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }

    /// <summary>Builds the 256-entry CRC-32 lookup table once at static-init time.</summary>
    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }
}
