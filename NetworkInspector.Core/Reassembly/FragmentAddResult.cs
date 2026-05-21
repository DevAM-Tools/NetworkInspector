// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Result returned by <see cref="DatagramFragmentBuffer.AddFragment"/>.
/// </summary>
internal enum FragmentAddResult
{
    /// <summary>Fragment was accepted; datagram is still incomplete.</summary>
    Incomplete,

    /// <summary>Fragment was accepted and the datagram is now complete and can be reassembled.</summary>
    Complete,

    /// <summary>
    /// The fragment overlaps with a previously-received fragment at a different offset.
    /// Per RFC 5722 (IPv6), the entire datagram MUST be silently discarded when such an
    /// overlap is detected. The buffer is now poisoned and must be removed.
    /// </summary>
    OverlapDiscarded,

    /// <summary>
    /// The terminal fragment (MF=0) would produce a total datagram length exceeding
    /// the maximum allowed size (65535 bytes). The datagram can never be completed
    /// within safe bounds and must be discarded immediately. The caller must remove the
    /// reassembly buffer so future fragments for the same key start fresh.
    /// </summary>
    OversizeDiscarded
}
