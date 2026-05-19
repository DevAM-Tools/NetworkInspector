// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// SOME/IP application-layer (16-byte header) per AUTOSAR SomeIpProtocol §3.1.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches the SOME/IP Length field.
///   The Length field counts bytes from offset 8 (ClientId) to the end of
///   the payload (inclusive), per the AUTOSAR spec.</item>
/// </list>
/// <para>For segmented SOME/IP-TP transport see <see cref="SomeIpTpLayer"/>
/// (AUTOSAR §5).</para>
/// </remarks>
public readonly struct SomeIpLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    /// <summary>Offset of the Length field within the SOME/IP header.</summary>
    private const int LengthOffset = 4;

    /// <summary>
    /// Number of bytes preceding (and including) the Length field — the
    /// Length field counts everything AFTER these initial 8 bytes
    /// (ServiceId, MethodId, Length itself).
    /// </summary>
    private const int LengthFieldEndOffset = 8;

    private readonly ushort _ServiceId;
    private readonly ushort _MethodId;
    private readonly ushort _ClientId;
    private readonly ushort _SessionId;
    private readonly byte _ProtocolVersion;
    private readonly byte _InterfaceVersion;
    private readonly byte _MessageType;
    private readonly byte _ReturnCode;

    /// <summary>Creates a SOME/IP application-layer header.</summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="methodId">Method / event identifier.</param>
    /// <param name="clientId">Client identifier; default 0.</param>
    /// <param name="sessionId">Session identifier; default 0.</param>
    /// <param name="messageType">Message type (see <see cref="SomeIpMessageType"/>).</param>
    /// <param name="returnCode">Return code (0 = OK).</param>
    /// <param name="interfaceVersion">Interface version (default 1).</param>
    /// <param name="protocolVersion">Protocol version (default 1, current SOME/IP).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SomeIpLayer(
        ushort serviceId,
        ushort methodId,
        ushort clientId = 0,
        ushort sessionId = 0,
        byte messageType = SomeIpMessageType.Request,
        byte returnCode = 0,
        byte interfaceVersion = 1,
        byte protocolVersion = 1)
    {
        _ServiceId = serviceId;
        _MethodId = methodId;
        _ClientId = clientId;
        _SessionId = sessionId;
        _MessageType = messageType;
        _ReturnCode = returnCode;
        _InterfaceVersion = interfaceVersion;
        _ProtocolVersion = protocolVersion;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => SomeIpHeader.Size;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        SomeIpHeader hdr = new()
        {
            ServiceId = _ServiceId,
            MethodId = _MethodId,
            Length = 0, // patched in post-fix
            ClientId = _ClientId,
            SessionId = _SessionId,
            ProtocolVersion = _ProtocolVersion,
            InterfaceVersion = _InterfaceVersion,
            MessageType = _MessageType,
            ReturnCode = _ReturnCode,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.Length)
        {
            return;
        }

        // Length covers ClientId..end of payload = total SOME/IP message
        // size minus the first 8 bytes (ServiceId + MethodId + Length itself).
        uint length = (uint)(myLength - LengthFieldEndOffset);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(myOffset + LengthOffset, 4), length);
    }
}
