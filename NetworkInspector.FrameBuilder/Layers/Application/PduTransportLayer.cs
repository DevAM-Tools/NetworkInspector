// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Single-PDU AUTOSAR PDU-Transport application layer for the new
/// <see cref="FrameStack"/> API. Wire format per AUTOSAR
/// <c>SoAdSocketProtocolPduHeader</c>:
/// <code>
/// [PDU ID:   1, 2 or 4 bytes, big-endian]
/// [Length:   1, 2 or 4 bytes, big-endian]
/// [Payload:  N bytes — supplied by the next layer]
/// </code>
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IInteriorLayer"/> — sub-layer (e.g.
///   <see cref="SignalPduLayer"/>) chains beneath it; mutually exclusive
///   with <see cref="IPayloadLayer"/> per the capability contract.</item>
///   <item><see cref="IStatelessLayer"/> — no per-frame mutable state.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — needs no transport pseudo
///   header.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches the Length field with the
///   number of payload bytes following the header.</item>
/// </list>
/// <para>
/// Construction goes exclusively through the <see cref="Single"/> factory so
/// the caller hands in a <see cref="PduTransportConfigFb"/> that the test
/// bridge also writes into the parser settings and the tshark UAT profile;
/// thus all three sides agree on the ID/Length field sizes.
/// </para>
/// <para>For multi-PDU datagrams (concatenated PDUs with one
/// <c>[ID][Len][Payload]</c> tuple per slot) use
/// <see cref="PduTransportMultiLayer"/>.</para>
/// <para>Thread safety: immutable struct, safe for concurrent use.</para>
/// </remarks>
public readonly struct PduTransportLayer : IStatelessLayer, IInteriorLayer, IPseudoHeaderIndependent
{
    private readonly uint _PduId;
    private readonly byte _IdSize;
    private readonly byte _LengthSize;

    /// <summary>
    /// Creates a single-PDU PDU-Transport header layer that emits one
    /// <c>[ID][Length]</c> tuple followed by the inner layer's payload.
    /// </summary>
    /// <param name="config">Configuration with ID/Length field sizes (validated at config-construction time).</param>
    /// <param name="pduId">PDU identifier; truncated to <see cref="PduTransportConfigFb.IdFieldSize"/> bytes (big-endian).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PduTransportLayer Single(PduTransportConfigFb config, uint pduId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new PduTransportLayer(pduId, config.IdFieldSize, config.LengthFieldSize);
    }

    private PduTransportLayer(uint pduId, byte idSize, byte lengthSize)
    {
        _PduId = pduId;
        _IdSize = idSize;
        _LengthSize = lengthSize;
    }

    /// <summary>The PDU identifier this layer encodes.</summary>
    public uint PduId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _PduId;
    }

    /// <summary>Size of the on-the-wire ID field in bytes.</summary>
    public byte IdSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _IdSize;
    }

    /// <summary>Size of the on-the-wire Length field in bytes.</summary>
    public byte LengthSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _LengthSize;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _IdSize + _LengthSize;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        PduTransportEncoding.WriteBigEndian(dst[.._IdSize], _PduId, _IdSize);
        // Length stays zero; patched in FixPhase.Length once the inner layer's
        // contribution is known.
        dst.Slice(_IdSize, _LengthSize).Clear();
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.Length)
        {
            return;
        }

        // myLength = ID + Length + payload; we want only the payload count.
        uint payloadLength = (uint)(myLength - HeaderSize);
        PduTransportEncoding.WriteBigEndian(frame.Slice(myOffset + _IdSize, _LengthSize), payloadLength, _LengthSize);
    }
}

/// <summary>
/// Shared big-endian writer used by <see cref="PduTransportLayer"/> and
/// <see cref="PduTransportMultiLayer"/>; centralises the 1/2/4-byte switch
/// so the two layers cannot drift apart.
/// </summary>
internal static class PduTransportEncoding
{
    /// <summary>
    /// Writes <paramref name="value"/> into the first <paramref name="size"/>
    /// bytes of <paramref name="dst"/> in big-endian order.
    /// </summary>
    /// <remarks>
    /// Only sizes 1, 2 and 4 are valid — the validation lives in
    /// <see cref="PduTransportConfigFb"/>'s constructor so callers cannot
    /// reach this method with an unsupported size.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteBigEndian(scoped Span<byte> dst, uint value, byte size)
    {
        switch (size)
        {
            case 1:
                dst[0] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)value);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(dst, value);
                break;
        }
    }
}
