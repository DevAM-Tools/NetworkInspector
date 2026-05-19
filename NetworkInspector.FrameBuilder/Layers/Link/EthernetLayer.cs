// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Ethernet II link-layer header (14 bytes) for the new
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — root link layer.</item>
///   <item><see cref="IConsumesNextProtocolValue"/> — patches its EtherType
///   from the next-inner layer's <see cref="IProvidesProtocolType.ProtocolType"/>.</item>
///   <item><see cref="IProvidesMtu"/> — exposes the configured link MTU
///   (default 1500 bytes) to fragmenting layers above.</item>
/// </list>
/// </remarks>
public readonly struct EthernetLayer :
    IStatelessLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent,
    IConsumesNextProtocolValue<EtherTypeKind>, IProvidesMtu
{
    /// <summary>Byte offset of the EtherType field within the Ethernet header.</summary>
    private const int EtherTypeOffset = 12;

    private readonly MacAddress _DstMac;
    private readonly MacAddress _SrcMac;

    /// <summary>Explicit EtherType when the user pinned one; meaningful only when <see cref="_EtherTypeIsExplicit"/> is <c>true</c>.</summary>
    private readonly ushort _ExplicitEtherType;

    /// <summary>
    /// <c>true</c> when the caller supplied an explicit EtherType via
    /// <see cref="Auto{T}.Explicit"/>; <c>false</c> means auto-patch
    /// from the inner layer's <see cref="IProvidesProtocolType.ProtocolType"/>.
    /// </summary>
    private readonly bool _EtherTypeIsExplicit;

    /// <summary>
    /// Maximum total on-the-wire frame size in bytes that the fragmenter is
    /// allowed to emit, <b>including</b> the 14-byte Ethernet header and any
    /// attached trailer (e.g. the 4-byte <c>EthernetFcs</c>). Default
    /// <c>1518</c> matches the standard 802.3 frame budget
    /// (1500 byte MAC client data + 14 byte header + 4 byte FCS).
    /// <para>
    /// Note: this is <b>not</b> the L3 MTU. The classic "MTU 1500" maps to a
    /// <see cref="_LinkMtu"/> of 1518 here. Naming kept as <c>_LinkMtu</c> to
    /// match the <see cref="IProvidesMtu"/> contract surfaced to fragmenters.
    /// </para>
    /// </summary>
    private readonly ushort _LinkMtu;

    /// <summary>Creates an Ethernet layer.</summary>
    /// <param name="dstMac">Destination MAC.</param>
    /// <param name="srcMac">Source MAC.</param>
    /// <param name="etherType">
    /// EtherType field; <see cref="Auto{T}.Compute"/> (default) means auto-patch
    /// from the inner network layer's <see cref="IProvidesProtocolType"/>.
    /// <see cref="Auto{T}.Explicit"/> pins the value verbatim, including
    /// <c>0x0000</c> (which suppresses auto-patching completely).
    /// </param>
    /// <param name="maxFrameSize">
    /// Maximum total Ethernet frame size in bytes — header + payload + trailer.
    /// Default <c>1518</c> (802.3 1500-MTU + 14 byte header + 4 byte FCS).
    /// Pass <c>1514</c> for "MTU 1500 without FCS in the budget".
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EthernetLayer(MacAddress dstMac, MacAddress srcMac, Auto<ushort> etherType = default, ushort maxFrameSize = 1518)
    {
        _DstMac = dstMac;
        _SrcMac = srcMac;
        _EtherTypeIsExplicit = etherType.TryGetExplicit(out ushort v);
        _ExplicitEtherType = v;
        _LinkMtu = maxFrameSize;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => EthernetHeader.Size;
    }

    /// <inheritdoc />
    public ushort LinkMtu
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _LinkMtu;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        // Use the [BinaryWritable]-generated TryWrite for zero-allocation serialization.
        EthernetHeader hdr = new()
        {
            DstMac = _DstMac,
            SrcMac = _SrcMac,
            EtherType = _ExplicitEtherType, // 0 = will be patched by PatchNextProtocol
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
    {
        // Only auto-patch if the user did not pin EtherType explicitly.
        if (!_EtherTypeIsExplicit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + EtherTypeOffset, 2), next);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // Ethernet has no post-fix work in any phase.
    }
}
