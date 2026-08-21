// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// IPv4 network-layer header with options (variable size 20–60 bytes) for the
/// new <see cref="FrameStack"/> API.  The caller supplies pre-encoded option
/// bytes; this layer pads them to a 4-byte boundary and adjusts the IHL field.
/// </summary>
/// <remarks>
/// <para>Capabilities mirror <see cref="IPv4Layer"/> exactly — including
/// <see cref="IFragmentable"/>.</para>
/// <para>Post-fix:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches TotalLength.</item>
///   <item><see cref="FixPhase.PublishPseudoHeader"/> — publishes addresses; the
///   transport region begins after the variable-length header.</item>
///   <item><see cref="FixPhase.OuterChecksum"/> — recomputes the IPv4 header
///   checksum over the variable-length header.</item>
/// </list>
/// </remarks>
public readonly struct IPv4LayerWithOptions :
    IStatelessLayer, IRootLayer, IInteriorLayer, IPseudoHeaderIndependent,
    IProvidesProtocolType, IProvidesNextProtocolValue<EtherTypeKind>,
    IConsumesNextProtocolValue<IpNextProtocolKind>, IProvidesPseudoHeader, IFragmentable
{
    /// <summary>Offset of the Protocol field within the IPv4 header.</summary>
    private const int _ProtocolFieldOffset = 9;

    /// <summary>Offset of the SrcAddr field within the IPv4 header.</summary>
    private const int _SrcAddrOffset = 12;

    /// <summary>Offset of the DstAddr field within the IPv4 header.</summary>
    private const int _DstAddrOffset = 16;

    /// <summary>Maximum total IPv4 header size (IHL=15).</summary>
    private const int _MaxHeaderSize = 60;

    /// <summary>Offset of the Flags+FragmentOffset combined 16-bit field.</summary>
    private const int _FlagsFragOffsetOffset = 6;

    /// <summary>Mask of the MF (More Fragments) flag inside the combined field.</summary>
    private const ushort _MoreFragmentsMask = 0x2000;

    /// <summary>Mask of the FragmentOffset bits (in 8-octet units).</summary>
    private const ushort _FragmentOffsetMask = 0x1FFF;

    private readonly IPv4Address _SrcAddr;
    private readonly IPv4Address _DstAddr;
    private readonly byte _Ttl;
    private readonly ushort _Identification;
    private readonly byte _ExplicitProtocol;

    /// <summary><c>true</c> when the caller supplied an explicit Protocol via <see cref="Auto.Explicit"/>.</summary>
    private readonly bool _ProtocolIsExplicit;

    /// <summary>Caller-supplied option bytes (raw, will be padded with 0x00 EOOL).</summary>
    private readonly ReadOnlyMemory<byte> _Options;

    /// <summary>Padded option length in bytes (multiple of 4).</summary>
    private readonly byte _PaddedOptionsLength;

    /// <summary>
    /// Stored as <c>!DontFragment</c> ("fragmentation allowed") so
    /// <c>default(IPv4LayerWithOptions)</c> emits DF=1 (safe default).
    /// <c>true</c> means fragmentation is permitted.
    /// </summary>
    public bool CanFragment { get; }

    /// <summary>Creates an IPv4 layer with options.</summary>
    /// <param name="srcAddr">Source address.</param>
    /// <param name="dstAddr">Destination address.</param>
    /// <param name="options">Pre-encoded option bytes; padded to a 4-byte boundary.</param>
    /// <param name="ttl">Time-to-live; default 64.</param>
    /// <param name="identification">Identification field; default 0.</param>
    /// <param name="protocol">
    /// Protocol field; <see cref="Auto.Compute"/> (default) auto-patches from inner layer.
    /// </param>
    /// <param name="dontFragment">
    /// Don't-Fragment (DF) flag.  Default <c>true</c>.  Set to <c>false</c> to
    /// allow fragmentation — required when the frame may exceed the path MTU.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when options exceed 40 bytes (max IHL=15).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv4LayerWithOptions(
        IPv4Address srcAddr,
        IPv4Address dstAddr,
        ReadOnlyMemory<byte> options,
        byte ttl = 64,
        ushort identification = 0,
        Auto<byte> protocol = default,
        bool dontFragment = true)
    {
        // Pad options length up to a 4-byte boundary; max header = 60 bytes (40 options).
        int padded = (options.Length + 3) & ~3;
        if (padded > _MaxHeaderSize - IPv4Header.Size)
        {
            throw new ArgumentException(
                $"IPv4 options exceed the maximum length of {_MaxHeaderSize - IPv4Header.Size} bytes.",
                nameof(options));
        }

        _SrcAddr = srcAddr;
        _DstAddr = dstAddr;
        _Options = options;
        _PaddedOptionsLength = (byte)padded;
        _Ttl = ttl;
        _Identification = identification;
        _ProtocolIsExplicit = protocol.TryGetExplicit(out byte p);
        _ExplicitProtocol = p;
        // Store "fragmentation allowed" so the struct's default emits DF=1.
        CanFragment = !dontFragment;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IPv4Header.Size + _PaddedOptionsLength;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => EtherTypes.IPv4;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        int totalSize = IPv4Header.Size + _PaddedOptionsLength;
        byte ihl = (byte)(totalSize / 4);

        IPv4Header hdr = IPv4Header.Create(
            _SrcAddr, _DstAddr, _ExplicitProtocol,
            _Ttl, _Identification,
            dontFragment: !CanFragment,
            ihl: ihl);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);

        // Copy raw option bytes, then zero-pad up to the padded length (EOOL = 0x00).
        if (_Options.Length > 0)
        {
            _Options.Span.CopyTo(dst[IPv4Header.Size..]);
        }
        if (_PaddedOptionsLength > _Options.Length)
        {
            dst.Slice(IPv4Header.Size + _Options.Length, _PaddedOptionsLength - _Options.Length).Clear();
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol)
    {
        if (!_ProtocolIsExplicit)
        {
            frame[myOffset + _ProtocolFieldOffset] = (byte)nextProtocol;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        int headerSize = IPv4Header.Size + _PaddedOptionsLength;
        switch (phase)
        {
            case FixPhase.Length:
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + 2, 2), (ushort)myLength);
                break;

            case FixPhase.PublishPseudoHeader:
                frame.Slice(myOffset + _SrcAddrOffset, 4).CopyTo(ctx.PseudoSrcIp);
                frame.Slice(myOffset + _DstAddrOffset, 4).CopyTo(ctx.PseudoDstIp);
                ctx.PseudoIpLength = 4;
                ctx.PseudoIsIPv6 = false;
                ctx.PseudoProtocol = frame[myOffset + _ProtocolFieldOffset];
                ctx.TransportOffset = myOffset + headerSize;
                ctx.TransportEnd = myOffset + myLength;
                break;

            case FixPhase.OuterChecksum:
                // Zero the checksum field, then recompute over the entire variable-length header.
                frame[myOffset + 10] = 0;
                frame[myOffset + 11] = 0;
                ushort checksum = ChecksumUtils.IPv4Header(frame.Slice(myOffset, headerSize));
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + 10, 2), checksum);
                break;

            default:
                break;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Patches the IPv4 Flags+FragmentOffset 16-bit field with the per-fragment
    /// MF flag and the FragmentOffset (in 8-octet units), and clears the DF bit
    /// so the fragmented frame is RFC-conformant.  TotalLength and the header
    /// checksum are recomputed by the regular <see cref="FixPhase.Length"/> and
    /// <see cref="FixPhase.OuterChecksum"/> phases that the fragmenting loop
    /// re-runs over each fragment.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        _ = myLength; // Only TotalLength uses it; that field is repatched by FixPhase.Length.
        ushort fragField = (ushort)((fragmentPayloadOffset >> 3) & _FragmentOffsetMask);
        if (moreFragments)
        {
            fragField |= _MoreFragmentsMask;
        }
        // The DF bit is implicitly cleared because we rewrite the full 16-bit field;
        // any DF flag that may have been carried over from the cached header is dropped.
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + _FlagsFragOffsetOffset, 2), fragField);
    }
}
