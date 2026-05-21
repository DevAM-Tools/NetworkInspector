// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv4 Redirect layer (type 5, 8-byte header) for the
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 792 Redirect (type 5) wire format:</para>
/// <code>
/// Byte  0:    Type = 5
/// Byte  1:    Code (0=Redirect for network, 1=Redirect for host,
///                   2=Redirect for TOS and network, 3=Redirect for TOS and host)
/// Bytes 2-3:  Checksum
/// Bytes 4-7:  Gateway Internet Address (IPv4)
/// Bytes 8+:   Original IP header + first 8 bytes of original datagram (via EmitFrame)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV4RedirectLayer : IStatelessLayer, IPseudoHeaderIndependent, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>
{
    /// <summary>Redirect for the network.</summary>
    public const byte CodeRedirectForNetwork = 0;

    /// <summary>Redirect for the host.</summary>
    public const byte CodeRedirectForHost = 1;

    /// <summary>Redirect for the TOS and network.</summary>
    public const byte CodeRedirectForTosNetwork = 2;

    /// <summary>Redirect for the TOS and host.</summary>
    public const byte CodeRedirectForTosHost = 3;

    private readonly IcmpV4Layer _Inner;

    /// <summary>Creates an ICMPv4 Redirect layer.</summary>
    /// <param name="gatewayAddress">
    /// IPv4 address of the router to use (bytes 4–7 of the ICMP header),
    /// supplied as a big-endian <see cref="uint"/> (same representation as
    /// <see cref="IPv4Address.RawValue"/>).
    /// </param>
    /// <param name="code">One of the <c>Code*</c> constants; default is <see cref="CodeRedirectForHost"/>.</param>
    /// <param name="checksum">Checksum; default is auto-compute.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4RedirectLayer(IPv4Address gatewayAddress, byte code = CodeRedirectForHost, Auto<ushort> checksum = default)
    {
        _Inner = new IcmpV4Layer(5, code, gatewayAddress.RawValue, checksum);
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Inner.HeaderSize;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Inner.ProtocolType;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst) => _Inner.WriteHeader(dst);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
        => _Inner.ApplyPostFix(phase, frame, myOffset, myLength, ref ctx);
}
