// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// SOME/IP-TP (Transport Protocol) application-layer header (20 bytes =
/// 16-byte SOME/IP base + 4-byte TP word) for segmented message transport
/// per AUTOSAR SomeIpProtocol §5.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier.</item>
///   <item><see cref="IFragmentable"/> with
///   <see cref="FragmentationKind.ApplicationSegmentation"/> and
///   <see cref="FragmentAlignment"/> = 16 — splits the SOME/IP-TP payload
///   into 16-byte-aligned segments; each emitted segment is a complete IP
///   datagram with its own transport checksum.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches the SOME/IP Length field
///   (same semantics as <see cref="SomeIpLayer"/>).</item>
/// </list>
/// <para>
/// The TP word at header offset 16 encodes the segment offset (upper 28 bits,
/// in 16-byte units) and the More Segments flag (bit 0).  When this layer
/// participates in fragmentation, the TP word is patched per emitted segment
/// by <see cref="PatchFragmentHeader"/>.  Direct construction with
/// <c>moreSegments</c> / <c>tpOffsetIn16Bytes</c> remains available for
/// callers who want to author single segments manually.
/// </para>
/// </remarks>
public readonly struct SomeIpTpLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent, IFragmentable
{
    /// <summary>Offset of the Length field within the SOME/IP header.</summary>
    private const int _LengthOffset = 4;

    /// <summary>
    /// Number of bytes before the Length value that are NOT counted in the Length field.
    /// </summary>
    private const int _LengthFieldEndOffset = 8;

    /// <summary>Total header size: 16-byte SOME/IP header + 4-byte TP word.</summary>
    public const int HeaderBytes = SomeIpHeader.Size + 4;

    private readonly ushort _ServiceId;
    private readonly ushort _MethodId;
    private readonly ushort _ClientId;
    private readonly ushort _SessionId;
    private readonly byte _ProtocolVersion;
    private readonly byte _InterfaceVersion;
    private readonly byte _MessageType;
    private readonly byte _ReturnCode;

    /// <summary>
    /// TP word: upper 28 bits = segment offset in 16-byte units, bit 0 = More Segments.
    /// </summary>
    private readonly uint _TpWord;

    /// <summary>Creates a SOME/IP-TP layer header.</summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="methodId">Method / event identifier.</param>
    /// <param name="clientId">Client identifier; default 0.</param>
    /// <param name="sessionId">Session identifier; default 0.</param>
    /// <param name="baseMessageType">
    /// Base message type before OR-ing with the TP flag (0x20).
    /// Default is <see cref="SomeIpMessageType.Request"/>.
    /// </param>
    /// <param name="tpOffsetIn16Bytes">
    /// Segment offset in units of 16 bytes; used for all fragments except the first.
    /// </param>
    /// <param name="moreSegments">More Segments flag; <c>true</c> for all fragments except the last.</param>
    /// <param name="returnCode">Return code; default 0.</param>
    /// <param name="interfaceVersion">Interface version; default 1.</param>
    /// <param name="protocolVersion">Protocol version; default 1.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SomeIpTpLayer(
        ushort serviceId,
        ushort methodId,
        ushort clientId = 0,
        ushort sessionId = 0,
        byte baseMessageType = SomeIpMessageType.Request,
        uint tpOffsetIn16Bytes = 0,
        bool moreSegments = false,
        byte returnCode = 0,
        byte interfaceVersion = 1,
        byte protocolVersion = 1)
    {
        _ServiceId = serviceId;
        _MethodId = methodId;
        _ClientId = clientId;
        _SessionId = sessionId;
        // TP flag bit 0x20 is always set in SOME/IP-TP messages.
        _MessageType = (byte)(baseMessageType | SomeIpMessageType.TpFlag);
        _ReturnCode = returnCode;
        _InterfaceVersion = interfaceVersion;
        _ProtocolVersion = protocolVersion;
        // TP word: upper 28 bits = offset, bit 0 = M flag.
        _TpWord = (tpOffsetIn16Bytes << 4) | (moreSegments ? 1u : 0u);
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HeaderBytes;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        // Write the 16-byte SOME/IP header.
        SomeIpHeader hdr = new()
        {
            ServiceId = _ServiceId,
            MethodId = _MethodId,
            Length = 0,  // patched in post-fix
            ClientId = _ClientId,
            SessionId = _SessionId,
            ProtocolVersion = _ProtocolVersion,
            InterfaceVersion = _InterfaceVersion,
            MessageType = _MessageType,
            ReturnCode = _ReturnCode,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);

        // Write the 4-byte TP word immediately after the SOME/IP header.
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(SomeIpHeader.Size, 4), _TpWord);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.Length)
        {
            return;
        }

        // Length field semantics identical to SomeIpLayer:
        // covers bytes from ClientId offset (8) to end of payload.
        uint length = (uint)(myLength - _LengthFieldEndOffset);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(myOffset + _LengthOffset, 4), length);
    }

    /// <inheritdoc />
    /// <remarks>SOME/IP-TP allows segmentation whenever this layer is present.</remarks>
    public bool CanFragment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    /// <inheritdoc />
    public FragmentationKind FragmentationKind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => FragmentationKind.ApplicationSegmentation;
    }

    /// <inheritdoc />
    /// <remarks>
    /// AUTOSAR §5: SOME/IP-TP segment offset is encoded in 16-byte units, so
    /// every non-final segment must carry a payload size that is a multiple
    /// of 16 octets.
    /// </remarks>
    public int FragmentAlignment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 16;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Rewrites the 4-byte TP word at <c>myOffset + SomeIpHeader.Size</c>
    /// with the per-segment offset (upper 28 bits, in 16-byte units) and the
    /// More Segments flag (bit 0).  The Length field is repatched by the
    /// <see cref="FixPhase.Length"/> phase that the segmentation iterator
    /// re-runs over each emitted segment.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        _ = myLength; // Repatched by FixPhase.Length.
        // Segment offset is in 16-byte units; the upper 28 bits of the TP word
        // hold it, bit 0 is the More Segments flag.
        uint tpWord = ((uint)fragmentPayloadOffset >> 4) << 4;
        if (moreFragments)
        {
            tpWord |= 1u;
        }
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(myOffset + SomeIpHeader.Size, 4), tpWord);
    }
}
