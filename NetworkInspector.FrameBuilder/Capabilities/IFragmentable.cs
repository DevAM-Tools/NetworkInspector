// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer can fragment its payload across multiple frames
/// when the total frame size exceeds the smallest MTU asserted along the
/// outer cons-list.  When at least one stack node carries this capability
/// AND its <see cref="CanFragment"/> returns <c>true</c>,
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}.MoveNext"/> may
/// emit more than one frame for a single build.
/// </summary>
/// <remarks>
/// <para>Strategy: build-once, slice-many.</para>
/// <list type="number">
///   <item>The full unfragmented frame is produced into a per-thread scratch
///   buffer with all post-fix walks executed exactly once.  Inner-of-fragmentable
///   layer checksums (UDP/TCP/SOME-IP) cover the entire datagram and only
///   live in fragment 0.</item>
///   <item>For every emitted fragment the bytes from frame start through the
///   end of the fragmentable layer's header are copied verbatim from the
///   scratch buffer; then the per-fragment slice of the inner-of-fragmentable
///   payload is appended.</item>
///   <item><see cref="PatchFragmentHeader"/> is invoked on the fragmentable
///   layer of every fragment to update its per-fragment fields
///   (TotalLength / FragmentOffset / MoreFragments) and to recompute any
///   layer-local checksum that depends on those bytes (e.g. the IPv4 header
///   checksum).</item>
/// </list>
/// </remarks>
public interface IFragmentable : IProtocolLayer
{
    /// <summary>
    /// <c>true</c> when this concrete instance currently allows fragmentation.
    /// IPv4 returns <c>!DontFragment</c>; the IPv6 fragment extension header
    /// always returns <c>true</c> (its presence in the stack is what signals
    /// the user's intent to allow splitting).
    /// </summary>
    bool CanFragment
    {
        get;
    }

    /// <summary>
    /// Classifies how this layer participates in multi-frame iteration.
    /// Default <see cref="FragmentationKind.NetworkLayer"/> matches the
    /// IP-style "build once, slice many" strategy.  SOME/IP-TP and other
    /// application-layer segmenters override to
    /// <see cref="FragmentationKind.ApplicationSegmentation"/>.
    /// </summary>
    FragmentationKind FragmentationKind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => FragmentationKind.NetworkLayer;
    }

    /// <summary>
    /// Per-fragment payload-slice alignment in bytes.  IP-style fragmentation
    /// requires multiples of 8 (RFC 791 / RFC 8200); SOME/IP-TP encodes
    /// segment offsets in 16-byte units (AUTOSAR §5).  Must be a power of
    /// two; default 8.
    /// </summary>
    int FragmentAlignment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 8;
    }

    /// <summary>
    /// Updates the per-fragment dynamic fields of this layer's already-written
    /// header in <paramref name="frame"/> and recomputes any checksum that
    /// depends on those bytes.  Called once per emitted fragment, AFTER both
    /// the cached header bytes and the fragment payload slice have been
    /// copied into <paramref name="frame"/>.
    /// </summary>
    /// <param name="frame">Destination buffer of the current fragment.</param>
    /// <param name="myOffset">Offset of this layer's header inside <paramref name="frame"/>.</param>
    /// <param name="myLength">
    /// Total bytes from <paramref name="myOffset"/> to the end of the fragment
    /// payload (this header + this fragment's payload slice; excludes any
    /// trailer appended afterwards).
    /// </param>
    /// <param name="fragmentPayloadOffset">
    /// Position of this fragment's payload within the original unfragmented
    /// payload pool, in bytes.  Multiple of <see cref="FragmentAlignment"/>
    /// for all fragments.
    /// </param>
    /// <param name="moreFragments">
    /// <c>true</c> when at least one further fragment will follow.  Maps to
    /// the IPv4 MF flag and the IPv6 fragment-ext "M" bit.
    /// </param>
    void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments);
}
