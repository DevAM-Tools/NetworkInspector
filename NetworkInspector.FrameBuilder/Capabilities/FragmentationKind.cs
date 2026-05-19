// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Classifies how an <see cref="IFragmentable"/> layer participates in
/// multi-frame iteration, controlling which post-fix phases the
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/> /
/// <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/> runs on
/// the unfragmented scratch versus per emitted frame.
/// </summary>
/// <remarks>
/// The kind is reported by the innermost <see cref="IFragmentable"/> along
/// the cons-list and selects the iterator's branch.  Layers whose kind does
/// not match the active fragmentation kind are skipped during
/// <see cref="IFragmentable.PatchFragmentHeader"/>; their already-cached
/// header bytes therefore stay verbatim across all emitted frames.
/// </remarks>
public enum FragmentationKind : byte
{
    /// <summary>
    /// IP-style fragmentation (RFC 791 §3.2 / RFC 8200 §4.5).  Inner-of-fragmentable
    /// transport checksums (UDP / TCP / ICMP) cover the entire unfragmented
    /// datagram and are computed once on the scratch.  Per emitted fragment
    /// only <see cref="FixPhase.Length"/> + <see cref="FixPhase.OuterChecksum"/>
    /// + <see cref="FixPhase.Trailer"/> run; the inner-of-fragmentable bytes
    /// are sliced raw from the scratch buffer.  Default alignment 8 octets.
    /// </summary>
    NetworkLayer = 0,

    /// <summary>
    /// Application-layer segmentation (e.g. SOME/IP-TP per AUTOSAR §5).
    /// Every emitted "fragment" is a self-contained network-layer datagram
    /// carrying its own complete payload checksum: the iterator runs ALL
    /// post-fix phases (<see cref="FixPhase.Length"/>,
    /// <see cref="FixPhase.PublishPseudoHeader"/>,
    /// <see cref="FixPhase.InnerChecksum"/>, <see cref="FixPhase.OuterChecksum"/>,
    /// <see cref="FixPhase.Trailer"/>) per emitted segment.  Outer
    /// <see cref="IFragmentable"/> layers (e.g. an IPv4 layer with DF cleared)
    /// are <b>not</b> patched: each segment is its own non-fragmented IP
    /// datagram.  Default alignment 16 octets (SOME/IP-TP encodes the segment
    /// offset in 16-byte units).
    /// </summary>
    ApplicationSegmentation = 1,
}
