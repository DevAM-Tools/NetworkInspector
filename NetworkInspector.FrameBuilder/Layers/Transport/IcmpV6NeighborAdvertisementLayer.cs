// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv6 Neighbor Advertisement layer (type 136) for the <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 4861 §4.4 Neighbor Advertisement wire format:</para>
/// <code>
/// Bytes 0-3:   ICMPv6 header (type=136, code=0, checksum)
/// Bytes 4-7:   Flags: R[31] | S[30] | O[29] | Reserved[28:0]
/// Bytes 8-23:  Target Address (IPv6)
/// Bytes 24+:   Options (supplied via EmitFrame payload)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV6NeighborAdvertisementLayer :
    IStatelessLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    private const int HeaderBytes = 24;

    private readonly uint _Flags;
    private readonly IPv6Address _TargetAddress;
    private readonly ushort _ExplicitChecksum;
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates an ICMPv6 Neighbor Advertisement layer.</summary>
    /// <param name="targetAddress">IPv6 address being advertised.</param>
    /// <param name="router"><c>true</c> if the sender is a router (R-bit).</param>
    /// <param name="solicited"><c>true</c> if this is in response to a Neighbor Solicitation (S-bit).</param>
    /// <param name="overrideFlag"><c>true</c> if the link-layer address should override cached values (O-bit).</param>
    /// <param name="checksum">Checksum; default is auto-compute.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV6NeighborAdvertisementLayer(
        IPv6Address targetAddress,
        bool router = false,
        bool solicited = true,
        bool overrideFlag = true,
        Auto<ushort> checksum = default)
    {
        _TargetAddress = targetAddress;
        uint flags = 0;
        if (router)
        {
            flags |= 0x80000000u;
        }
        if (solicited)
        {
            flags |= 0x40000000u;
        }
        if (overrideFlag)
        {
            flags |= 0x20000000u;
        }
        _Flags = flags;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HeaderBytes;
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
        dst[0] = 136; // type: Neighbor Advertisement
        dst[1] = 0;   // code
        dst[2] = 0;   // checksum (patched later)
        dst[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..8], _Flags);
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
