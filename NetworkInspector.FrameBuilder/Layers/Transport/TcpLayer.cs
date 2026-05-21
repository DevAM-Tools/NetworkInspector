// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// TCP transport-layer header (20 bytes, no options) for the new <see cref="FrameStack"/> API.
/// Fully stateless — the caller supplies the absolute sequence and acknowledgement numbers.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/>.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 6 (TCP) so an outer
///   IP layer auto-patches its Protocol/NextHeader field.</item>
///   <item><see cref="IRequiresPseudoHeader"/> — needs the network layer's
///   pseudo-header for checksum computation.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.InnerChecksum"/> — recomputes the TCP checksum over
///   the IP pseudo-header + TCP header + payload.</item>
/// </list>
/// </remarks>
public readonly struct TcpLayer :
    IStatelessLayer, IInteriorLayer, IProvidesProtocolType,
    IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader, IStreamCarrier
{
    /// <summary>Offset of the Checksum field within the TCP header.</summary>
    private const int ChecksumOffset = 16;

    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;
    private readonly uint _SeqNum;
    private readonly uint _AckNum;
    private readonly byte _Flags;
    private readonly ushort _WindowSize;
    private readonly ushort _UrgentPointer;

    /// <summary>Explicit checksum value when caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when caller supplied a checksum verbatim; <c>false</c> means auto-compute.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates a stateless TCP layer.  Sequence and ACK are caller-supplied.</summary>
    /// <param name="srcPort">Source port.</param>
    /// <param name="dstPort">Destination port.</param>
    /// <param name="seqNum">Absolute sequence number.</param>
    /// <param name="ackNum">Acknowledgement number (only meaningful when ACK flag set).</param>
    /// <param name="flags">TCP control flags (see <see cref="TcpFlags"/>).</param>
    /// <param name="windowSize">Window size; default 65535.</param>
    /// <param name="urgentPointer">Urgent pointer; default 0.</param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto{T}.Compute"/> (default) means auto-compute over
    /// the IP pseudo-header + TCP segment.  Use <see cref="Auto{T}.Explicit"/>
    /// to pin (corruption / conformance tests).
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TcpLayer(
        ushort srcPort,
        ushort dstPort,
        uint seqNum = 0,
        uint ackNum = 0,
        byte flags = TcpFlags.Syn,
        ushort windowSize = 65535,
        ushort urgentPointer = 0,
        Auto<ushort> checksum = default)
    {
        _SrcPort = srcPort;
        _DstPort = dstPort;
        _SeqNum = seqNum;
        _AckNum = ackNum;
        _Flags = flags;
        _WindowSize = windowSize;
        _UrgentPointer = urgentPointer;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
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
    public void WriteHeader(scoped Span<byte> dst)
    {
        TcpHeader hdr = TcpHeader.Create(
            _SrcPort, _DstPort, _SeqNum, _AckNum,
            _Flags, _WindowSize, dataOffsetWords: 5, _UrgentPointer);
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

        if (_ChecksumIsExplicit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), _ExplicitChecksum);
            return;
        }

        ComputeChecksum(frame, myOffset, myLength, in ctx);
    }

    /// <summary>Computes the TCP checksum over the IP pseudo-header plus the TCP segment.</summary>
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
            ? ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.Tcp, segment)
            : ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, IpProtocols.Tcp, segment);

        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
