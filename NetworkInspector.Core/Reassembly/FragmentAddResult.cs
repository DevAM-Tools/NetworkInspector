// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Result returned by <see cref="DatagramFragmentBuffer.AddFragment"/>.
/// Expected outcomes are values and not exceptions, so a hostile fragment stream costs no exception per fragment.
/// </summary>
public enum FragmentAddResult
{
    /// <summary>Fragment was accepted; datagram is still incomplete.</summary>
    Incomplete,

    /// <summary>Fragment was accepted and the datagram is now complete and can be reassembled.</summary>
    Complete,

    /// <summary>
    /// The fragment overlaps with a previously-received fragment at a different offset, or it is a last fragment (MF=0)
    /// whose end contradicts the total length that an earlier last fragment fixed.
    /// Per RFC 5722 (IPv6), the entire datagram MUST be silently discarded when such an
    /// overlap is detected. The buffer is now poisoned and must be removed.
    /// </summary>
    OverlapDiscarded,

    /// <summary>
    /// The fragment ends beyond the maximum datagram size (65535 bytes), or the datagram already holds the maximum
    /// number of fragments. The datagram can never be completed within safe bounds and must be discarded
    /// immediately. The caller must remove the reassembly buffer so future fragments for the same key start fresh.
    /// (Unlike <see cref="OverlapDiscarded"/>, the buffer is not poisoned: it still accepts fragments, so removing it
    /// is the caller's job.)
    /// </summary>
    OversizeDiscarded
}
