// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv6 Router Advertisement layer (type 134) for the <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 4861 §4.2 Router Advertisement wire format:</para>
/// <code>
/// Bytes 0-3:  ICMPv6 header (type=134, code=0, checksum)
/// Byte  4:    Cur Hop Limit
/// Byte  5:    M-bit[7] | O-bit[6] | H-bit[5] | Prf[4:3] | Reserved[2:0]
/// Bytes 6-7:  Router Lifetime (seconds)
/// Bytes 8-11: Reachable Time (milliseconds)
/// Bytes 12-15:Retrans Timer (milliseconds)
/// Bytes 16+:  Options (supplied via EmitFrame payload)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV6RouterAdvertisementLayer :
    IStatelessLayer, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader
{
    /// <summary>Header size in bytes.</summary>
    public const int HeaderBytes = 16;

    private readonly byte _CurHopLimit;
    private readonly byte _Flags;
    private readonly ushort _RouterLifetime;
    private readonly uint _ReachableTime;
    private readonly uint _RetransTimer;
    private readonly ushort _ExplicitChecksum;
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates an ICMPv6 Router Advertisement layer.</summary>
    /// <param name="curHopLimit">Current hop limit recommended to hosts (default 64).</param>
    /// <param name="managed">M-bit: hosts use stateful address configuration.</param>
    /// <param name="other">O-bit: hosts use stateful configuration for other info.</param>
    /// <param name="routerLifetimeSec">Router lifetime in seconds (0 = not default router).</param>
    /// <param name="reachableTimeMs">Reachable time in milliseconds (0 = unspecified).</param>
    /// <param name="retransTimerMs">Retransmission timer in milliseconds (0 = unspecified).</param>
    /// <param name="checksum">Checksum; default is auto-compute.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV6RouterAdvertisementLayer(
        byte curHopLimit = 64,
        bool managed = false,
        bool other = false,
        ushort routerLifetimeSec = 1800,
        uint reachableTimeMs = 0,
        uint retransTimerMs = 0,
        Auto<ushort> checksum = default)
    {
        _CurHopLimit = curHopLimit;
        byte flags = 0;
        if (managed)
        {
            flags |= 0x80;
        }
        if (other)
        {
            flags |= 0x40;
        }
        _Flags = flags;
        _RouterLifetime = routerLifetimeSec;
        _ReachableTime = reachableTimeMs;
        _RetransTimer = retransTimerMs;
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
        dst[0] = 134; // type: Router Advertisement
        dst[1] = 0;   // code
        dst[2] = 0;   // checksum (patched later)
        dst[3] = 0;
        dst[4] = _CurHopLimit;
        dst[5] = _Flags;
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..8], _RouterLifetime);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..12], _ReachableTime);
        BinaryPrimitives.WriteUInt32BigEndian(dst[12..16], _RetransTimer);
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
