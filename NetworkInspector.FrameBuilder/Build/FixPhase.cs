// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Phase of post-fix processing.  Each phase walks the cons-list once; layers
/// participate only in the phases they care about.
/// </summary>
public enum FixPhase : byte
{
    /// <summary>
    /// Fill in length fields (IP TotalLength / PayloadLength, UDP Length, …).
    /// Walked outer→inner so transport layers can read their containing IP
    /// length if they need to.
    /// </summary>
    Length = 0,

    /// <summary>
    /// Network layers publish their pseudo-header source/destination/protocol
    /// data into <see cref="PostFixContext"/> so transport layers can compute
    /// pseudo-header checksums.
    /// </summary>
    PublishPseudoHeader = 1,

    /// <summary>
    /// Compute transport-layer checksums (TCP/UDP/ICMPv6).  Walked inner→outer
    /// so transport runs before any outer checksum that might cover it.
    /// </summary>
    InnerChecksum = 2,

    /// <summary>
    /// Compute outer checksums (IPv4 header checksum).
    /// </summary>
    OuterChecksum = 3,

    /// <summary>
    /// Trailer phase (FCS, padding, MIC, auth-tag).  Runs last after every
    /// length / checksum is finalised.
    /// </summary>
    Trailer = 4,
}
