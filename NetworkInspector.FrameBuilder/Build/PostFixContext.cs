// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Carries cross-layer information needed by the post-fix phases of a frame
/// build.  Lives entirely on the stack as a <c>ref struct</c>.
/// </summary>
/// <remarks>
/// <para>
/// Network layers populate the pseudo-header fields during
/// <see cref="FixPhase.PublishPseudoHeader"/>; transport layers consume them
/// during <see cref="FixPhase.InnerChecksum"/>.
/// </para>
/// <para>
/// The <see cref="LayerOffsets"/> span is sized to the cons-list depth and
/// addresses layers in <em>outer→inner</em> order: index 0 is the link layer.
/// The <see cref="TransportOffset"/> + <see cref="TransportEnd"/> fields delimit
/// the transport-layer header + payload so the checksum routines can read it
/// without knowing the cons-list shape.
/// </para>
/// </remarks>
public ref struct PostFixContext
{
    /// <summary>Total bytes that were written to the frame (header(s) + payload).</summary>
    public int TotalLength
    {
        get; set;
    }

    /// <summary>Per-layer header start offsets, outer→inner.  Length == cons-list depth.</summary>
    internal Span<int> LayerOffsets;

    /// <summary>Number of valid entries in <see cref="LayerOffsets"/>.</summary>
    public int LayerCount
    {
        get; set;
    }

    /// <summary>Pseudo-header source IP (4 bytes IPv4, 16 bytes IPv6).  Empty if not set.</summary>
    public Span<byte> PseudoSrcIp
    {
        get; set;
    }

    /// <summary>Pseudo-header destination IP (4 / 16 bytes).  Empty if not set.</summary>
    public Span<byte> PseudoDstIp
    {
        get; set;
    }

    /// <summary>Length of the pseudo-header IP-address portion in bytes (4 or 16).</summary>
    public byte PseudoIpLength
    {
        get; set;
    }

    /// <summary><c>true</c> when the pseudo-header is an IPv6 pseudo-header.</summary>
    public bool PseudoIsIPv6
    {
        get; set;
    }

    /// <summary>IP protocol number for the pseudo-header (e.g. 6 = TCP, 17 = UDP, 58 = ICMPv6).</summary>
    public byte PseudoProtocol
    {
        get; set;
    }

    /// <summary>Frame offset where the transport layer's header starts.</summary>
    public int TransportOffset
    {
        get; set;
    }

    /// <summary>Frame offset where the transport layer's header + payload ends (exclusive).</summary>
    public int TransportEnd
    {
        get; set;
    }
}
