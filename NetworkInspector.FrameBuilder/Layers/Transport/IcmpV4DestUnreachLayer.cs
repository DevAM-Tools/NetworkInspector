// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv4 Destination Unreachable layer (type 3, 8-byte header) for the
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 792 Destination Unreachable (type 3) wire format:</para>
/// <code>
/// Byte  0:    Type = 3
/// Byte  1:    Code (0=Net unreachable, 1=Host unreachable, 3=Port unreachable, …)
/// Bytes 2-3:  Checksum
/// Bytes 4-5:  Unused (must be zero) — or Next-Hop MTU for code 4 (fragmentation needed)
/// Bytes 6-7:  Unused (must be zero)
/// Bytes 8+:   Original IP header + first 8 bytes of original datagram (supplied via EmitFrame)
/// </code>
/// <para><b>Capabilities:</b> same as <see cref="IcmpV4Layer"/>.</para>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV4DestUnreachLayer : IStatelessLayer, IPseudoHeaderIndependent, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>
{
    /// <summary>Net unreachable.</summary>
    public const byte CodeNetUnreachable = 0;
    /// <summary>Host unreachable.</summary>
    public const byte CodeHostUnreachable = 1;
    /// <summary>Protocol unreachable.</summary>
    public const byte CodeProtocolUnreachable = 2;
    /// <summary>Port unreachable.</summary>
    public const byte CodePortUnreachable = 3;
    /// <summary>Fragmentation needed and DF set.</summary>
    public const byte CodeFragmentationNeeded = 4;
    /// <summary>Source route failed.</summary>
    public const byte CodeSourceRouteFailed = 5;

    private readonly IcmpV4Layer _Inner;

    /// <summary>
    /// Creates an ICMPv4 Destination Unreachable layer with <see cref="CodePortUnreachable"/> code
    /// and auto checksum.
    /// Required because C# does not invoke a constructor with only optional parameters when
    /// <c>new IcmpV4DestUnreachLayer()</c> is used — the struct would otherwise be zero-initialized.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4DestUnreachLayer()
    {
        _Inner = new IcmpV4Layer(3, CodePortUnreachable, 0, default);
    }

    /// <summary>Creates an ICMPv4 Destination Unreachable layer.</summary>
    /// <param name="code">One of the <c>Code*</c> constants.</param>
    /// <param name="nextHopMtu">
    /// Next-hop MTU in bytes (code 4 / fragmentation needed only); stored in bytes 6–7.
    /// Default <c>0</c> for all other codes.
    /// </param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto{T}.Compute"/> (default) means auto-compute.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4DestUnreachLayer(byte code = CodePortUnreachable, ushort nextHopMtu = 0, Auto<ushort> checksum = default)
    {
        // Bytes 4-7: unused(2) | next-hop-MTU(2) for code 4; all zeros otherwise.
        uint data4 = (uint)nextHopMtu;  // stored in the low 16 bits → big-endian bytes 6-7
        _Inner = new IcmpV4Layer(3, code, data4, checksum);
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
