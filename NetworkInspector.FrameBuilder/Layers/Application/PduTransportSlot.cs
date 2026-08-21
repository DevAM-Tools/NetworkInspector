// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// One PDU slot for use with <see cref="PduTransportMultiLayer"/>: an identifier
/// plus an opaque payload that may itself be a Signal Message bit-image, a SOME/IP
/// message, or any other byte stream.
/// </summary>
/// <remarks>
/// Thread safety: immutable value type; safe to use from any thread once
/// initialized.
/// </remarks>
/// <param name="PduId">PDU identifier; truncated to <see cref="PduTransportConfigFb.IdFieldSize"/> bytes (big-endian).</param>
/// <param name="Payload">Payload bytes for this slot; written verbatim after the slot header.</param>
public readonly record struct PduTransportSlot(uint PduId, ReadOnlyMemory<byte> Payload);
