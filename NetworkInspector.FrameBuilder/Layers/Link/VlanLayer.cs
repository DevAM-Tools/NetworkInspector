// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IEEE 802.1Q VLAN tag (4 bytes) for the new <see cref="FrameStack"/> API.
/// Sits between Ethernet and the next-network-layer; for QinQ multiple
/// instances may be stacked.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — link layer (sits in the link region).</item>
///   <item><see cref="IProvidesProtocolType"/> — value 0x8100 (or 0x88A8 for QinQ),
///   so the outer Ethernet/VLAN layer auto-patches its EtherType to the TPID.</item>
///   <item><see cref="IProvidesNextProtocolValue"/> — the outer link layer must
///   patch us with our TPID.</item>
///   <item><see cref="IConsumesNextProtocolValue"/> — patches our own
///   <c>InnerEtherType</c> field from the next-inner layer's protocol type.</item>
/// </list>
/// <para>
/// MTU: VLAN is intentionally <em>not</em> <see cref="IProvidesMtu"/>; the outer
/// link layer's MTU is reduced by the VLAN tag's 4 bytes via the cons-list MTU
/// walk.  The VLAN tag does not own an MTU anchor of its own.
/// </para>
/// </remarks>
public readonly struct VlanLayer :
    IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent,
    IProvidesProtocolType, IProvidesNextProtocolValue<EtherTypeKind>, IConsumesNextProtocolValue<EtherTypeKind>
{
    /// <summary>Byte offset of the inner EtherType field within the VLAN tag.</summary>
    private const int InnerEtherTypeOffset = 2;

    private readonly ushort _VlanId;
    private readonly byte _Pcp;
    private readonly byte _Dei;
    private readonly bool _IsQinQ;

    /// <summary>Creates a standard 802.1Q VLAN tag (TPID 0x8100).</summary>
    /// <param name="vlanId">VLAN identifier (0–4095).</param>
    /// <param name="pcp">Priority Code Point (0–7). Default 0.</param>
    /// <param name="dei">Drop Eligible Indicator (0–1). Default 0.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VlanLayer(ushort vlanId, byte pcp = 0, byte dei = 0)
    {
        _VlanId = vlanId;
        _Pcp = pcp;
        _Dei = dei;
        _IsQinQ = false;
    }

    /// <summary>Creates a VLAN tag with explicit TPID selection (standard 0x8100 vs. QinQ 0x88A8).</summary>
    /// <param name="vlanId">VLAN identifier (0–4095).</param>
    /// <param name="isQinQ"><c>true</c> for QinQ (0x88A8), <c>false</c> for standard (0x8100).</param>
    /// <param name="pcp">Priority Code Point (0–7). Default 0.</param>
    /// <param name="dei">Drop Eligible Indicator (0–1). Default 0.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VlanLayer(ushort vlanId, bool isQinQ, byte pcp = 0, byte dei = 0)
    {
        _VlanId = vlanId;
        _Pcp = pcp;
        _Dei = dei;
        _IsQinQ = isQinQ;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => VlanTag.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _IsQinQ ? EtherTypes.QinQ : EtherTypes.VlanTagged;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        VlanTag tag = new()
        {
            Tci = VlanTag.MakeTci(_VlanId, _Pcp, _Dei),
            InnerEtherType = (ushort)0, // patched by PatchNextProtocol
        };
        _ = ((IBinarySerializable)tag).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
        => BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + InnerEtherTypeOffset, 2), next);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // VLAN has no post-fix work in any phase.
    }
}
