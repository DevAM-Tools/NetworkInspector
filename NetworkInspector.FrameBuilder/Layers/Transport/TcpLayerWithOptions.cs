// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// TCP transport-layer header with options (variable size 20–60 bytes) for the
/// new <see cref="FrameStack"/> API.  Caller supplies pre-encoded option bytes
/// padded to a 4-byte boundary; the layer sets the DataOffset field accordingly.
/// </summary>
/// <remarks>
/// Capabilities and post-fix mirror <see cref="TcpLayer"/>.  The checksum span
/// covers the IP pseudo-header plus the variable-length TCP segment.
/// </remarks>
public readonly struct TcpLayerWithOptions :
    IStatelessLayer, IInteriorLayer, IProvidesProtocolType,
    IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader, IStreamCarrier
{
    /// <summary>Offset of the DataOffsetFlags field within the TCP header.</summary>
    private const int DataOffsetFlagsOffset = 12;

    /// <summary>Offset of the Checksum field within the TCP header.</summary>
    private const int ChecksumOffset = 16;

    /// <summary>Maximum total TCP header size (data offset 15 × 4).</summary>
    private const int MaxHeaderSize = 60;

    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;
    private readonly uint _SeqNum;
    private readonly uint _AckNum;
    private readonly byte _Flags;
    private readonly ushort _WindowSize;
    private readonly ushort _UrgentPointer;

    /// <summary>Caller-supplied option bytes (raw, padded with 0x00 EOOL — End-Of-Options-List).</summary>
    private readonly ReadOnlyMemory<byte> _Options;

    /// <summary>Padded option length in bytes (multiple of 4).</summary>
    private readonly byte _PaddedOptionsLength;

    /// <summary>Explicit checksum value when caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when caller supplied a checksum verbatim.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates a TCP layer with options.</summary>
    /// <param name="srcPort">Source port.</param>
    /// <param name="dstPort">Destination port.</param>
    /// <param name="opts">Pre-encoded TCP option bytes; padded to a 4-byte boundary.</param>
    /// <param name="seqNum">Absolute sequence number.</param>
    /// <param name="ackNum">Acknowledgement number (only meaningful when ACK flag set).</param>
    /// <param name="flags">TCP control flags (see <see cref="TcpFlags"/>).</param>
    /// <param name="windowSize">Window size; default 65535.</param>
    /// <param name="urgentPointer">Urgent pointer; default 0.</param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto{T}.Compute"/> (default) means auto-compute.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when options exceed 40 bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TcpLayerWithOptions(
        ushort srcPort,
        ushort dstPort,
        TcpOptions opts,
        uint seqNum = 0,
        uint ackNum = 0,
        byte flags = TcpFlags.Syn,
        ushort windowSize = 65535,
        ushort urgentPointer = 0,
        Auto<ushort> checksum = default)
    {
        int padded = (opts.Data.Length + 3) & ~3;
        if (padded > MaxHeaderSize - TcpHeader.Size)
        {
            throw new ArgumentException(
                $"TCP options exceed the maximum length of {MaxHeaderSize - TcpHeader.Size} bytes.",
                nameof(opts));
        }

        _SrcPort = srcPort;
        _DstPort = dstPort;
        _Options = opts.Data;
        _PaddedOptionsLength = (byte)padded;
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
        get => TcpHeader.Size + _PaddedOptionsLength;
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
        int totalSize = TcpHeader.Size + _PaddedOptionsLength;
        int dataOffsetWords = totalSize / 4;

        TcpHeader hdr = TcpHeader.Create(
            _SrcPort, _DstPort, _SeqNum, _AckNum,
            _Flags, _WindowSize, dataOffsetWords, _UrgentPointer);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);

        // Copy raw option bytes, then zero-pad up to the padded length (NOP = 0x01,
        // but the canonical "padding" octet for the unused tail is the End-Of-Options
        // marker (0x00).  Zero-fill matches both prevailing implementations and
        // RFC 9293 §3.1 which allows padding to be implementation-defined; using
        // EOOL is the safest choice.
        if (_Options.Length > 0)
        {
            _Options.Span.CopyTo(dst[TcpHeader.Size..]);
        }
        if (_PaddedOptionsLength > _Options.Length)
        {
            dst.Slice(TcpHeader.Size + _Options.Length, _PaddedOptionsLength - _Options.Length).Clear();
        }
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
