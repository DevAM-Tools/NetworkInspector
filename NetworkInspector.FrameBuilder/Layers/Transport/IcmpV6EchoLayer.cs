// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv6 echo request/reply layer (8-byte header) for the new <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/>.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 58 (ICMPv6) so the IPv6 layer
///   auto-patches its NextHeader field.</item>
///   <item><see cref="IProvidesNextProtocolValue"/>.</item>
///   <item><see cref="IRequiresPseudoHeader"/> — ICMPv6 requires the IPv6 pseudo-header
///   for checksum computation per RFC 4443 §2.3.</item>
/// </list>
/// </remarks>
public readonly struct IcmpV6EchoLayer : IStatelessLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    /// <summary>ICMPv6 type: Echo Request.</summary>
    public const byte TypeEchoRequest = 128;

    /// <summary>ICMPv6 type: Echo Reply.</summary>
    public const byte TypeEchoReply = 129;

    /// <summary>Offset of the Checksum field within the ICMPv6 header.</summary>
    private const int ChecksumOffset = 2;

    private readonly byte _Type;
    private readonly byte _Code;
    private readonly ushort _Identifier;
    private readonly ushort _SequenceNumber;

    /// <summary>Explicit checksum value when caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when caller supplied a checksum verbatim.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates an ICMPv6 echo layer.</summary>
    /// <param name="type">ICMP type (<see cref="TypeEchoRequest"/> or <see cref="TypeEchoReply"/>).</param>
    /// <param name="identifier">Echo identifier.</param>
    /// <param name="sequenceNumber">Echo sequence number.</param>
    /// <param name="code">ICMP code; default 0.</param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto{T}.Compute"/> (default) means auto-compute over
    /// the IPv6 pseudo-header + ICMP message.
    /// </param>
    /// <param name="isReply">
    /// Convenience flag: when <c>true</c> sets type to <see cref="TypeEchoReply"/>,
    /// overriding <paramref name="type"/>.  When <c>false</c> (default) the
    /// <paramref name="type"/> parameter is used unchanged.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV6EchoLayer(
        byte type = TypeEchoRequest,
        ushort identifier = 1,
        ushort sequenceNumber = 1,
        byte code = 0,
        Auto<ushort> checksum = default,
        bool isReply = false)
    {
        _Type = isReply ? TypeEchoReply : type;
        _Code = code;
        _Identifier = identifier;
        _SequenceNumber = sequenceNumber;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IcmpV6Header.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IcmpV6;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        IcmpV6Header hdr = new()
        {
            Type = _Type,
            Code = _Code,
            Checksum = (ushort)0,
            Identifier = _Identifier,
            SequenceNumber = _SequenceNumber,
        };
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

        // Zero the checksum field, then compute over IPv6 pseudo-header + ICMPv6 message.
        frame[myOffset + ChecksumOffset] = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;

        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp = ctx.PseudoDstIp[..ctx.PseudoIpLength];

        ushort checksum = ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.IcmpV6, segment);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
