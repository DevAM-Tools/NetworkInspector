// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Stateful IPv4 layer that auto-increments the IP Identification field on
/// every frame.  Behaves identically to <see cref="IPv4Layer"/> in every
/// other respect.
/// </summary>
/// <remarks>
/// <para>
/// State slot: <see cref="SessionState.IPv4NextId"/>.  Initialised to the
/// caller-supplied seed (default <c>0</c>); incremented by 1 per frame, with
/// natural wrap-around at <see cref="ushort.MaxValue"/>.
/// </para>
/// <para>
/// Only usable inside a <see cref="Session{TStack,TTrailer,TInterceptor}"/>.  Direct
/// stateless emission is rejected at compile time by the
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>'s
/// <c>IStatelessStack</c> constraint.
/// </para>
/// </remarks>
public readonly struct IPv4LayerWithAutoIpId :
    IStatefulLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent, IProvidesProtocolType,
    IProvidesNextProtocolValue<EtherTypeKind>, IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader,
    IFragmentable
{
    private const int ProtocolFieldOffset = 9;
    private const int SrcAddrOffset = 12;
    private const int DstAddrOffset = 16;

    /// <summary>Offset of the Flags+FragmentOffset combined 16-bit field.</summary>
    private const int FlagsFragOffsetOffset = 6;

    /// <summary>Mask of the MF (More Fragments) flag inside the combined field.</summary>
    private const ushort MoreFragmentsMask = 0x2000;

    /// <summary>Mask of the FragmentOffset bits (in 8-octet units).</summary>
    private const ushort FragmentOffsetMask = 0x1FFF;

    private readonly IPv4Address _SrcAddr;
    private readonly IPv4Address _DstAddr;
    private readonly byte _Ttl;

    /// <summary>Initial Identification value to seed the session counter with.</summary>
    private readonly ushort _SeedIdentification;

    /// <summary>Explicit Protocol value when pinned; <c>0</c> means "patch from inner".</summary>
    private readonly byte _ExplicitProtocol;

    /// <summary>See <see cref="IPv4Layer"/> for the negation rationale.</summary>
    private readonly bool _ClearDontFragment;

    /// <summary>Creates a stateful auto-IPID IPv4 layer.</summary>
    /// <param name="srcAddr">Source address.</param>
    /// <param name="dstAddr">Destination address.</param>
    /// <param name="ttl">Time-to-Live; default 64.</param>
    /// <param name="initialIdentification">
    /// Seed for the per-session IP Identification counter.  Default 0.  The
    /// counter is incremented by 1 per emitted frame.
    /// </param>
    /// <param name="protocol">
    /// IP Protocol field; <see cref="Auto{T}.Compute"/> (default) means auto-patch
    /// from the inner transport layer.
    /// </param>
    /// <param name="dontFragment">DF flag; default <c>true</c>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv4LayerWithAutoIpId(
        IPv4Address srcAddr,
        IPv4Address dstAddr,
        byte ttl = 64,
        ushort initialIdentification = 0,
        Auto<byte> protocol = default,
        bool dontFragment = true)
    {
        _SrcAddr = srcAddr;
        _DstAddr = dstAddr;
        _Ttl = ttl;
        _SeedIdentification = initialIdentification;
        _ExplicitProtocol = protocol.TryGetExplicit(out byte p) ? p : (byte)0;
        _ClearDontFragment = !dontFragment;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IPv4Header.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => EtherTypes.IPv4;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeState(ref SessionState state)
    {
        state.IPv4NextId = _SeedIdentification;
        state.HasIPv4AutoId = true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
    {
        ushort id = state.IPv4NextId;
        // Wrap-around is the natural ushort behaviour.
        unchecked
        {
            state.IPv4NextId = (ushort)(id + 1);
        }

        IPv4Header hdr = IPv4Header.Create(
            _SrcAddr, _DstAddr, _ExplicitProtocol, _Ttl, id,
            dontFragment: !_ClearDontFragment);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
    {
        if (_ExplicitProtocol == 0)
        {
            frame[myOffset + ProtocolFieldOffset] = (byte)next;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        switch (phase)
        {
            case FixPhase.Length:
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + 2, 2), (ushort)myLength);
                break;

            case FixPhase.PublishPseudoHeader:
                PublishPseudoHeader(frame, myOffset, myLength, ref ctx);
                break;

            case FixPhase.OuterChecksum:
                frame[myOffset + 10] = 0;
                frame[myOffset + 11] = 0;
                ushort checksum = ChecksumUtils.IPv4Header(frame.Slice(myOffset, IPv4Header.Size));
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + 10, 2), checksum);
                break;

            default:
                break;
        }
    }

    /// <summary>See <see cref="IPv4Layer"/> for the pseudo-header publish details.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PublishPseudoHeader(scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        frame.Slice(myOffset + SrcAddrOffset, 4).CopyTo(ctx.PseudoSrcIp);
        frame.Slice(myOffset + DstAddrOffset, 4).CopyTo(ctx.PseudoDstIp);
        ctx.PseudoIpLength = 4;
        ctx.PseudoIsIPv6 = false;
        ctx.PseudoProtocol = frame[myOffset + ProtocolFieldOffset];
        ctx.TransportOffset = myOffset + IPv4Header.Size;
        ctx.TransportEnd = myOffset + myLength;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Fragmentation capability is determined by the DF flag.  Mirrors
    /// <see cref="IPv4Layer.CanFragment"/>.
    /// </remarks>
    public bool CanFragment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ClearDontFragment;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Patches the IPv4 Flags+FragmentOffset 16-bit field with the per-fragment
    /// MF flag and the FragmentOffset (in 8-octet units) and clears the DF bit.
    /// TotalLength and the header checksum are recomputed by the regular
    /// <see cref="FixPhase.Length"/> and <see cref="FixPhase.OuterChecksum"/>
    /// phases that the fragmenting loop re-runs over each fragment.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        // myLength is patched separately by FixPhase.Length; nothing to do here.
        ushort fragField = (ushort)((fragmentPayloadOffset >> 3) & FragmentOffsetMask);
        if (moreFragments)
        {
            fragField |= MoreFragmentsMask;
        }
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + FlagsFragOffsetOffset, 2), fragField);
    }
}

