// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Generic ICMPv6 layer (4-byte common header) for the <see cref="FrameStack"/> API.
/// Supports any ICMPv6 message type by accepting arbitrary type-specific body bytes
/// via <c>EmitFrame(typeSpecificBody)</c>.
/// </summary>
/// <remarks>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IStatelessLayer"/> — deterministic from constructor parameters.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 58 so an outer
///   <see cref="IPv6Layer"/> auto-patches its NextHeader field.</item>
///   <item><see cref="IRequiresPseudoHeader"/> — ICMPv6 requires the IPv6 pseudo-header
///   for checksum computation per RFC 4443 §2.3.</item>
/// </list>
/// <para><b>Wire format (RFC 4443):</b></para>
/// <code>
/// Byte  0:    Type
/// Byte  1:    Code
/// Bytes 2-3:  Checksum (computed over IPv6 pseudo-header + ICMPv6 message)
/// Bytes 4+:   Type-specific body (via EmitFrame payload)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV6Layer : IStatelessLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    /// <summary>Offset of the Checksum field within the ICMPv6 header.</summary>
    private const int ChecksumOffset = 2;

    private readonly byte _Type;
    private readonly byte _Code;

    /// <summary>Explicit checksum value when the caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when the caller supplied a verbatim checksum.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates a generic ICMPv6 layer.</summary>
    /// <param name="type">ICMPv6 message type.</param>
    /// <param name="code">ICMPv6 code for the given type.</param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto{T}.Compute"/> (default) means auto-compute over
    /// the IPv6 pseudo-header + ICMPv6 message. Use <see cref="Auto{T}.Explicit"/> to pin.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV6Layer(byte type, byte code = 0, Auto<ushort> checksum = default)
    {
        _Type = type;
        _Code = code;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 4; // type(1) + code(1) + checksum(2)
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
        dst[0] = _Type;
        dst[1] = _Code;
        dst[2] = 0; // checksum high byte — patched in ApplyPostFix
        dst[3] = 0; // checksum low byte
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

        // Zero the checksum field, then compute over the IPv6 pseudo-header + ICMPv6 message.
        frame[myOffset + ChecksumOffset] = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;
        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp = ctx.PseudoDstIp[..ctx.PseudoIpLength];
        ushort checksum = ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.IcmpV6, segment);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
