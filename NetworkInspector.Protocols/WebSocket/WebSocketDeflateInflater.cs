// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using IO = System.IO;

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Per-message DEFLATE decompression (RFC 7692)

    // Limit: largest decompressed WebSocket frame payload in bytes (RSV1 frames, permessage-deflate).
    // Value: default 67108864 (64 MiB), allowed 1024 (1 KiB) to 137438953472 (128 GiB); a value outside the range is rejected by
    // the settings layer and the default stays. Threat: decompression bomb, a few kilobytes expanding to gigabytes of memory.
    // Configurable (setting, changed by the operator or user), not a constant, because the right size depends on the host and
    // on the traffic; it is not a protocol fact. Posture: secure by default, because WebSocket payloads come from untrusted
    // peers; raising it is opt-in. Rejected alternative: a fixed constant (cripples hosts that inspect large messages).
    // Exceeding it is an expected failure: inflating stops at the limit, the payload stays undecoded (never truncated) and the
    // websocket.payload.decompressed.limit field names this setting. Residual risk: a value near the maximum lets one frame use
    // that much memory (and time); in addition one array holds the result, so about 2 GiB is the hard bound whatever is set.
    [U64Setting("ws.max_decompressed_bytes", "Maximum decompressed WebSocket payload size (bytes)", "ws",
        Default = 67108864, HasMin = true, Min = 1024, HasMax = true, Max = 137438953472,
        Description = "Upper bound for a decompressed WebSocket frame payload in bytes (1 KiB to 128 GiB). A larger payload is left "
            + "undecoded and flagged. Raising the value lets a small hostile frame use more memory. All payloads of one packet "
            + "together are bounded by ws.max_derived_bytes_per_packet.")]
    private ulong _MaxDecompressedBytes;

    // Limit: most bytes bound to ONE packet in total when WebSocket binds an unmasked or inflated payload, all frames of the packet
    // together (they stay referenced until the packet is recycled, see _DispatchFramePayload); the per-payload limit above bounds
    // one inflated payload only. The packet keeps one sum for all protocols, so the bytes that another protocol or the TCP
    // transport bound count too (accepted: the total bounds the memory the packet pins).
    // Value: default 268435456 (256 MiB, four times the per-payload default), allowed 2048 (2 KiB) to 137438953472 (128 GiB), the
    // range of DerivedBytesLimit; a value outside the range is rejected by the settings layer and the default stays. Threat:
    // memory exhaustion by many inflated frames in one packet (up to 255 buffers of the per-payload limit each, deflate expands
    // about 1000 to 1, so about 1 MiB of small frames pins about 1 GiB).
    // Configurable (setting, changed by the operator or user), not a constant, because the right size depends on the host and
    // the traffic. Posture: secure by default, because the payloads come from untrusted peers, but relaxed on purpose (owner
    // decision): the same four-times rule as http.max_derived_bytes_per_packet. Raising it is opt-in. Rejected alternatives: the
    // per-payload setting doubling as the total (one knob cannot be both a per-item cap and a relaxed total: a cap
    // that suits one inflated payload refuses the second ordinary large frame of a packet), no total (the exposure above).
    // Exceeding it is an expected failure, not a truncation: the payload that would pass the total is not bound or parsed, the
    // packet reports a parse error that names this setting and says it is the total per packet, the caller ends the dispatch of
    // the later frames and still records their index groups. Unmasked, uncompressed payloads bind nothing and do not count.
    // Residual risk: the payload that fails the check was already inflated (at most the per-payload limit, freed at once); a
    // value near the maximum lets one packet pin that much memory.
    [U64Setting("ws.max_derived_bytes_per_packet", "Maximum unmasked and decompressed WebSocket bytes per packet (bytes)", "ws",
        Default = 268435456, HasMin = true, Min = 2048, HasMax = true, Max = 137438953472,
        Description = "Upper bound for all bytes bound to one packet when WebSocket binds an unmasked or decompressed payload, in bytes "
            + "(2 KiB to 128 GiB); the bytes of other protocols and the TCP PDUs of the packet count too. The payload that would pass it is not parsed further and the packet reports an error that names this "
            + "setting. A value below ws.max_decompressed_bytes also limits a single payload. Raising the value lets one packet "
            + "with many frames use more memory.")]
    private ulong _MaxDerivedBytesPerPacket;

    /// <summary>
    /// Decompresses a WebSocket per-message DEFLATE payload (RFC 7692 §7.2.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Algorithm.</b> RFC 7692 §7.2.2 requires that the receiver append the
    /// four-byte sequence <c>0x00 0x00 0xFF 0xFF</c> to the compressed payload before
    /// feeding it into the raw DEFLATE decompressor.  The sender strips these bytes (the empty stored block that a
    /// DEFLATE sync flush ends with, RFC 7692 §7.2.1), so appending them again completes the
    /// final block for the <see cref="DeflateStream"/>.</para>
    /// <para>The method reports failure through its status instead of propagating an exception — the callers
    /// (<see cref="_PopulateWebSocketFields"/> and the eager dispatch) treat anything but success as "no decompressed
    /// payload": the populator then adds a <c>websocket.payload.decompressed.error</c> field (or the
    /// <c>websocket.payload.decompressed.limit</c> field) for a non-empty payload and the dispatch keeps the compressed
    /// bytes, which is preferable to aborting the entire frame parse. An empty input is reported as a failure too.</para>
    /// <para><b>Limitations (stateless inflate).</b> Every call uses a fresh decompressor over one frame's payload.
    /// A message that is fragmented over several frames is therefore not concatenated before inflating, and a message
    /// that relies on the sliding window of earlier messages (context takeover, the default unless a
    /// <c>no_context_takeover</c> parameter is negotiated) may fail to inflate (a back reference into the earlier
    /// window is invalid for a fresh decompressor).</para>
    /// <para><b>Resource limit.</b> The decompressed size is bounded by <c>ws.max_decompressed_bytes</c> (see
    /// <see cref="_MaxDecompressedBytes"/> for the full limit record). Inflating is streamed through a bounded read buffer and
    /// stops at the first chunk that would pass the limit, so a decompression bomb costs at most the limit. The
    /// frame is inflated twice for a packet that is expanded (eager dispatch and populator); each pass is bounded.
    /// Thread-safe: no shared state.</para>
    /// </remarks>
    /// <param name="data">The (unmasked) payload of one frame with RSV1 set.</param>
    /// <param name="decompressed">The inflated bytes on success; empty otherwise.</param>
    /// <returns>
    /// <see cref="DecompressionStatus.Succeeded"/>; <see cref="DecompressionStatus.LimitExceeded"/> when the inflated size is
    /// above the limit (nothing is truncated or returned); <see cref="DecompressionStatus.Failed"/> when
    /// <paramref name="data"/> is empty or not valid DEFLATE.
    /// </returns>
    private DecompressionStatus _DecompressPermessageDeflate(ReadOnlyMemory<byte> data, out ReadOnlyMemory<byte> decompressed)
    {
        decompressed = default;
        if (data.Length == 0)
        {
            return DecompressionStatus.Failed;
        }

        // Append 0x00 0x00 0xFF 0xFF (the stripped sync-flush tail, RFC 7692 §7.2.2) before decompressing. A copy is
        // needed because the payload lives in packet memory that must not be modified or extended. The copy is as large as
        // the payload, which is bounded by the TCP segment.
        byte[] compressedWithTrailer = new byte[data.Length + 4];
        data.Span.CopyTo(compressedWithTrailer);
        compressedWithTrailer[data.Length] = 0x00;
        compressedWithTrailer[data.Length + 1] = 0x00;
        compressedWithTrailer[data.Length + 2] = 0xFF;
        compressedWithTrailer[data.Length + 3] = 0xFF;

        using IO.MemoryStream input = new(compressedWithTrailer, writable: false);
        using DeflateStream deflate = new(input, CompressionMode.Decompress, leaveOpen: true);
        return BoundedDecompressor.TryDecompress(deflate, _MaxDecompressedBytes, out decompressed);
    }

    #endregion
}
