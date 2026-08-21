// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv4 network-layer header (20 bytes, no options) for the new
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/> — network layer.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 0x0800 so an outer
///   <see cref="EthernetLayer"/> can auto-patch its EtherType.</item>
///   <item><see cref="IConsumesNextProtocolValue"/> — patches the IP Protocol
///   field from an inner transport layer's protocol type.</item>
///   <item><see cref="IProvidesPseudoHeader"/> — publishes its addresses
///   to <see cref="PostFixContext"/> for transport-layer checksums.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches TotalLength.</item>
///   <item><see cref="FixPhase.PublishPseudoHeader"/> — publishes
///   src/dst/protocol/length to <see cref="PostFixContext"/>.</item>
///   <item><see cref="FixPhase.OuterChecksum"/> — recomputes the IPv4
///   header checksum.</item>
/// </list>
/// </remarks>
public readonly struct IPv4Layer :
    IStatelessLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent,
    IProvidesProtocolType, IProvidesNextProtocolValue<EtherTypeKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader, IFragmentable
{
    /// <summary>Offset of the Protocol field within the IPv4 header.</summary>
    private const int _ProtocolFieldOffset = 9;

    /// <summary>Offset of the Source Address field within the IPv4 header.</summary>
    private const int _SrcAddrOffset = 12;

    /// <summary>Offset of the Destination Address field within the IPv4 header.</summary>
    private const int _DstAddrOffset = 16;

    /// <summary>Offset of the Flags+FragmentOffset combined 16-bit field.</summary>
    private const int _FlagsFragOffsetOffset = 6;

    /// <summary>Mask of the MF (More Fragments) flag inside the combined field.</summary>
    private const ushort _MoreFragmentsMask = 0x2000;

    /// <summary>Mask of the FragmentOffset bits (in 8-octet units).</summary>
    private const ushort _FragmentOffsetMask = 0x1FFF;

    private readonly IPv4Address _SrcAddr;
    private readonly IPv4Address _DstAddr;
    private readonly byte _Ttl;

    /// <summary>Caller-supplied IP Identification (stateless: caller picks; default 0).</summary>
    private readonly ushort _Identification;

    /// <summary>Explicit Protocol value when the user pinned one; meaningful only when <see cref="_ProtocolIsExplicit"/> is <c>true</c>.</summary>
    private readonly byte _ExplicitProtocol;

    /// <summary><c>true</c> when the caller supplied an explicit Protocol via <see cref="Auto.Explicit"/>.</summary>
    private readonly bool _ProtocolIsExplicit;

    /// <summary>
    /// Don't Fragment flag (RFC 791 §3.1).  Stored as <c>!DontFragment</c>
    /// ("fragmentation allowed") so the struct's <c>default</c> instance still
    /// emits DF=1 (the conventional safe default — the layer is normally
    /// constructed via the explicit constructor below).
    /// </summary>
    public bool CanFragment { get; }

    /// <summary>
    /// Creates an IPv4 layer.  TotalLength and HeaderChecksum are always
    /// computed automatically in the post-fix phases.
    /// </summary>
    /// <param name="srcAddr">Source address.</param>
    /// <param name="dstAddr">Destination address.</param>
    /// <param name="ttl">Time-to-Live; default 64.</param>
    /// <param name="identification">
    /// IP Identification field.  Stateless: caller decides.  Default 0.
    /// Cross-frame counters require the stateful layer variant (see roadmap).
    /// </param>
    /// <param name="protocol">
    /// IP Protocol field; <see cref="Auto.Compute"/> (default) means auto-patch
    /// from the inner transport layer's <see cref="IProvidesProtocolType"/>.
    /// Use <see cref="Auto.Explicit"/> to pin.
    /// </param>
    /// <param name="dontFragment">
    /// Don't-Fragment (DF) flag.  Default <c>true</c> (matches the historical
    /// behaviour of <see cref="IPv4Header.Create"/>).  Set to <c>false</c> to
    /// allow router fragmentation — mainly for protocol-conformance tests.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv4Layer(IPv4Address srcAddr, IPv4Address dstAddr, byte ttl = 64,
        ushort identification = 0, Auto<byte> protocol = default, bool dontFragment = true)
    {
        _SrcAddr = srcAddr;
        _DstAddr = dstAddr;
        _Ttl = ttl;
        _Identification = identification;
        _ProtocolIsExplicit = protocol.TryGetExplicit(out byte p);
        _ExplicitProtocol = p;
        // Store "fragmentation allowed" so the struct's default(IPv4Layer) emits DF=1.
        CanFragment = !dontFragment;
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
    public void WriteHeader(scoped Span<byte> dst)
    {
        IPv4Header hdr = IPv4Header.Create(
            _SrcAddr, _DstAddr, _ExplicitProtocol, _Ttl, _Identification,
            dontFragment: !CanFragment);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol)
    {
        // The Protocol field is a single byte; ignore the high byte of "next".
        if (!_ProtocolIsExplicit)
        {
            frame[myOffset + _ProtocolFieldOffset] = (byte)nextProtocol;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        switch (phase)
        {
            case FixPhase.Length:
                // Patch the TotalLength field (covers IPv4 header + everything inside).
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + 2, 2), (ushort)myLength);
                break;

            case FixPhase.PublishPseudoHeader:
                _PublishPseudoHeader(frame, myOffset, myLength, ref ctx);
                break;

            case FixPhase.OuterChecksum:
                // Zero the checksum field, then recompute over the 20-byte header.
                frame[myOffset + 10] = 0;
                frame[myOffset + 11] = 0;
                ushort checksum = ChecksumUtils.IPv4Header(frame.Slice(myOffset, IPv4Header.Size));
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + 10, 2), checksum);
                break;

            default:
                // Other phases not handled by IPv4.
                break;
        }
    }

    /// <summary>
    /// Publishes IPv4 addressing and the post-IP segment description to the
    /// shared <see cref="PostFixContext"/>.  Inner transport layers consume
    /// this in <see cref="FixPhase.InnerChecksum"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _PublishPseudoHeader(scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // Source and destination address copies for the pseudo-header.
        frame.Slice(myOffset + _SrcAddrOffset, 4).CopyTo(ctx.PseudoSrcIp);
        frame.Slice(myOffset + _DstAddrOffset, 4).CopyTo(ctx.PseudoDstIp);
        ctx.PseudoIpLength = 4;
        ctx.PseudoIsIPv6 = false;

        // Protocol field has been patched during the write walk.
        ctx.PseudoProtocol = frame[myOffset + _ProtocolFieldOffset];

        // Transport segment offset/end inside the frame.
        ctx.TransportOffset = myOffset + IPv4Header.Size;
        ctx.TransportEnd = myOffset + myLength;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Patches the IPv4 Flags+FragmentOffset 16-bit field with the per-fragment
    /// MF flag and the FragmentOffset (in 8-octet units), and clears the DF bit
    /// so the fragmented frame is RFC-conformant.  TotalLength and the header
    /// checksum are recomputed by the regular <see cref="FixPhase.Length"/> and
    /// <see cref="FixPhase.OuterChecksum"/> phases that the fragmenting loop
    /// re-runs over each fragment.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        _ = myLength; // Only TotalLength uses it; that field is repatched by FixPhase.Length.
        ushort fragField = (ushort)((fragmentPayloadOffset >> 3) & _FragmentOffsetMask);
        if (moreFragments)
        {
            fragField |= _MoreFragmentsMask;
        }
        // The DF bit is implicitly cleared because we rewrite the full 16-bit field;
        // any DF flag that may have been carried over from the cached header is dropped.
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _FlagsFragOffsetOffset, 2), fragField);
    }
}
