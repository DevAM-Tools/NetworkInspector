// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv4 echo request/reply layer (8-byte header) for the new <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/>.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 1 (ICMP) so the IPv4 layer
///   auto-patches its Protocol field.</item>
///   <item><see cref="IProvidesNextProtocolValue"/> — outer IPv4 must patch us.</item>
/// </list>
/// <para>ICMPv4 has <em>no</em> pseudo-header.  Its checksum covers just the
/// ICMP message (header + payload).</para>
/// </remarks>
public readonly struct IcmpV4EchoLayer : IStatelessLayer, IPseudoHeaderIndependent, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>
{
    /// <summary>ICMPv4 type: Echo Request (8) and Echo Reply (0).</summary>
    public const byte TypeEchoRequest = 8;

    /// <summary>ICMPv4 type: Echo Reply.</summary>
    public const byte TypeEchoReply = 0;

    /// <summary>Offset of the Checksum field within the ICMPv4 header.</summary>
    private const int _ChecksumOffset = 2;

    private readonly byte _Type;
    private readonly byte _Code;
    private readonly ushort _Identifier;
    private readonly ushort _SequenceNumber;

    /// <summary>Explicit checksum value when caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when caller supplied a checksum verbatim.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates an ICMPv4 echo layer.</summary>
    /// <param name="type">ICMP type (<see cref="TypeEchoRequest"/> or <see cref="TypeEchoReply"/>).</param>
    /// <param name="identifier">Echo identifier.</param>
    /// <param name="sequenceNumber">Echo sequence number.</param>
    /// <param name="code">ICMP code; default 0.</param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto.Compute"/> (default) means auto-compute over
    /// the ICMP message.  Use <see cref="Auto.Explicit"/> to pin.
    /// </param>
    /// <param name="isReply">
    /// Convenience flag: when <c>true</c> sets type to <see cref="TypeEchoReply"/>,
    /// overriding <paramref name="type"/>.  When <c>false</c> (default) the
    /// <paramref name="type"/> parameter is used unchanged.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4EchoLayer(
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
        get => IcmpV4Header.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.Icmp;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        IcmpV4Header hdr = new()
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
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _ChecksumOffset, 2), _ExplicitChecksum);
            return;
        }

        // Zero the checksum field, then compute over the entire ICMP message.
        frame[myOffset + _ChecksumOffset] = 0;
        frame[myOffset + _ChecksumOffset + 1] = 0;
        ushort checksum = ChecksumUtils.OnesComplement(frame.Slice(myOffset, myLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _ChecksumOffset, 2), checksum);
    }
}
