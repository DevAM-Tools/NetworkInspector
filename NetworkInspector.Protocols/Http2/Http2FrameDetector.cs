// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// Finds one HTTP/2 unit in a reassembled TCP byte stream: the 24-octet client connection preface as a unit of its own, then
/// one frame (9-octet header plus payload) per unit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> frames are cut by TCP segmentation, not by HTTP/2. A DATA frame of 16 KiB covers a dozen segments
/// at an MSS of 1460; without framing the first segment showed the frame header and 1451 bytes, and the others showed "frames"
/// whose header was body bytes. With one frame per unit, the HTTP/2 parser sees complete frames and the connection-level
/// HPACK state can follow the header blocks in order.
/// </para>
/// <para>
/// <b>Plausibility (security and robustness):</b> the detector is registered on a port that usually carries TLS (8443) and
/// is selected by content for cleartext prior-knowledge traffic. Encrypted bytes must not be mistaken for a frame whose
/// "length" is megabytes, because the stream would then hold bytes until its buffer limit. A header is therefore accepted only
/// when its type is one the format defines and its payload length does not exceed the frame size limit that applies to the
/// direction: 16384 octets until the peer announces another value in a SETTINGS frame (RFC 9113 section 4.2, 6.5.2). An
/// implausible header is reported as <see cref="PduBoundaryResult.Invalid"/>, which stops reassembly for the connection and
/// leaves the bytes to the plain per-segment parser (the behavior before reassembly existed), instead of buffering them.
/// </para>
/// <para>
/// <b>Per-stream state:</b> only the announced frame size limits, and only for streams that sent a SETTINGS frame with
/// SETTINGS_MAX_FRAME_SIZE. A SETTINGS frame sent by one endpoint states the largest frame the OTHER endpoint may send to it,
/// so the value is stored for the opposite direction. The state is written when a SETTINGS frame is reported complete (not on
/// the repeated calls of an incomplete one) and dropped by <see cref="ResetStream"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The per-stream state is mutated by <c>Detect</c>, so one owner thread must drive one
/// instance (the contract of <see cref="IStreamPduBoundaryDetector"/>); a stack builds its own instance and parses packets on
/// one ordered thread.
/// </para>
/// </remarks>
internal sealed class Http2FrameDetector : IStreamPduBoundaryDetector
{
    #region Constants

    /// <summary>
    /// Frame payload size every endpoint must accept, 2^14 octets (RFC 9113 section 4.2); the limit that applies to a
    /// direction until its receiver announces another one. A protocol fact, so a constant.
    /// </summary>
    internal const int DefaultMaxFrameSize = 16384;

    /// <summary>The largest frame payload the format allows, 2^24 - 1 octets (RFC 9113 section 4.2): a protocol fact.</summary>
    internal const int AbsoluteMaxFrameSize = 16_777_215;

    private const byte _TypeSettings = 4;
    private const byte _FlagAck = 0x01;
    private const ushort _SettingMaxFrameSize = 5;

    #endregion

    #region Fields

    /// <summary>
    /// Announced frame size limit per stream and direction, only for streams that announced one. Index 0 is the forward
    /// direction's limit (frames SENT by the forward endpoint), index 1 the reverse direction's.
    /// </summary>
    private readonly Dictionary<ulong, int[]> _Limits = [];

    #endregion

    #region Detect

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data) => _Detect(data, DefaultMaxFrameSize, out _);

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data, in StreamDetectionContext context)
    {
        int limit = DefaultMaxFrameSize;
        if (_Limits.TryGetValue(context.StreamId, out int[]? announced))
        {
            limit = announced[context.IsForward ? 0 : 1];
        }

        PduBoundaryResult result = _Detect(data, limit, out int announcedMaxFrameSize);

        // A completed SETTINGS frame that announces a frame size limit applies to the frames the OTHER endpoint sends.
        if (result.IsComplete && announcedMaxFrameSize > 0)
        {
            if (!_Limits.TryGetValue(context.StreamId, out int[]? stored))
            {
                stored = [DefaultMaxFrameSize, DefaultMaxFrameSize];
                _Limits[context.StreamId] = stored;
            }

            stored[context.IsForward ? 1 : 0] = announcedMaxFrameSize;
        }

        return result;
    }

    /// <inheritdoc />
    public void ResetStream(ulong streamId)
    {
        // Idempotent: removing an unknown id does nothing.
        _Limits.Remove(streamId);
    }

    /// <summary>
    /// Frames the unit at the start of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The buffered prefix.</param>
    /// <param name="maxFrameSize">Largest plausible frame payload for this direction.</param>
    /// <param name="announcedMaxFrameSize">
    /// For a complete SETTINGS frame with a SETTINGS_MAX_FRAME_SIZE entry: the announced value (clamped to the legal range);
    /// otherwise 0.
    /// </param>
    private static PduBoundaryResult _Detect(ReadOnlySpan<byte> data, int maxFrameSize, out int announcedMaxFrameSize)
    {
        announcedMaxFrameSize = 0;

        // The client connection preface is a unit of its own: it is not a frame (reading it as one would give a length of
        // 0x505249). A buffer that is still a proper prefix of it must wait for the rest, not be read as a frame header.
        ReadOnlySpan<byte> preface = Http2Protocol.ConnectionPreface;
        if (data.StartsWith(preface))
        {
            return PduBoundaryResult.Complete(preface.Length);
        }

        if (data.Length < preface.Length && preface.StartsWith(data))
        {
            return PduBoundaryResult.Incomplete;
        }

        if (data.Length < Http2FrameHeader.Size)
        {
            return PduBoundaryResult.Incomplete;
        }

        // Plausibility of the header, see the class remarks: a defined frame type and a payload within the limit.
        int length = (data[0] << 16) | (data[1] << 8) | data[2];
        byte type = data[3];
        if (!_IsKnownType(type) || length > maxFrameSize)
        {
            return PduBoundaryResult.Invalid;
        }

        int total = Http2FrameHeader.Size + length;
        if (total > data.Length)
        {
            return PduBoundaryResult.Incomplete;
        }

        if (type == _TypeSettings && (data[4] & _FlagAck) == 0)
        {
            announcedMaxFrameSize = _FindMaxFrameSize(data.Slice(Http2FrameHeader.Size, length));
        }

        return PduBoundaryResult.Complete(total);
    }

    /// <summary>
    /// Reads the SETTINGS_MAX_FRAME_SIZE entry of a SETTINGS payload (6-octet entries: 16-bit identifier, 32-bit value).
    /// </summary>
    /// <returns>The value clamped to the legal range 16384..16777215, or 0 when the frame has no such entry.</returns>
    private static int _FindMaxFrameSize(ReadOnlySpan<byte> payload)
    {
        int result = 0;
        for (int position = 0; position + 6 <= payload.Length; position += 6)
        {
            ushort identifier = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]);
            if (identifier != _SettingMaxFrameSize)
            {
                continue;
            }

            // The last entry wins (RFC 9113 section 6.5.3). The value comes from the wire: clamped to the legal range, so a
            // hostile value can neither lower the limit below the default nor raise it above the format maximum.
            uint value = BinaryPrimitives.ReadUInt32BigEndian(payload[(position + 2)..]);
            result = (int)Math.Clamp(value, DefaultMaxFrameSize, AbsoluteMaxFrameSize);
        }

        return result;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a frame type the format defines: DATA..CONTINUATION (0-9), ALTSVC (10), ORIGIN (12)
    /// and PRIORITY_UPDATE (16). Unknown types are legal on the wire (receivers ignore them) but are what encrypted bytes
    /// look like, so the detector treats them as "not HTTP/2" (see the class remarks).
    /// </summary>
    private static bool _IsKnownType(byte type) => type is <= 10 or 12 or 16;

    #endregion
}
