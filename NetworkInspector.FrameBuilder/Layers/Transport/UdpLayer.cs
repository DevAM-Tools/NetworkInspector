// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// UDP transport-layer header (8 bytes) for the new
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/> — transport layer.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 17 so an outer
///   IP layer can patch its Protocol field.</item>
///   <item><see cref="IRequiresPseudoHeader"/> — needs the IP layer to publish
///   its pseudo-header for checksum computation.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches the UDP Length field.</item>
///   <item><see cref="FixPhase.InnerChecksum"/> — computes the UDP checksum
///   using the IPv4 / IPv6 pseudo-header from <see cref="PostFixContext"/>
///   when <c>computeChecksum</c> is enabled.</item>
/// </list>
/// <para>
/// RFC 8200 §8.1 forbids a pinned zero UDP checksum over IPv6. Because post-fix
/// phases must not throw, that violation is surfaced through
/// <see cref="BuildStatus.InvalidLayerState"/> on the build status rather than
/// by raising an exception.
/// </para>
/// </remarks>
public readonly struct UdpLayer : IStatelessLayer, IInteriorLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    /// <summary>Offset of the Length field within the UDP header.</summary>
    private const int LengthOffset = 4;

    /// <summary>Offset of the Checksum field within the UDP header.</summary>
    private const int ChecksumOffset = 6;

    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;

    /// <summary>Explicit checksum value when caller pinned one; ignored when <see cref="_ChecksumIsExplicit"/> is false.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when caller supplied a checksum verbatim; <c>false</c> means auto-compute.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates a UDP layer.</summary>
    /// <param name="srcPort">Source port.</param>
    /// <param name="dstPort">Destination port.</param>
    /// <param name="checksum">
    /// UDP checksum field.
    /// <para><see cref="Auto{T}.Compute"/> (default) — compute over the IP pseudo-header
    /// + UDP segment; an all-zero result is encoded as <c>0xFFFF</c> per RFC 768.</para>
    /// <para><see cref="Auto{T}.Explicit"/> with value <c>0</c> — emit "no checksum"
    /// (IPv4 only; over IPv6 this is a protocol violation).</para>
    /// <para><see cref="Auto{T}.Explicit"/> with non-zero value — use the supplied
    /// value verbatim (corruption / conformance tests).</para>
    /// </param>
    /// <param name="computeChecksum">
    /// Convenience flag: when <c>false</c> disables checksum computation (emits zero).
    /// Takes effect only when <paramref name="checksum"/> is <see cref="Auto{T}.Compute"/>.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UdpLayer(ushort srcPort, ushort dstPort, Auto<ushort> checksum = default, bool computeChecksum = true)
    {
        _SrcPort = srcPort;
        _DstPort = dstPort;
        // If computeChecksum=false and no explicit checksum, emit zero (no-checksum)
        if (!computeChecksum && !checksum.TryGetExplicit(out _))
        {
            _ChecksumIsExplicit = true;
            _ExplicitChecksum = 0;
        }
        else
        {
            _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
            _ExplicitChecksum = v;
        }
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => UdpHeader.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.Udp;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        UdpHeader hdr = UdpHeader.Create(_SrcPort, _DstPort);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        switch (phase)
        {
            case FixPhase.Length:
                // UDP Length covers the UDP header plus everything inside it.
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + LengthOffset, 2), (ushort)myLength);
                break;

            case FixPhase.InnerChecksum:
                if (_ChecksumIsExplicit)
                {
                    // RFC 8200 §8.1 forbids a zero UDP checksum over IPv6
                    // (unless the upper-layer protocol explicitly opts in via
                    // a separate spec — we do not here). A pinned 0x0000 over
                    // an IPv6 pseudo-header would silently produce a
                    // non-conformant packet. Post-fix phases must never throw
                    // (an escaping exception would corrupt the FrameSequence
                    // iterator state), so we surface the violation through the
                    // build status and leave the frame bytes untouched.
                    if (_ExplicitChecksum == 0 && ctx.PseudoIsIPv6)
                    {
                        ctx.Status = BuildStatus.InvalidLayerState;
                        break;
                    }
                    // Caller pinned the checksum; write verbatim.
                    BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), _ExplicitChecksum);
                }
                else
                {
                    ComputeChecksum(frame, myOffset, myLength, in ctx);
                }
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Computes the UDP checksum over the IPv4 / IPv6 pseudo-header plus the
    /// UDP segment.  Requires the network layer to have published its
    /// pseudo-header in <see cref="FixPhase.PublishPseudoHeader"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeChecksum(Span<byte> frame, int myOffset, int myLength, in PostFixContext ctx)
    {
        // Zero the checksum field before computing.
        frame[myOffset + ChecksumOffset] = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;

        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp = ctx.PseudoDstIp[..ctx.PseudoIpLength];

        ushort checksum = ctx.PseudoIsIPv6
            ? ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.Udp, segment)
            : ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, IpProtocols.Udp, segment);

        // RFC 768: an all-zero computed checksum is transmitted as 0xFFFF
        // (because zero already means "no checksum").
        if (checksum == 0)
        {
            checksum = 0xFFFF;
        }

        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
