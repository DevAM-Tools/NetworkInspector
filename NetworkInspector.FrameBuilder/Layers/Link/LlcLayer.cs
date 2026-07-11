// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IEEE 802.2 LLC (Logical Link Control) sub-layer, including SNAP extension,
/// for the <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>
/// In plain LLC mode (no SNAP), the header is 3 bytes:
/// </para>
/// <code>
/// Byte 0:   DSAP
/// Byte 1:   SSAP
/// Byte 2:   Control field (U-frame: 0x03)
/// </code>
/// <para>
/// In SNAP mode (DSAP = 0xAA, SSAP = 0xAA, Control = 0x03), the header is 8 bytes:
/// </para>
/// <code>
/// Byte 0:   DSAP = 0xAA
/// Byte 1:   SSAP = 0xAA
/// Byte 2:   Control = 0x03
/// Bytes 3-5: OUI (organization code, typically 00:00:00)
/// Bytes 6-7: EtherType (carries the upper-layer protocol)
/// </code>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IInteriorLayer"/> — sits between IEEE 802.3 Ethernet and upper layers.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header.</item>
///   <item><see cref="IConsumesNextProtocolValue{EtherTypeKind}"/> — auto-patches EtherType from the next layer in SNAP mode.</item>
///   <item><see cref="IProvidesNextProtocolValue{EtherTypeKind}"/> — provides its EtherType to the outer Ethernet layer
///   (for the containing 802.3 frame).</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct LlcLayer :
    IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent, IConsumesNextProtocolValue<EtherTypeKind>
{
    /// <summary>SNAP DSAP/SSAP constant per IEEE 802.2.</summary>
    public const byte SnapSap = 0xAA;

    /// <summary>SNAP control field for Unnumbered Information (UI).</summary>
    public const byte SnapControl = 0x03;

    /// <summary>Byte offset to the EtherType field in the SNAP variant.</summary>
    private const int _EtherTypeOffset = 6;

    private readonly byte _Dsap;
    private readonly byte _Ssap;
    private readonly byte _Control;
    private readonly uint _OuiAndEtherType; // [23:8] OUI | [7:0] high byte of EtherType + EtherType low byte packed
    private readonly bool _IsSnap;
    private readonly ushort _ExplicitEtherType;
    private readonly bool _EtherTypeIsExplicit;

    /// <summary>
    /// Creates a plain LLC layer (3-byte header, no SNAP extension).
    /// </summary>
    /// <param name="dsap">Destination SAP value.</param>
    /// <param name="ssap">Source SAP value.</param>
    /// <param name="control">Control field (default 0x03 = Unnumbered Information).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LlcLayer(byte dsap, byte ssap, byte control = 0x03)
    {
        _Dsap = dsap;
        _Ssap = ssap;
        _Control = control;
        _IsSnap = false;
        _OuiAndEtherType = 0;
        _EtherTypeIsExplicit = false;
        _ExplicitEtherType = 0;
    }

    /// <summary>
    /// Creates a SNAP LLC layer (8-byte header, DSAP=0xAA, SSAP=0xAA, Control=0x03).
    /// </summary>
    /// <param name="oui">3-byte OUI (organization code), default 0x000000 for generic Ethernet types.</param>
    /// <param name="etherType">EtherType value; use <see cref="Auto.Compute"/> (default) to auto-fill from the inner layer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LlcLayer CreateSnap(uint oui = 0, Auto<ushort> etherType = default)
    {
        bool isExplicit = etherType.TryGetExplicit(out ushort v);
        return new LlcLayer(oui, v, isExplicit);
    }

    // Private constructor for SNAP variant.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LlcLayer(uint oui, ushort etherType, bool etherTypeIsExplicit)
    {
        _Dsap = SnapSap;
        _Ssap = SnapSap;
        _Control = SnapControl;
        _IsSnap = true;
        _OuiAndEtherType = oui & 0xFFFFFF;
        _ExplicitEtherType = etherType;
        _EtherTypeIsExplicit = etherTypeIsExplicit;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_IsSnap)
            {
                return 8;
            }

            return 3;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        dst[0] = _Dsap;
        dst[1] = _Ssap;
        dst[2] = _Control;
        if (!_IsSnap)
        {
            return;
        }
        // OUI (3 bytes, big-endian)
        dst[3] = (byte)(_OuiAndEtherType >> 16);
        dst[4] = (byte)(_OuiAndEtherType >> 8);
        dst[5] = (byte)_OuiAndEtherType;
        // EtherType (patched in PatchNextProtocol if not explicit)
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6, 2), _ExplicitEtherType);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol)
    {
        if (_IsSnap && !_EtherTypeIsExplicit)
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
