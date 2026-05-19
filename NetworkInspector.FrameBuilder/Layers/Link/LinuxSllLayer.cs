// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Linux Cooked Capture v1 (SLL) link-layer header for the <see cref="FrameStack"/> API.
/// Uses LINKTYPE_LINUX_SLL (DLT 113) as the capture link type.
/// </summary>
/// <remarks>
/// <para>Header wire format (16 bytes):</para>
/// <code>
/// Bytes  0- 1: Packet type (pkttype, 0=unicast-to-us, 4=outgoing, ...)
/// Bytes  2- 3: Link-layer address type (hatype, 1=Ethernet)
/// Bytes  4- 5: Link-layer address length (halen, 6 for Ethernet)
/// Bytes  6-13: Link-layer address (8 bytes, zero-padded on the right)
/// Bytes 14-15: EtherType / protocol
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
public readonly struct LinuxSllLayer : IStatelessLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent, IConsumesNextProtocolValue<EtherTypeKind>
{
    /// <summary>EtherType field offset within the SLL header.</summary>
    private const int EtherTypeOffset = 14;

    private readonly ushort _PktType;
    private readonly ushort _HaType;
    private readonly ushort _HaLen;

    // 8-byte address, stored inline.
    private readonly byte _A0;
    private readonly byte _A1;
    private readonly byte _A2;
    private readonly byte _A3;
    private readonly byte _A4;
    private readonly byte _A5;
    private readonly byte _A6;
    private readonly byte _A7;

    private readonly ushort _ExplicitEtherType;
    private readonly bool _EtherTypeIsExplicit;

    /// <summary>Creates a Linux Cooked Capture v1 (SLL) layer.</summary>
    /// <param name="srcAddress">
    /// Source link-layer address (up to 8 bytes; zero-padded right).
    /// Use a 6-byte MAC for Ethernet.
    /// </param>
    /// <param name="haType">Link-layer address type (1 = Ethernet).</param>
    /// <param name="pktType">
    /// Packet type:
    /// 0 = Unicast to us; 1 = Broadcast; 2 = Multicast;
    /// 3 = Unicast to another host; 4 = Sent by us.
    /// </param>
    /// <param name="etherType">EtherType; default is auto-fill from inner layer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LinuxSllLayer(
        ReadOnlySpan<byte> srcAddress = default,
        ushort haType = 1,
        ushort pktType = 0,
        Auto<ushort> etherType = default)
    {
        _PktType = pktType;
        _HaType = haType;
        int len = srcAddress.Length > 8 ? 8 : srcAddress.Length;
        _HaLen = (ushort)len;
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
        get => 16;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], _PktType);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..4], _HaType);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..6], _HaLen);
        dst[6] = _A0;
        dst[7] = _A1;
        dst[8] = _A2;
        dst[9] = _A3;
        dst[10] = _A4;
        dst[11] = _A5;
        dst[12] = _A6;
        dst[13] = _A7;
        BinaryPrimitives.WriteUInt16BigEndian(dst[14..16], _ExplicitEtherType);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
    {
        if (!_EtherTypeIsExplicit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + EtherTypeOffset, 2), next);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed.
    }
}
