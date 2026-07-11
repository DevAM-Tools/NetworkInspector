// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Linux Cooked Capture v2 (SLL2) link-layer header for the <see cref="FrameStack"/> API.
/// Uses LINKTYPE_LINUX_SLL2 (DLT 276) as the capture link type.
/// </summary>
/// <remarks>
/// <para>Header wire format (20 bytes):</para>
/// <code>
/// Bytes  0- 1: EtherType / protocol
/// Bytes  2- 3: Reserved (must be zero)
/// Bytes  4- 7: Interface index
/// Bytes  8- 9: Link-layer address type (hatype, 1=Ethernet)
/// Byte  10:    Packet type (pkttype)
/// Byte  11:    Link-layer address length (halen, 6 for Ethernet)
/// Bytes 12-19: Link-layer address (8 bytes, zero-padded on the right)
/// </code>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — root link-layer; no outer layer chains beneath it.</item>
///   <item><see cref="IInteriorLayer"/> — can carry inner protocol layers.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
///   <item><see cref="IConsumesNextProtocolValue{EtherTypeKind}"/> — auto-patches EtherType from the inner layer.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct LinuxSll2Layer : IStatelessLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent, IConsumesNextProtocolValue<EtherTypeKind>
{
    /// <summary>EtherType field offset within the SLL2 header.</summary>
    private const int _EtherTypeOffset = 0;

    private readonly uint _IfIndex;
    private readonly ushort _HaType;
    private readonly byte _PktType;

    // 8-byte address, stored inline.
    private readonly byte _A0;
    private readonly byte _A1;
    private readonly byte _A2;
    private readonly byte _A3;
    private readonly byte _A4;
    private readonly byte _A5;
    private readonly byte _A6;
    private readonly byte _A7;
    private readonly byte _HaLen;

    private readonly ushort _ExplicitEtherType;
    private readonly bool _EtherTypeIsExplicit;

    /// <summary>Creates a Linux Cooked Capture v2 (SLL2) layer.</summary>
    /// <param name="srcAddress">
    /// Source link-layer address (up to 8 bytes; zero-padded right).
    /// Use a 6-byte MAC for Ethernet.
    /// </param>
    /// <param name="ifIndex">Interface index on the capturing machine.</param>
    /// <param name="haType">Link-layer address type (1 = Ethernet).</param>
    /// <param name="pktType">
    /// Packet type:
    /// 0 = Unicast to us; 1 = Broadcast; 2 = Multicast;
    /// 3 = Unicast to another host; 4 = Sent by us.
    /// </param>
    /// <param name="etherType">EtherType; default is auto-fill from inner layer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LinuxSll2Layer(
        ReadOnlySpan<byte> srcAddress = default,
        uint ifIndex = 1,
        ushort haType = 1,
        byte pktType = 0,
        Auto<ushort> etherType = default)
    {
        _IfIndex = ifIndex;
        _HaType = haType;
        _PktType = pktType;
        int len = srcAddress.Length > 8 ? 8 : srcAddress.Length;
        _HaLen = (byte)len;
        _A0 = len > 0 ? srcAddress[0] : (byte)0;
        _A1 = len > 1 ? srcAddress[1] : (byte)0;
        _A2 = len > 2 ? srcAddress[2] : (byte)0;
        _A3 = len > 3 ? srcAddress[3] : (byte)0;
        _A4 = len > 4 ? srcAddress[4] : (byte)0;
        _A5 = len > 5 ? srcAddress[5] : (byte)0;
        _A6 = len > 6 ? srcAddress[6] : (byte)0;
        _A7 = len > 7 ? srcAddress[7] : (byte)0;
        _EtherTypeIsExplicit = etherType.TryGetExplicit(out ushort v);
        _ExplicitEtherType = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 20;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], _ExplicitEtherType);
        dst[2] = 0; // reserved
        dst[3] = 0; // reserved
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..8], _IfIndex);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..10], _HaType);
        dst[10] = _PktType;
        dst[11] = _HaLen;
        dst[12] = _A0;
        dst[13] = _A1;
        dst[14] = _A2;
        dst[15] = _A3;
        dst[16] = _A4;
        dst[17] = _A5;
        dst[18] = _A6;
        dst[19] = _A7;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol)
    {
        if (!_EtherTypeIsExplicit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _EtherTypeOffset, 2), nextProtocol);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed.
    }
}
