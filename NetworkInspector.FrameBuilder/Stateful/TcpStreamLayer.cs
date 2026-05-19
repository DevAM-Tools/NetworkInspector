// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Internal stateful TCP layer used exclusively by
/// <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/>.  Distinct from
/// <see cref="TcpLayerWithAutoSequence"/>: this layer reads <em>every</em>
/// per-segment value (flags, window, ack, urgent) from dedicated
/// <see cref="SessionState"/> slots that the connection populates before
/// each NextPacket call.  Only the source/destination ports are baked
/// into the layer itself (constant per direction within a connection).
/// </summary>
/// <remarks>
/// <para>
/// State slots: <see cref="SessionState.TcpStreamNextSeq"/>,
/// <see cref="SessionState.TcpStreamAck"/>,
/// <see cref="SessionState.TcpStreamFlags"/>,
/// <see cref="SessionState.TcpStreamWindow"/>,
/// <see cref="SessionState.TcpStreamUrgent"/>,
/// <see cref="SessionState.HasTcpStream"/>.  These are intentionally
/// SEPARATE from the <c>TcpAck/TcpNextSeq</c> slots used by
/// <see cref="TcpLayerWithAutoSequence"/>: a stack must never mix both
/// stateful TCP layers.
/// </para>
/// <para>
/// SEQ self-management: <see cref="WriteHeader"/> snapshots
/// <see cref="SessionState.TcpStreamNextSeq"/> for the current frame and
/// then advances it by <c>CurrentPayloadLength</c> + 1 if SYN or FIN
/// is set in the flags byte.  Mutator-driven flag changes (e.g. caller
/// adds FIN to a data segment) therefore feed the bookkeeping
/// automatically.
/// </para>
/// <para>
/// Checksum: identical algorithm as <see cref="TcpLayerWithAutoSequence"/>
/// — pseudo-header over the active IPv4/IPv6 source/destination plus the
/// TCP segment, written into the standard offset-16 slot during
/// <see cref="FixPhase.InnerChecksum"/>.
/// </para>
/// <para>Thread safety: the struct is immutable; the per-frame state lives in <see cref="SessionState"/>.</para>
/// </remarks>
internal readonly struct TcpStreamLayer :
    IStatefulLayer, IInteriorLayer, IProvidesProtocolType,
    IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader, IStreamCarrier
{
    private const int ChecksumOffset = 16;

    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;
    private readonly uint _InitialSequence;
    private readonly uint _InitialAck;
    private readonly ushort _InitialWindow;

    /// <summary>Creates the per-direction TCP layer used by <see cref="TcpConnection{TOld,TTail}"/>.</summary>
    /// <param name="srcPort">Source port (the local endpoint of this direction).</param>
    /// <param name="dstPort">Destination port (the peer endpoint of this direction).</param>
    /// <param name="initialSequence">Initial Send Sequence number; written into the SYN of this direction.</param>
    /// <param name="initialAck">Initial acknowledgment number (typically 0; updated by the connection after the peer's SYN).</param>
    /// <param name="initialWindow">Initial advertised window size; mutator may override per segment.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TcpStreamLayer(
        ushort srcPort,
        ushort dstPort,
        uint initialSequence,
        uint initialAck,
        ushort initialWindow)
    {
        _SrcPort = srcPort;
        _DstPort = dstPort;
        _InitialSequence = initialSequence;
        _InitialAck = initialAck;
        _InitialWindow = initialWindow;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TcpHeader.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.Tcp;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeState(ref SessionState state)
    {
        state.TcpStreamNextSeq = _InitialSequence;
        state.TcpStreamAck = _InitialAck;
        state.TcpStreamFlags = TcpFlags.Ack;
        state.TcpStreamWindow = _InitialWindow;
        state.TcpStreamUrgent = 0;
        state.HasTcpStream = true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
    {
        // Snapshot of the per-frame values that TcpConnection wrote into
        // the slots BEFORE invoking NextPacket.  After this WriteHeader
        // returns, TcpConnection re-reads TcpStreamNextSeq to know the
        // sequence the just-emitted frame consumed (for ACK accounting on
        // the peer side).
        uint seq = state.TcpStreamNextSeq;
        uint ack = state.TcpStreamAck;
        byte flags = state.TcpStreamFlags;
        ushort window = state.TcpStreamWindow;
        ushort urgent = state.TcpStreamUrgent;

        // Advance SEQ for the NEXT frame: payload bytes + 1 for SYN/FIN.
        bool isSynOrFin = (flags & (TcpFlags.Syn | TcpFlags.Fin)) != 0;
        uint advance = (uint)state.CurrentPayloadLength + (isSynOrFin ? 1u : 0u);
        unchecked
        {
            state.TcpStreamNextSeq = seq + advance;
        }

        TcpHeader hdr = TcpHeader.Create(
            _SrcPort, _DstPort, seq, ack,
            flags, window, dataOffsetWords: 5, urgent);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.InnerChecksum)
        {
            return;
        }

        ComputeChecksum(frame, myOffset, myLength, in ctx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeChecksum(Span<byte> frame, int myOffset, int myLength, in PostFixContext ctx)
    {
        frame[myOffset + ChecksumOffset] = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;

        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp = ctx.PseudoDstIp[..ctx.PseudoIpLength];

        ushort checksum = ctx.PseudoIsIPv6
            ? ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.Tcp, segment)
            : ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, IpProtocols.Tcp, segment);

        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
