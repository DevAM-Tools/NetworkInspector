// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv6 Router Solicitation layer (type 133) for the <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 4861 §4.1 Router Solicitation wire format:</para>
/// <code>
/// Bytes 0-3:  ICMPv6 header (type=133, code=0, checksum)
/// Bytes 4-7:  Reserved (must be zero)
/// Bytes 8+:   Options (supplied via EmitFrame payload)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV6RouterSolicitationLayer :
    IStatelessLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    private const int _ChecksumOffset = 2;

    /// <summary>Header size in bytes (4-byte ICMPv6 common + 4-byte reserved).</summary>
    public const int HeaderBytes = 8;

    /// <summary>Explicit checksum value when the caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when the caller supplied a verbatim checksum.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates an ICMPv6 Router Solicitation layer.</summary>
    /// <param name="checksum">Checksum; default is auto-compute.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV6RouterSolicitationLayer(Auto<ushort> checksum = default)
    {
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize => HeaderBytes;

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
        dst[0] = 133; // type: Router Solicitation
        dst[1] = 0;   // code
        dst[2] = 0;   // checksum (patched later)
        dst[3] = 0;
        dst[4] = 0;   // reserved
        dst[5] = 0;
        dst[6] = 0;
        dst[7] = 0;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.InnerChecksum)
        {
            return;
        }
        ApplyIcmpV6Checksum(frame, myOffset, myLength, ref ctx, _ChecksumIsExplicit, _ExplicitChecksum);
    }

    /// <summary>Shared ICMPv6 checksum computation helper.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyIcmpV6Checksum(
        scoped Span<byte> frame,
        int myOffset,
        int myLength,
        scoped ref PostFixContext ctx,
        bool isExplicit,
        ushort explicitValue)
    {
        if (isExplicit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _ChecksumOffset, 2), explicitValue);
            return;
        }
        frame[myOffset + _ChecksumOffset] = 0;
        frame[myOffset + _ChecksumOffset + 1] = 0;
        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp = ctx.PseudoDstIp[..ctx.PseudoIpLength];
        ushort checksum = ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.IcmpV6, segment);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _ChecksumOffset, 2), checksum);
    }
}
