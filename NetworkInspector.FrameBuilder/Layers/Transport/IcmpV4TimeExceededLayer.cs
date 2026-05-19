// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ICMPv4 Time Exceeded layer (type 11, 8-byte header) for the
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>RFC 792 Time Exceeded (type 11) wire format:</para>
/// <code>
/// Byte  0:    Type = 11
/// Byte  1:    Code (0=TTL exceeded in transit, 1=Fragment reassembly time exceeded)
/// Bytes 2-3:  Checksum
/// Bytes 4-7:  Unused (must be zero)
/// Bytes 8+:   Original IP header + first 8 bytes of original datagram (via EmitFrame)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV4TimeExceededLayer :
    IStatelessLayer, IPseudoHeaderIndependent, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>
{
    /// <summary>Time-to-Live exceeded in transit (code 0).</summary>
    public const byte CodeTtlExceeded = 0;

    /// <summary>Fragment reassembly time exceeded (code 1).</summary>
    public const byte CodeReassemblyTimeout = 1;

    private readonly IcmpV4Layer _Inner;

    /// <summary>
    /// Creates an ICMPv4 Time Exceeded layer with <see cref="CodeTtlExceeded"/> code and auto checksum.
    /// Required because C# does not invoke a constructor with only optional parameters when
    /// <c>new IcmpV4TimeExceededLayer()</c> is used — the struct would otherwise be zero-initialized.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4TimeExceededLayer()
    {
        _Inner = new IcmpV4Layer(11, CodeTtlExceeded, 0, default);
    }

    /// <summary>Creates an ICMPv4 Time Exceeded layer.</summary>
    /// <param name="code"><see cref="CodeTtlExceeded"/> or <see cref="CodeReassemblyTimeout"/>.</param>
    /// <param name="checksum">Checksum; default is auto-compute.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4TimeExceededLayer(byte code = CodeTtlExceeded, Auto<ushort> checksum = default)
    {
        _Inner = new IcmpV4Layer(11, code, 0, checksum);
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
