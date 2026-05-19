// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv6 Destination Options extension header (RFC 8200 §4.6, IP protocol 60).
/// Minimal 8-byte form with a PadN option.
/// </summary>
public readonly struct IPv6DestinationOptionsLayer :
    IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent, IIPv6ExtensionLayer,
    IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader
{
    private readonly byte _ExplicitNextHeader;
    private readonly bool _NextHeaderIsExplicit;

    /// <summary>Creates a Destination Options header layer.</summary>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto{T}.Compute"/> (default) means auto-patch from inner.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6DestinationOptionsLayer(Auto<byte> nextHeader = default)
    {
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IPv6OptionsExtensionHeader.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6DestinationOptions;
    }

    /// <inheritdoc />
    public byte ExtensionProtocol
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6DestinationOptions;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
        => IPv6ExtensionLayerHelpers.WriteHeader(dst, _ExplicitNextHeader);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next)
        => IPv6ExtensionLayerHelpers.PatchNextProtocol(frame, myOffset, next, _NextHeaderIsExplicit);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
        => IPv6ExtensionLayerHelpers.ApplyPostFix(phase, frame, myOffset, myLength, ref ctx, HeaderSize);
}
