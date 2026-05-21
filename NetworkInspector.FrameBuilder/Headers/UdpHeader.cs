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
[BinaryWritable]
internal readonly partial struct UdpHeader
{
    /// <summary>Size of the UDP header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>Source port number.</summary>
    internal U16BE SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number.</summary>
    internal U16BE DstPort
    {
        get; init;
    }

    /// <summary>Length of UDP header + payload. Set to 0 for fixup.</summary>
    internal U16BE Length
    {
        get; init;
    }

    /// <summary>Checksum (0 = no checksum). Optional for IPv4, mandatory for IPv6.</summary>
    internal U16BE Checksum
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
}
