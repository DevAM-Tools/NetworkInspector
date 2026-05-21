// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Stateful SOME/IP-TP application layer that auto-advances the SOME/IP
/// SessionId per emitted logical packet (not per fragment) per AUTOSAR
/// SomeIpProtocol §4.1.2.5.
/// </summary>
/// <remarks>
/// <para>
/// State slot: <see cref="SessionState.SomeIpNextSessionId"/>.  Initialised
/// to the caller-supplied <c>initialSessionId</c>; advanced by 1 per
/// <see cref="WriteHeader(System.Span{byte},ref SessionState)"/> call,
/// skipping the reserved value 0 on wraparound.
/// </para>
/// <para>
/// Capabilities mirror <see cref="SomeIpTpLayer"/>:
/// <see cref="IPayloadLayer"/> + <see cref="IFragmentable"/> with
/// <see cref="FragmentationKind.ApplicationSegmentation"/> and
/// <see cref="FragmentAlignment"/> = 16.
/// </para>
/// <para>
/// Only usable inside a <see cref="Session{TStack,TTrailer,TInterceptor}"/>;
/// stateless emission is rejected at compile time.
/// </para>
/// </remarks>
public readonly struct SomeIpTpLayerWithAutoCounter :
    IStatefulLayer, IPayloadLayer, IPseudoHeaderIndependent, IFragmentable
{
    /// <summary>Offset of the Length field within the SOME/IP header.</summary>
    private const int LengthOffset = 4;

    /// <summary>Bytes before Length value not counted in the Length field.</summary>
    private const int LengthFieldEndOffset = 8;

    /// <summary>Total header size: 16-byte SOME/IP header + 4-byte TP word.</summary>
    public const int HeaderBytes = SomeIpHeader.Size + 4;

    private readonly ushort _ServiceId;
    private readonly ushort _MethodId;
    private readonly ushort _ClientId;
    private readonly ushort _InitialSessionId;
    private readonly byte _ProtocolVersion;
    private readonly byte _InterfaceVersion;
    private readonly byte _MessageType;
    private readonly byte _ReturnCode;

    /// <summary>Creates a stateful SOME/IP-TP layer with an auto-incrementing SessionId.</summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="methodId">Method / event identifier.</param>
    /// <param name="initialSessionId">Initial SOME/IP SessionId; 0 is illegal and replaced by 1.</param>
    /// <param name="clientId">Client identifier; default 0.</param>
    /// <param name="baseMessageType">
    /// Base message type before OR-ing with the TP flag (0x20).
    /// Default is <see cref="SomeIpMessageType.Request"/>.
    /// </param>
    /// <param name="returnCode">Return code; default 0.</param>
    /// <param name="interfaceVersion">Interface version; default 1.</param>
    /// <param name="protocolVersion">Protocol version; default 1.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SomeIpTpLayerWithAutoCounter(
        ushort serviceId,
        ushort methodId,
        ushort initialSessionId = 1,
        ushort clientId = 0,
        byte baseMessageType = SomeIpMessageType.Request,
        byte returnCode = 0,
        byte interfaceVersion = 1,
        byte protocolVersion = 1)
    {
        _ServiceId = serviceId;
        _MethodId = methodId;
        // SessionId 0 is reserved per AUTOSAR §4.1.2.5; substitute 1.
        _InitialSessionId = initialSessionId == 0 ? (ushort)1 : initialSessionId;
        _ClientId = clientId;
        // TP flag bit 0x20 is always set in SOME/IP-TP messages.
        _MessageType = (byte)(baseMessageType | SomeIpMessageType.TpFlag);
        _ReturnCode = returnCode;
        _InterfaceVersion = interfaceVersion;
        _ProtocolVersion = protocolVersion;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HeaderBytes;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeState(ref SessionState state)
    {
        state.SomeIpNextSessionId = _InitialSessionId;
        state.HasSomeIpAutoCounter = true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
    {
        // Snapshot current SessionId for THIS logical packet then advance,
        // skipping the reserved value 0 on wraparound.
        ushort sessionId = state.SomeIpNextSessionId;
        if (sessionId == 0)
        {
            sessionId = 1;
        }
        ushort next = unchecked((ushort)(sessionId + 1));
        if (next == 0)
        {
            next = 1;
        }
        state.SomeIpNextSessionId = next;

        SomeIpHeader hdr = new()
        {
            ServiceId = _ServiceId,
            MethodId = _MethodId,
            Length = 0, // patched in FixPhase.Length
            ClientId = _ClientId,
            SessionId = sessionId,
            ProtocolVersion = _ProtocolVersion,
            InterfaceVersion = _InterfaceVersion,
            MessageType = _MessageType,
            ReturnCode = _ReturnCode,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);

        // TP word: zeroed; patched per segment by PatchFragmentHeader for
        // segmented messages, or left zero for single-frame messages
        // (offset 0, MF=0).
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(SomeIpHeader.Size, 4), 0u);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.Length)
        {
            return;
        }

        // Length covers bytes from ClientId offset (8) to end of payload.
        uint length = (uint)(myLength - LengthFieldEndOffset);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(myOffset + LengthOffset, 4), length);
    }

    /// <inheritdoc />
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
    public int FragmentAlignment
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 16;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength, int fragmentPayloadOffset, bool moreFragments)
    {
        _ = myLength;
        // Segment offset in 16-byte units (upper 28 bits); MF flag in bit 0.
        uint tpWord = ((uint)fragmentPayloadOffset >> 4) << 4;
        if (moreFragments)
        {
            tpWord |= 1u;
        }
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(myOffset + SomeIpHeader.Size, 4), tpWord);
    }
}
