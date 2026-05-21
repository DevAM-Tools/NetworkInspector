// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Stateful IPv6 Fragment extension header that auto-increments the 32-bit
/// Identification field per logical packet of a session, while keeping the
/// same Identification across all fragments of one packet (as RFC 8200 §4.5
/// requires).
/// </summary>
/// <remarks>
/// <para>
/// Behaves identically to <see cref="IPv6FragmentExtensionLayer"/> except that
/// the Identification field is sourced from <see cref="SessionState.IPv6NextFragId"/>.
/// The fragmenter cycle inside
/// <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/> writes
/// the layer header exactly once per logical packet (during the scratch
/// build), so the counter advances by exactly one per packet — not per
/// emitted fragment.
/// </para>
/// <para>
/// Only usable inside a <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </para>
/// </remarks>
public readonly struct IPv6FragmentExtensionLayerWithAutoId :
    IStatefulLayer, IInteriorLayer, IPseudoHeaderIndependent, IIPv6ExtensionLayer, IProvidesProtocolType,
    IProvidesNextProtocolValue<IpNextProtocolKind>, IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader,
    IFragmentable
{
    private const int NextHeaderOffset = 0;
    private const int FragmentOffsetAndFlagsOffset = 2;
    private const ushort MoreFragmentsMask = 0x0001;

    private readonly byte _ExplicitNextHeader;
    private readonly uint _SeedIdentification;

    /// <summary>Creates a stateful auto-id IPv6 Fragment extension header layer.</summary>
    /// <param name="initialIdentification">
    /// Seed for the per-session 32-bit Identification counter.  Default 0.
    /// The counter is incremented by 1 per logical packet (not per fragment).
    /// </param>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto{T}.Compute"/> (default) means
    /// auto-patch from the inner layer's protocol type.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6FragmentExtensionLayerWithAutoId(uint initialIdentification = 0, Auto<byte> nextHeader = default)
    {
        _SeedIdentification = initialIdentification;
        _ExplicitNextHeader = nextHeader.TryGetExplicit(out byte nh) ? nh : (byte)0;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IPv6FragmentExtensionHeader.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6Fragment;
    }

    /// <inheritdoc />
    public byte ExtensionProtocol
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6Fragment;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeState(ref SessionState state)
    {
        state.IPv6NextFragId = _SeedIdentification;
        state.HasIPv6AutoFragId = true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
    {
        uint id = state.IPv6NextFragId;
        // Wrap-around is the natural uint behaviour.
        unchecked
        {
            state.IPv6NextFragId = id + 1u;
        }

        IPv6FragmentExtensionHeader hdr = new()
        {
            NextHeader = _ExplicitNextHeader, // 0 = will be patched by PatchNextProtocol
            Reserved = 0,
            FragmentOffsetAndFlags = (ushort)0, // patched per fragment
            Identification = id,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
    {
        if (_ExplicitNextHeader == 0)
        {
            frame[myOffset + NextHeaderOffset] = (byte)next;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.PublishPseudoHeader)
        {
            return;
        }

        // Forward the upper-layer protocol number to the pseudo-header so the
        // transport checksum uses the correct value (RFC 8200 §8.1) and skip
        // past this extension header.
        ctx.PseudoProtocol = frame[myOffset + NextHeaderOffset];
        ctx.TransportOffset = myOffset + IPv6FragmentExtensionHeader.Size;
        _ = myLength;
    }

    /// <inheritdoc />
    public bool CanFragment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // Presence of this layer in the stack is the user's explicit consent
        // to fragment.  Always allow splitting.
        get => true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Patches the FragmentOffset (in 8-octet units) and the M flag.  The
    /// Identification field stays at the value written into the cached
    /// header for this packet (see <see cref="WriteHeader"/>); it is the
    /// same across every fragment.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        _ = myLength;
        ushort word = (ushort)(((fragmentPayloadOffset >> 3) & 0x1FFF) << 3);
        if (moreFragments)
        {
            word |= MoreFragmentsMask;
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + FragmentOffsetAndFlagsOffset, 2), word);
    }
}
