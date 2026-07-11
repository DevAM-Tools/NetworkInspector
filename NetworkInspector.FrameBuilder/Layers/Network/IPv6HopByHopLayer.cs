// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv6 Hop-by-Hop Options extension header (RFC 8200 §4.3, IP protocol 0).
/// Minimal 8-byte form with a PadN option when no options are provided, or a
/// variable-size form when caller-supplied option bytes are given.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IIPv6ExtensionLayer"/>.</item>
///   <item><see cref="IProvidesProtocolType"/> = 0 (IPv6 HopByHop).</item>
///   <item><see cref="IProvidesNextProtocolValue"/> — outer IPv6/ext patches us.</item>
///   <item><see cref="IConsumesNextProtocolValue"/> — patches our NextHeader from inner.</item>
///   <item><see cref="IProvidesPseudoHeader"/> — forwards the outer IPv6's pseudo-header.</item>
/// </list>
/// </remarks>
public readonly struct IPv6HopByHopLayer :
    IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent, IIPv6ExtensionLayer,
    IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader
{
    /// <summary>Explicit next-header byte.  Meaningful only when <see cref="_NextHeaderIsExplicit"/> is <c>true</c>.</summary>
    private readonly byte _ExplicitNextHeader;

    /// <summary><c>true</c> when the caller pinned NextHeader via <see cref="Auto.Explicit"/>.</summary>
    private readonly bool _NextHeaderIsExplicit;

    /// <summary>Caller-supplied option bytes (may be empty).</summary>
    private readonly ReadOnlyMemory<byte> _OptionsData;

    /// <summary>Creates a minimal 8-byte Hop-by-Hop options header (PadN only).</summary>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto.Compute"/> (default) means auto-patch from inner.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6HopByHopLayer(Auto<byte> nextHeader = default)
    {
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
        _OptionsData = ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Creates a Hop-by-Hop options header with caller-supplied option bytes.</summary>
    /// <param name="options">
    /// Raw option bytes to include in the header.  The header is padded to
    /// the next multiple of 8 bytes per RFC 8200 §4.3.
    /// </param>
    /// <param name="nextHeader">
    /// NextHeader field; <see cref="Auto.Compute"/> (default) means auto-patch from inner.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6HopByHopLayer(ReadOnlyMemory<byte> options, Auto<byte> nextHeader = default)
    {
        _NextHeaderIsExplicit = nextHeader.TryGetExplicit(out byte nh);
        _ExplicitNextHeader = nh;
        _OptionsData = options;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // For zero-initialized default and empty-options: 8 bytes (PadN form).
            // For option data: round (2 + optionBytes) up to next 8-byte boundary.
            int optionLen = _OptionsData.Length;
            return optionLen == 0
                ? IPv6OptionsExtensionHeader.Size
                : Math.Max(8, (2 + optionLen + 7) & ~7);
        }
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6HopByHop;
    }

    /// <inheritdoc />
    public byte ExtensionProtocol
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.IPv6HopByHop;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        if (_OptionsData.IsEmpty)
        {
            // Minimal 8-byte form: PadN option fills the data area.
            IPv6ExtensionLayerHelpers.WriteHeader(dst, _ExplicitNextHeader);
            return;
        }

        // Variable-size form: write NextHeader + HdrExtLen + options + zero-padding.
        dst[0] = _ExplicitNextHeader;
        dst[1] = (byte)((HeaderSize / 8) - 1); // HdrExtLen = (total_octets / 8) - 1
        ReadOnlySpan<byte> options = _OptionsData.Span;
        options.CopyTo(dst.Slice(2, options.Length));
        // Remaining bytes in dst (padding) are left as zero (caller zeroes the buffer).
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
