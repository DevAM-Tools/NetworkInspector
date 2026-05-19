// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv6 network-layer header (40 bytes) for the new <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/>.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 0x86DD (EtherType IPv6) so an
///   outer Ethernet/VLAN auto-patches its EtherType.</item>
///   <item><see cref="IProvidesNextProtocolValue"/> — outer link layer must patch us.</item>
///   <item><see cref="IConsumesNextProtocolValue"/> — patches our NextHeader byte
///   from the next-inner layer's protocol type.</item>
///   <item><see cref="IProvidesPseudoHeader"/> — publishes addresses for transport-checksum.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches PayloadLength (excludes the 40-byte header).</item>
///   <item><see cref="FixPhase.PublishPseudoHeader"/> — publishes 16-byte src/dst plus
///   transport extents.</item>
/// </list>
/// </remarks>
public readonly struct IPv6Layer :
    IStatelessLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent,
    IProvidesProtocolType, IProvidesNextProtocolValue<EtherTypeKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader
{
    /// <summary>Offset of the NextHeader field within the IPv6 header.</summary>
    private const int NextHeaderOffset = 6;

    /// <summary>Offset of the SrcAddr field within the IPv6 header.</summary>
    private const int SrcAddrOffset = 8;

    /// <summary>Offset of the DstAddr field within the IPv6 header.</summary>
    private const int DstAddrOffset = 24;

    private readonly IPv6Address _SrcAddr;
    private readonly IPv6Address _DstAddr;
    private readonly byte _HopLimit;

    /// <summary>IP-layer MTU in bytes (IPv6 header + payload).  Zero means no limit.</summary>
    private readonly ushort _Mtu;

    /// <summary>Explicit NextHeader value when caller pinned one; meaningful only when <see cref="_NextHeaderIsExplicit"/> is <c>true</c>.</summary>
    private readonly byte _ExplicitNextHeader;

    /// <summary>
    /// <c>true</c> when the caller supplied an explicit NextHeader via
    /// <see cref="Auto{T}.Explicit"/>; <c>false</c> means auto-patch from
    /// the inner layer's protocol type.  This bool flag distinguishes an
    /// explicit <c>0</c> (HopByHop) from "auto".
    /// </summary>
    private readonly bool _NextHeaderIsExplicit;

    /// <summary>
    /// Maximum IPv6 payload size in bytes (MTU minus the 40-byte IPv6 header).
    /// Returns <see cref="int.MaxValue"/> when no MTU is set.
    /// </summary>
    public int MaxPayloadSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Mtu == 0 ? int.MaxValue : _Mtu - IPv6Header.Size;
    }

    /// <summary>Creates an IPv6 layer.  PayloadLength is always patched in post-fix.</summary>
    /// <param name="srcAddr">Source address.</param>
    /// <param name="dstAddr">Destination address.</param>
    /// <param name="hopLimit">Hop limit; default 64.</param>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto{T}.Compute"/> (default) means auto-patch from inner layer.
    /// </param>
    /// <param name="mtu">
    /// IP-layer MTU in bytes (IPv6 header + payload, not including Ethernet).
    /// Zero (default) means no fragmentation limit.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6Layer(IPv6Address srcAddr, IPv6Address dstAddr, byte hopLimit = 64, Auto<byte> nextHeader = default, ushort mtu = 0)
    {
        _SrcAddr = srcAddr;
        _DstAddr = dstAddr;
        _HopLimit = hopLimit;
        _Mtu = mtu;
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IPv6Header.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => EtherTypes.IPv6;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        IPv6Header hdr = IPv6Header.Create(_SrcAddr, _DstAddr, _ExplicitNextHeader, _HopLimit);
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
        switch (phase)
        {
            case FixPhase.Length:
                // PayloadLength = total - IPv6 header size.
                BinaryPrimitives.WriteUInt16BigEndian(
                    frame.Slice(myOffset + 4, 2),
                    (ushort)(myLength - IPv6Header.Size));
                break;

            case FixPhase.PublishPseudoHeader:
                PublishPseudoHeader(frame, myOffset, myLength, ref ctx);
                break;

            default:
                break;
        }
    }

    /// <summary>Publishes IPv6 addressing and transport extents to the shared <see cref="PostFixContext"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PublishPseudoHeader(scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        frame.Slice(myOffset + SrcAddrOffset, 16).CopyTo(ctx.PseudoSrcIp);
        frame.Slice(myOffset + DstAddrOffset, 16).CopyTo(ctx.PseudoDstIp);
        ctx.PseudoIpLength = 16;
        ctx.PseudoIsIPv6 = true;
        ctx.PseudoProtocol = frame[myOffset + NextHeaderOffset];
        ctx.TransportOffset = myOffset + IPv6Header.Size;
        ctx.TransportEnd = myOffset + myLength;
    }
}
