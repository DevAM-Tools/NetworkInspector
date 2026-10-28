// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

// Connection state of HTTP/2: the HPACK decoder of each direction and the header blocks that are still waiting for their last
// CONTINUATION frame. Everything here is touched by the first parse of a packet only; later parses read the recorded result.
public sealed partial class Http2Protocol
{
    #region Types and state

    /// <summary>
    /// Upper bound for the header block fragments collected while a block waits for its END_HEADERS flag. Limit record: value
    /// 256 KiB per direction; threat: an endless chain of CONTINUATION frames (a known denial-of-service pattern against
    /// HTTP/2 endpoints) would otherwise grow the pending buffer with every frame; constant of the dissector (a memory bound,
    /// not configurable); posture: secure by default, since the data comes from untrusted peers. Exceeding it is not an error
    /// that stops the stream: the block in progress is dropped (its headers are not shown), the decoder state continues with
    /// the next block, and the connection stays readable.
    /// </summary>
    private const int _MaxPendingHeaderBlockBytes = 256 * 1024;

    /// <summary>The state of one direction of one connection.</summary>
    private sealed class DirectionState
    {
        /// <summary>The HPACK decoder (dynamic table) of the header blocks this direction sends.</summary>
        internal HpackDecoder Decoder { get; } = new();

        /// <summary>Fragments of a header block that has not seen END_HEADERS yet; null when no block is pending.</summary>
        internal ArrayBufferWriter<byte>? Pending { get; set; }

        /// <summary>The pending block exceeded <see cref="_MaxPendingHeaderBlockBytes"/>: its remaining fragments are discarded.</summary>
        internal bool PendingOverflow { get; set; }
    }

    /// <summary>
    /// The decoded headers of the frames of one packet layer, recorded on the first parse. <c>Blocks[i]</c> belongs to the
    /// i-th frame of the layer (after the preface); a missing or null entry means the frame shows no headers. The array is
    /// shorter than the frame count when the last frames have none.
    /// </summary>
    private readonly record struct Http2LayerEffect(HpackDecoder.Header[]?[] Blocks);

    /// <summary>
    /// Headers recorded per (packet, layer). Single ordered writer (the first parse), lock-free readers once an entry is
    /// published, the same contract as the TCP effect store. Its lifetime is the lifetime of this protocol instance.
    /// </summary>
    private readonly EffectStore<Http2LayerEffect> _Effects = new();

    /// <summary>
    /// Per-direction state keyed by <c>(tcp stream index &lt;&lt; 1) | direction</c>. Written and read by the first parse only
    /// (serialized by the session), never by a replay. A connection owns two entries for its lifetime: the connection ends
    /// when the protocol instance does, the same lifetime rule as the TCP tracker, and the number of connections is bounded
    /// by <c>tcp.max_tracked_streams</c> (a connection that TCP did not track has no stream identity and no entry).
    /// </summary>
    private readonly Dictionary<ulong, DirectionState> _Directions = [];

    #endregion

    #region Header block resolution

    /// <summary>
    /// Returns the decoded headers of the frames in this layer: decodes them against the connection state on the first parse
    /// and records them, or returns the recorded ones on a replay.
    /// </summary>
    /// <param name="parentField">The TCP layer's field, used for the packet identity and the layer key.</param>
    /// <param name="data">The data of this layer (the bound buffer of the PDU).</param>
    /// <param name="prefaceLength">Length of the connection preface at the start of <paramref name="data"/>, or 0.</param>
    /// <returns>
    /// The per-frame headers, or <see langword="null"/> when no connection state is available (no enclosing TCP stream, or no
    /// recorded entry on a replay), in which case the caller decodes every block with a fresh decoder.
    /// </returns>
    private HpackDecoder.Header[]?[]? _ResolveHeaderBlocks(in MutField parentField, ReadOnlyMemory<byte> data, int prefaceLength)
    {
        PacketId packetId = parentField.Packet.Id;
        if (!TcpProtocol.TryGetCurrentStream(packetId, out uint stream, out bool forward, out bool isReplay)
            || !parentField.TryGetEffectLayerKey(data, out int layerKey))
        {
            return null;
        }

        if (isReplay)
        {
            return _Effects.TryGet(packetId.Value, layerKey, out Http2LayerEffect recorded)
                ? recorded.Blocks
                : null;
        }

        ulong key = ((ulong)stream << 1) | (forward ? 1UL : 0UL);
        if (!_Directions.TryGetValue(key, out DirectionState? direction))
        {
            direction = new();
            _Directions[key] = direction;
        }

        HpackDecoder.Header[]?[] blocks = _DecodeFrames(direction, data.Span[prefaceLength..]);
        _Effects.Record(packetId.Value, layerKey, new Http2LayerEffect(blocks));
        return blocks;
    }

    /// <summary>
    /// Decodes the header blocks of every HEADERS and CONTINUATION frame in <paramref name="span"/>, in frame order, against
    /// the direction's decoder.
    /// </summary>
    /// <remarks>
    /// A frame whose payload is cut off (the plain per-segment path, where reassembly was not possible) is decoded with a
    /// throw-away decoder: its fragment is incomplete, and feeding it to the connection's decoder would corrupt the dynamic
    /// table for every later block.
    /// </remarks>
    private static HpackDecoder.Header[]?[] _DecodeFrames(DirectionState direction, ReadOnlySpan<byte> span)
    {
        List<HpackDecoder.Header[]?>? blocks = null;
        int ordinal = 0;
        int offset = 0;
        while (offset + Http2FrameHeader.Size <= span.Length && Http2FrameHeader.TryParse(span[offset..], out Http2FrameHeader frame))
        {
            // The same clamping as the field walk: the declared length comes from the wire and never sizes anything.
            int payloadStart = offset + Http2FrameHeader.Size;
            int payloadEnd = Math.Min(payloadStart + frame.Length, span.Length);
            bool complete = payloadStart + frame.Length <= span.Length;

            if (frame.Type is 1 or 9)
            {
                HpackDecoder.Header[]? headers = _DecodeHeaderFrame(direction, frame, span[payloadStart..payloadEnd], complete);
                if (headers is not null)
                {
                    blocks ??= [];
                    while (blocks.Count < ordinal)
                    {
                        blocks.Add(null);
                    }

                    blocks.Add(headers);
                }
            }

            offset = payloadEnd;
            ordinal++;
        }

        return blocks is null
            ? []
            : [.. blocks];
    }

    /// <summary>
    /// Decodes one HEADERS or CONTINUATION frame. A block that is not finished by this frame (no END_HEADERS) is collected,
    /// and its headers are returned by the frame that finishes it.
    /// </summary>
    /// <returns>The headers to show on this frame, or <see langword="null"/> when it shows none.</returns>
    private static HpackDecoder.Header[]? _DecodeHeaderFrame(
        DirectionState direction, Http2FrameHeader frame, ReadOnlySpan<byte> payload, bool complete)
    {
        if (!_TryGetHeaderBlockFragment(frame.Type, frame.Flags, payload, out ReadOnlySpan<byte> fragment))
        {
            return null;
        }

        if (!complete)
        {
            return _ToArrayOrNull(new HpackDecoder().Decode(fragment));
        }

        bool endHeaders = (frame.Flags & 0x04) != 0;

        // A HEADERS frame starts a block; an earlier block that never saw its END_HEADERS is abandoned (the peer broke the
        // protocol or the capture lost frames), so a stale fragment cannot be glued to the new block.
        if (frame.Type == 1)
        {
            _ClearPending(direction);
            if (endHeaders)
            {
                return _ToArrayOrNull(direction.Decoder.Decode(fragment));
            }

            _StartPending(direction, fragment);
            return null;
        }

        // CONTINUATION without a pending block (the HEADERS frame is not in the capture): the fragment can only be decoded
        // on its own, and only when it is the last one; an unfinished orphan fragment has nothing to attach to.
        if (direction.Pending is null)
        {
            return endHeaders
                ? _ToArrayOrNull(direction.Decoder.Decode(fragment))
                : null;
        }

        _AppendPending(direction, fragment);
        if (!endHeaders)
        {
            return null;
        }

        HpackDecoder.Header[]? finished = direction.PendingOverflow
            ? null
            : _ToArrayOrNull(direction.Decoder.Decode(direction.Pending.WrittenSpan));
        _ClearPending(direction);
        return finished;
    }

    private static void _StartPending(DirectionState direction, ReadOnlySpan<byte> fragment)
    {
        direction.Pending ??= new ArrayBufferWriter<byte>(Math.Max(fragment.Length * 2, 256));
        direction.Pending.ResetWrittenCount();
        direction.PendingOverflow = false;
        _AppendPending(direction, fragment);
    }

    private static void _AppendPending(DirectionState direction, ReadOnlySpan<byte> fragment)
    {
        if (direction.PendingOverflow)
        {
            return;
        }

        // The pending size never exceeds the cap (this check enforces it) and a fragment is at most one frame (2^24 - 1), so
        // the sum cannot overflow an int.
        if (direction.Pending!.WrittenCount + fragment.Length > _MaxPendingHeaderBlockBytes)
        {
            direction.PendingOverflow = true;
            return;
        }

        direction.Pending.Write(fragment);
    }

    private static void _ClearPending(DirectionState direction)
    {
        direction.Pending = null;
        direction.PendingOverflow = false;
    }

    private static HpackDecoder.Header[]? _ToArrayOrNull(List<HpackDecoder.Header> headers) =>
        headers.Count == 0
            ? null
            : [.. headers];

    #endregion

    #region Header block layout

    /// <summary>
    /// Finds the header block fragment inside the payload of a HEADERS or CONTINUATION frame.
    /// </summary>
    /// <remarks>
    /// Layout (RFC 7540 section 6.2): <c>[Pad Length (1, if PADDED)] [Stream Dependency (4) + Weight (1), if PRIORITY]
    /// Header Block Fragment [Padding (Pad Length), if PADDED]</c>. CONTINUATION frames (section 6.10) carry only the
    /// fragment. One definition for the index detection, the field walk and the connection decoding, so the three can never
    /// disagree about where the fragment is.
    /// </remarks>
    /// <param name="frameType">1 for HEADERS, 9 for CONTINUATION.</param>
    /// <param name="flags">The frame flags.</param>
    /// <param name="payload">The frame payload present in the data (possibly cut off).</param>
    /// <param name="fragment">The header block fragment (empty when the payload holds only the optional fields).</param>
    /// <returns><see langword="false"/> when the optional fields do not fit the payload (a malformed frame).</returns>
    private static bool _TryGetHeaderBlockFragment(byte frameType, byte flags, ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> fragment)
    {
        fragment = default;
        int start = 0;
        int padLength = 0;

        if (frameType == 1)
        {
            bool padded = (flags & 0x08) != 0;
            bool priority = (flags & 0x20) != 0;

            if (padded)
            {
                if (payload.Length < 1)
                {
                    return false;
                }

                padLength = payload[0];
                start += 1;
            }

            if (priority)
            {
                // Stream dependency (31 bits plus the exclusive flag) and weight are skipped, not shown; the 5 bytes must be
                // skipped so the fragment starts at the right place.
                if (payload.Length < start + 5)
                {
                    return false;
                }

                start += 5;
            }

            // Padding is appended at the end of the payload and excluded from the fragment. Declared padding that exceeds the
            // remaining payload is malformed: refuse to decode instead of slicing out of range.
            if (padLength > 0 && payload.Length - start < padLength)
            {
                return false;
            }
        }

        int end = payload.Length - padLength;
        fragment = start < end
            ? payload[start..end]
            : default;
        return true;
    }

    #endregion
}
