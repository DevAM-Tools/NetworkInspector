// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Multi-PDU AUTOSAR PDU-Transport application layer: emits a sequence of
/// <c>[PDU ID][Length][Payload]</c> tuples back-to-back inside one UDP
/// datagram per the AUTOSAR PDU-Router concatenation convention.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — terminal carrier; the slot
///   payloads are supplied at construction, so no further inner layer is
///   composed in the FrameBuilder stack.</item>
///   <item><see cref="IStatelessLayer"/> — no per-frame mutable state.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — needs no transport pseudo
///   header.</item>
/// </list>
/// <para>
/// Lengths are written verbatim during <see cref="WriteHeader"/>; no
/// <see cref="FixPhase.Length"/> patch is required because the layer knows
/// each slot's payload size up front.
/// </para>
/// <para>Thread safety: immutable struct after construction; the underlying
/// payload memory must remain valid until the surrounding frame has been
/// emitted.</para>
/// </remarks>
public readonly struct PduTransportMultiLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    /// <summary>Size of the on-the-wire ID field in bytes.</summary>
    public byte IdSize { get; }

    /// <summary>Size of the on-the-wire Length field in bytes.</summary>
    public byte LengthSize { get; }

    /// <inheritdoc />
    public int HeaderSize { get; }

    private readonly PduTransportSlot[] _Slots;

    private PduTransportMultiLayer(byte idSize, byte lengthSize, PduTransportSlot[] slots, int totalSize)
    {
        IdSize = idSize;
        LengthSize = lengthSize;
        _Slots = slots;
        HeaderSize = totalSize;
    }

    /// <summary>
    /// Creates a multi-PDU layer with one or more concatenated slots.
    /// </summary>
    /// <param name="config">Configuration with ID/Length field sizes (validated at config-construction time).</param>
    /// <param name="slots">Slots in wire order; at least one is required.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="slots"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any slot's payload size cannot fit into the configured
    /// <see cref="PduTransportConfigFb.LengthFieldSize"/>.
    /// </exception>
    public static PduTransportMultiLayer Create(PduTransportConfigFb config, params PduTransportSlot[] slots)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Length == 0)
        {
            throw new ArgumentException("PDU-Transport multi-layer needs at least one slot.", nameof(slots));
        }

        byte idSize = config.IdFieldSize;
        byte lengthSize = config.LengthFieldSize;
        long maxPayload = lengthSize switch
        {
            1 => byte.MaxValue,
            2 => ushort.MaxValue,
            _ => uint.MaxValue,
        };

        int total = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            int payloadLen = slots[i].Payload.Length;
            if (payloadLen > maxPayload)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slots),
                    payloadLen,
                    $"Slot {i} (PDU ID 0x{slots[i].PduId:X}) payload of {payloadLen} bytes"
                        + $" exceeds the {lengthSize}-byte Length field range (max {maxPayload}).");
            }
            total += idSize + lengthSize + payloadLen;
        }

        // Defensive copy: the FrameSequence may emit the same stack repeatedly,
        // and a caller-mutated `params` array would otherwise drift.
        PduTransportSlot[] copy = new PduTransportSlot[slots.Length];
        Array.Copy(slots, copy, slots.Length);
        return new PduTransportMultiLayer(idSize, lengthSize, copy, total);
    }

    /// <summary>The number of slots this layer encodes.</summary>
    public int SlotCount
    {
        get
        {
            if (_Slots is null)
            {
                return 0;
            }

            return _Slots.Length;
        }
    }

    /// <summary>Returns the PDU ID of the slot at <paramref name="index"/>.</summary>
    public uint GetSlotPduId(int index) => _Slots[index].PduId;

    /// <inheritdoc />
    public void WriteHeader(scoped Span<byte> dst)
    {
        PduTransportSlot[] slots = _Slots;
        byte idSize = IdSize;
        byte lengthSize = LengthSize;
        int offset = 0;

        for (int i = 0; i < slots.Length; i++)
        {
            PduTransportSlot slot = slots[i];
            int payloadLen = slot.Payload.Length;

            PduTransportEncoding.WriteBigEndian(dst.Slice(offset, idSize), slot.PduId, idSize);
            offset += idSize;

            PduTransportEncoding.WriteBigEndian(dst.Slice(offset, lengthSize), (uint)payloadLen, lengthSize);
            offset += lengthSize;

            slot.Payload.Span.CopyTo(dst.Slice(offset, payloadLen));
            offset += payloadLen;
        }
    }

    /// <inheritdoc />
    /// <remarks>No-op: lengths are written verbatim in <see cref="WriteHeader"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
    }
}
