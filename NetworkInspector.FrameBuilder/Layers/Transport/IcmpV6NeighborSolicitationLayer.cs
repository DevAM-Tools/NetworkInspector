// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv6 Neighbor Solicitation layer (type 135) for the <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 4861 §4.3 Neighbor Solicitation wire format:</para>
/// <code>
/// Bytes 0-3:   ICMPv6 header (type=135, code=0, checksum)
/// Bytes 4-7:   Reserved (must be zero)
/// Bytes 8-23:  Target Address (IPv6)
/// Bytes 24+:   Options (supplied via EmitFrame payload)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV6NeighborSolicitationLayer :
    IStatelessLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    private const int _HeaderBytes = 24;

    private readonly IPv6Address _TargetAddress;
    private readonly ushort _ExplicitChecksum;
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates an ICMPv6 Neighbor Solicitation layer.</summary>
    /// <param name="targetAddress">IPv6 address being solicited.</param>
    /// <param name="checksum">Checksum; default is auto-compute.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV6NeighborSolicitationLayer(IPv6Address targetAddress, Auto<ushort> checksum = default)
    {
        _TargetAddress = targetAddress;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _HeaderBytes;
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
        dst[0] = 135; // type: Neighbor Solicitation
        dst[1] = 0;   // code
        dst[2] = 0;   // checksum (patched later)
        dst[3] = 0;
        dst[4] = 0;   // reserved
        dst[5] = 0;
        dst[6] = 0;
        dst[7] = 0;
        _TargetAddress.ToBytes(dst[8..24]);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.InnerChecksum)
        {
            return;
        }
        IcmpV6RouterSolicitationLayer.ApplyIcmpV6Checksum(frame, myOffset, myLength, ref ctx, _ChecksumIsExplicit, _ExplicitChecksum);
    }
}
