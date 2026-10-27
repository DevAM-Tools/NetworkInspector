// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// UDP header (8 bytes).
/// Layout per RFC 768: SrcPort(2), DstPort(2), Length(2), Checksum(2).
/// </summary>
/// <remarks>
/// Length is left at 0 and patched by <c>UdpLayer</c> <c>FixPhase.Length</c>.
/// Checksum is optional (0 = no checksum).
/// </remarks>
internal readonly struct UdpHeader
{
    /// <summary>Size of the UDP header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>Source port number.</summary>
    internal ushort SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number.</summary>
    internal ushort DstPort
    {
        get; init;
    }

    /// <summary>Length of UDP header + payload. Set to 0 for fixup.</summary>
    internal ushort Length
    {
        get; init;
    }

    /// <summary>Checksum (0 = no checksum). Optional for IPv4, mandatory for IPv6.</summary>
    internal ushort Checksum
    {
        get; init;
    }

    /// <summary>
    /// Creates a UDP header with length and checksum set to 0 for fixup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static UdpHeader Create(ushort srcPort, ushort dstPort)
    {
        return new UdpHeader
        {
            SrcPort = srcPort,
            DstPort = dstPort,
            Length = (ushort)0, // patched by UdpLayer FixPhase.Length
            Checksum = (ushort)0,
        };
    }

    /// <summary>Writes the 8 header bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
    /// <remarks>
    /// Replaces a generated writer: one length check, then fixed-offset big-endian stores on an exact-size slice, so the JIT
    /// drops the per-store bounds checks. Layers call it directly (no interface dispatch). Writes nothing when it fails.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        // One length check up front; a short destination is an expected failure, not an exception.
        if (destination.Length < Size)
        {
            bytesWritten = 0;
            return false;
        }

        // Exact-size slice: the fixed-offset stores below need no further bounds checks after JIT.
        Span<byte> d = destination[..Size];
        BinaryPrimitives.WriteUInt16BigEndian(d, SrcPort);        // 0..2
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], DstPort);   // 2..4
        BinaryPrimitives.WriteUInt16BigEndian(d[4..], Length);    // 4..6
        BinaryPrimitives.WriteUInt16BigEndian(d[6..], Checksum);  // 6..8
        bytesWritten = Size;
        return true;
    }
}
