// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Generic ICMPv4 layer (8-byte header) for the <see cref="FrameStack"/> API.
/// Supports any ICMPv4 message type by accepting an arbitrary 4-byte data field
/// (bytes 4-7 of the ICMP header, whose semantics are type-specific).
/// </summary>
/// <remarks>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IStatelessLayer"/> — deterministic output from constructor parameters.</item>
///   <item><see cref="IProvidesProtocolType"/> — value 1 (ICMP) so an outer
///   <see cref="IPv4Layer"/> auto-patches its Protocol field.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — ICMPv4 checksums cover only the
///   ICMP message itself, not an IP pseudo-header.</item>
/// </list>
/// <para><b>Wire format (RFC 792):</b></para>
/// <code>
/// Byte  0:    Type
/// Byte  1:    Code
/// Bytes 2-3:  Checksum (one's complement over type+code+checksum+data4+payload)
/// Bytes 4-7:  Type-specific data (e.g. unused zeros, gateway IP, pointer byte)
/// Bytes 8+:   Payload (typically original IP header + first 8 bytes of datagram)
/// </code>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct IcmpV4Layer : IStatelessLayer, IPseudoHeaderIndependent, IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>
{
    /// <summary>Offset of the Checksum field within the ICMPv4 header.</summary>
    private const int ChecksumOffset = 2;

    private readonly byte _Type;
    private readonly byte _Code;
    private readonly uint _Data4; // bytes 4-7: type-specific

    /// <summary>Explicit checksum value when the caller pinned one.</summary>
    private readonly ushort _ExplicitChecksum;

    /// <summary><c>true</c> when the caller supplied a verbatim checksum.</summary>
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates a generic ICMPv4 layer.</summary>
    /// <param name="type">ICMP message type.</param>
    /// <param name="code">ICMP code for the given type.</param>
    /// <param name="data4">
    /// Type-specific 4-byte field (bytes 4–7 of the ICMP header).
    /// Stored in big-endian order on the wire.
    /// Use <c>0</c> for types where this field is unused (e.g. Destination Unreachable,
    /// Time Exceeded). For Redirect (type 5) pass the gateway IPv4 address as a
    /// big-endian uint. For Echo Request/Reply prefer <see cref="IcmpV4EchoLayer"/>.
    /// </param>
    /// <param name="checksum">
    /// Checksum field; <see cref="Auto{T}.Compute"/> (default) means auto-compute.
    /// Use <see cref="Auto{T}.Explicit"/> to pin a specific value (e.g. 0x0000 to
    /// force an invalid checksum in negative tests).
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IcmpV4Layer(byte type, byte code, uint data4 = 0, Auto<ushort> checksum = default)
    {
        _Type = type;
        _Code = code;
        _Data4 = data4;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 8;
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
        dst[0] = _Type;
        dst[1] = _Code;
        dst[2] = 0; // checksum high byte — patched in ApplyPostFix
        dst[3] = 0; // checksum low byte
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..8], _Data4);
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

        // Zero the checksum field before computing one's complement over the full ICMP message.
        frame[myOffset + ChecksumOffset] = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;
        ushort checksum = ChecksumUtils.OnesComplement(frame.Slice(myOffset, myLength));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
