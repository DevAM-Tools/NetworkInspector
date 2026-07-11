// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv6 Routing extension header (RFC 8200 §4.4, IP protocol 43).
/// Supports a minimal 8-byte form as well as Type 2 (home-address, Mobile IPv6)
/// and generic (variable type-specific data) routing headers.
/// </summary>
/// <remarks>
/// <para>Header layout: NextHeader(1) + HdrExtLen(1) + RoutingType(1) + SegmentsLeft(1)
/// + TypeSpecificData(variable, 8-byte aligned).</para>
/// </remarks>
public readonly struct IPv6RoutingLayer :
    IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent, IIPv6ExtensionLayer,
    IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader
{
    private readonly byte _ExplicitNextHeader;
    private readonly bool _NextHeaderIsExplicit;
    private readonly byte _RoutingType;
    private readonly byte _SegmentsLeft;
    private readonly ReadOnlyMemory<byte> _TypeSpecificData;

    /// <summary>Creates a minimal 8-byte Routing header with no type-specific data.</summary>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto.Compute"/> (default) means auto-patch from inner.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6RoutingLayer(Auto<byte> nextHeader = default)
    {
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
        _RoutingType = 0;
        _SegmentsLeft = 0;
        _TypeSpecificData = ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Creates a Type 2 (Mobile IPv6) Routing header with a 16-byte home address.</summary>
    /// <param name="homeAddress">16-byte IPv6 home address.</param>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto.Compute"/> (default) means auto-patch from inner.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when homeAddress is not 16 bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6RoutingLayer(ReadOnlyMemory<byte> homeAddress, Auto<byte> nextHeader = default)
    {
        if (homeAddress.Length != 16)
        {
            throw new ArgumentException("Home address must be 16 bytes.", nameof(homeAddress));
        }
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
        _RoutingType = 2; // Type 2 = Mobile IPv6 home address
        _SegmentsLeft = 1;
        _TypeSpecificData = homeAddress;
    }

    /// <summary>Creates a generic Routing header with caller-specified type and data.</summary>
    /// <param name="routingType">Routing type byte.</param>
    /// <param name="segmentsLeft">Segments left field.</param>
    /// <param name="typeSpecificData">Type-specific data; padded to 8-byte boundary.</param>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto.Compute"/> (default) means auto-patch from inner.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6RoutingLayer(
        byte routingType,
        byte segmentsLeft,
        ReadOnlyMemory<byte> typeSpecificData,
        Auto<byte> nextHeader = default)
    {
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
        _RoutingType = routingType;
        _SegmentsLeft = segmentsLeft;
        _TypeSpecificData = typeSpecificData;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Compute dynamically so zero-initialized structs (new()) give a valid 8-byte header.
            int dataLen = _TypeSpecificData.Length;
            if (dataLen == 0)
            {
                return 8; // minimal form: 4 fixed bytes + 4 reserved bytes
            }
            // 8 fixed bytes + data, rounded up to next 8-byte boundary.
            return (8 + dataLen + 7) & ~7;
        }
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6Routing;
    }

    /// <inheritdoc />
    public byte ExtensionProtocol
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6Routing;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        // Byte 0: NextHeader (patched later via PatchNextProtocol)
        dst[0] = _ExplicitNextHeader;
        // Byte 1: HdrExtLen = (totalLen / 8) - 1
        dst[1] = (byte)((HeaderSize / 8) - 1);
        // Byte 2: Routing Type
        dst[2] = _RoutingType;
        // Byte 3: Segments Left
        dst[3] = _SegmentsLeft;
        // Bytes 4-7: Reserved (zero)
        dst.Slice(4, 4).Clear();
        // Type-specific data (zero-padded)
        if (_TypeSpecificData.Length > 0)
        {
            _TypeSpecificData.Span.CopyTo(dst.Slice(8, _TypeSpecificData.Length));
            // Zero-pad between data end and padded size
            int paddedSize = HeaderSize;
            if (8 + _TypeSpecificData.Length < paddedSize)
            {
                dst.Slice(8 + _TypeSpecificData.Length, paddedSize - 8 - _TypeSpecificData.Length).Clear();
            }
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol)
        => IPv6ExtensionLayerHelpers.PatchNextProtocol(frame, myOffset, nextProtocol, _NextHeaderIsExplicit);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
        => IPv6ExtensionLayerHelpers.ApplyPostFix(phase, frame, myOffset, myLength, ref ctx, HeaderSize);
}

