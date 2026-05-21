// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv6 Fragment extension header (RFC 8200 §4.5, IP protocol 44).  Adding
/// this layer between an IPv6 layer and a transport layer signals the user's
/// intent to allow IP-layer fragmentation; the
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/> emits one frame
/// per fragment when the unfragmented datagram exceeds the smallest MTU
/// asserted along the cons-list.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IIPv6ExtensionLayer"/> (chains via the existing
///   <c>Then</c> extension on IPv6 / extension headers).</item>
///   <item><see cref="IProvidesProtocolType"/> = 44 (IPv6 Fragment).</item>
///   <item><see cref="IProvidesNextProtocolValue"/> — outer IPv6 / ext patches our slot.</item>
///   <item><see cref="IConsumesNextProtocolValue"/> — patches our NextHeader from inner.</item>
///   <item><see cref="IProvidesPseudoHeader"/> — forwards the outer IPv6's
///   pseudo-header bytes by overwriting the upper-layer protocol byte and
///   advancing <see cref="PostFixContext.TransportOffset"/>.</item>
///   <item><see cref="IFragmentable"/> — splits the inner-of-fragment payload
///   across one frame per fragment.</item>
/// </list>
/// </remarks>
public readonly struct IPv6FragmentExtensionLayer :
    IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent, IIPv6ExtensionLayer,
    IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader, IFragmentable
{
    /// <summary>Offset of the NextHeader field within the fragment ext header.</summary>
    private const int NextHeaderOffset = 0;

    /// <summary>Offset of the FragmentOffset+Flags field within the fragment ext header.</summary>
    private const int FragmentOffsetAndFlagsOffset = 2;

    /// <summary>Offset of the Identification field within the fragment ext header.</summary>
    private const int IdentificationOffset = 4;

    /// <summary>Mask of the M (More Fragments) flag inside the packed 16-bit field.</summary>
    private const ushort MoreFragmentsMask = 0x0001;

    private readonly byte _ExplicitNextHeader;
    private readonly bool _NextHeaderIsExplicit;
    private readonly uint _Identification;

    /// <summary>Creates an IPv6 Fragment extension header layer.</summary>
    /// <param name="identification">
    /// 32-bit Identification field shared by all fragments of one datagram.
    /// Stateless: caller decides.  Default 0.  Cross-frame counters require
    /// using <see cref="IPv6FragmentExtensionLayerWithAutoId"/> inside a
    /// <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
    /// </param>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto{T}.Compute"/> (default) means auto-patch
    /// from the inner layer's protocol type.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6FragmentExtensionLayer(uint identification = 0, Auto<byte> nextHeader = default)
    {
        _Identification = identification;
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
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
    public void WriteHeader(scoped Span<byte> dst)
    {
        IPv6FragmentExtensionHeader hdr = new()
        {
            NextHeader = _ExplicitNextHeader, // 0 = will be patched by PatchNextProtocol
            Reserved = 0,
            FragmentOffsetAndFlags = (ushort)0, // patched per fragment
            Identification = _Identification,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
    {
        if (!_NextHeaderIsExplicit)
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
    /// Identification field stays at its initial value across all fragments.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        _ = myLength;
        // FragmentOffset occupies the high 13 bits of the 16-bit word; the M
        // flag is the LSB.  RFC 8200 §4.5: fragment offset is in 8-octet units.
        ushort word = (ushort)(((fragmentPayloadOffset >> 3) & 0x1FFF) << 3);
        if (moreFragments)
        {
            word |= MoreFragmentsMask;
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + FragmentOffsetAndFlagsOffset, 2), word);
        // Identification field stays at the value written into the cached
        // header (offset IdentificationOffset = 4); nothing to patch here.
    }
}
