// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer can serialize itself into a contiguous byte stream
/// of arbitrary length, suitable for handing to an <see cref="IStreamCarrier"/>
/// (TCP) for MSS-conformant segmentation.
/// </summary>
/// <remarks>
/// <para>
/// Stream-producing application layers (WebSocket, HTTP/2 frames, TLS
/// records) are themselves rahmungsorientiert — they own a length-prefixed
/// or framed wire format.  Once serialized, the resulting bytes are an
/// opaque byte stream from the carrier's perspective; the carrier is free
/// to slice them on any boundary.
/// </para>
/// <para>
/// Implementations write their full byte stream by appending into the
/// supplied <see cref="System.Buffers.IBufferWriter{T}"/>.  The writer is
/// owned by the carrier (typically a pooled
/// <see cref="System.Buffers.ArrayBufferWriter{T}"/>) and may be backed by
/// rented memory; producers must not retain references after returning.
/// </para>
/// <para>
/// The contract is intentionally allocation-light: no <c>byte[]</c> is
/// returned and no <see cref="System.IO.Stream"/> is required.  Multiple
/// <c>Advance</c> calls are permitted.
/// </para>
/// <para>
/// Thread-safety: instances are typically value-types and not thread-safe;
/// each call to <see cref="WriteStream"/> mutates the supplied writer.
/// The caller must ensure the writer is not concurrently accessed.
/// </para>
/// </remarks>
public interface IStreamProducer
{
    /// <summary>
    /// Writes the producer's complete serialized byte stream into the
    /// supplied buffer writer.
    /// </summary>
    /// <param name="writer">Pooled byte-buffer writer; not retained beyond the call.</param>
    void WriteStream(IBufferWriter<byte> writer);
}
