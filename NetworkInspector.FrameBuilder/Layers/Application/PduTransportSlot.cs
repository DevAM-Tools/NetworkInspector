// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// One PDU slot for use with <see cref="PduTransportMultiLayer"/>: an identifier
/// plus an opaque payload that may itself be a Signal-PDU bit-image, a SOME/IP
/// message, or any other byte stream.
/// </summary>
/// <remarks>
/// Thread safety: immutable value type; safe to use from any thread once
/// initialized.
/// </remarks>
public readonly struct PduTransportSlot
{
    /// <summary>PDU identifier; truncated to <see cref="PduTransportConfigFb.IdFieldSize"/> bytes (big-endian).</summary>
    public uint PduId
    {
        get; init;
    }

    /// <summary>Payload bytes for this slot; written verbatim after the slot header.</summary>
    public ReadOnlyMemory<byte> Payload
    {
        get; init;
    }
}
