// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Finds one TLS unit in a reassembled TCP byte stream: a single record, or the run of handshake records that completes a
/// handshake message which started in the first record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> TLS records are cut by the TCP segmentation, not by the TLS layer. A server certificate chain of a
/// few kilobytes spans several segments, and a handshake message may even span several records (RFC 8446 section 5.1: the
/// 24-bit handshake length is independent of the 16 KiB record limit). The TLS parser works on complete units, so without
/// framing every record that continues in the next segment, and everything that follows it, was dropped, and the server
/// name, ALPN and certificates of most real captures were invisible.
/// </para>
/// <para>
/// <b>What a unit is:</b> a non-handshake record (application data, alert, change cipher spec, heartbeat) is a unit of its
/// own. A handshake record ends the unit when every handshake message in it is complete at the record end; otherwise the
/// unit extends over the following records until a record ends exactly at a message boundary. A handshake header (4 bytes)
/// that is itself split by a record boundary is handled the same way as a split body.
/// </para>
/// <para>
/// <b>Cost:</b> stateless; the whole buffered prefix is walked again on every call. The walk reads only record headers and
/// handshake headers (a few bytes per record), and a unit is bounded by the reassembly <c>MaxPduSize</c> of the registration, so
/// the repeated walk is cheap compared with the segment copy the stream buffer makes. Only the prefix of the current unit is
/// walked: the walk stops at the first record that ends a unit.
/// </para>
/// <para><b>Thread safety:</b> stateless; safe to share.</para>
/// </remarks>
internal sealed class TlsRecordDetector : IPduBoundaryDetector
{
    #region Constants

    /// <summary>Size of the record header in bytes (content type, version, length): a format fact of TLS.</summary>
    private const int _RecordHeaderSize = 5;

    /// <summary>Size of the handshake message header in bytes (type and 24-bit length): a format fact of TLS.</summary>
    private const int _HandshakeHeaderSize = 4;

    /// <summary>
    /// Largest legal TLSCiphertext fragment: 2^14 plus 2048 bytes of expansion (RFC 5246 section 6.2.3). A format fact, so a
    /// constant; a record that announces more is not TLS, which is what makes <see cref="PduBoundaryResult.Invalid"/> safe.
    /// </summary>
    private const int _MaxFragment = 16384 + 2048;

    /// <summary>Content type of a handshake record.</summary>
    private const byte _ContentTypeHandshake = 22;

    #endregion

    #region Detect

    /// <inheritdoc />
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        UnitState state = TryGetUnit(data, 0, out int unitEnd, out _);
        return state switch
        {
            UnitState.Complete => PduBoundaryResult.Complete(unitEnd),
            UnitState.Incomplete => PduBoundaryResult.Incomplete,
            _ => PduBoundaryResult.Invalid,
        };
    }

    /// <summary>The outcome of walking one unit.</summary>
    internal enum UnitState
    {
        /// <summary>The unit ends at the returned end offset.</summary>
        Complete,

        /// <summary>More bytes are needed to finish the unit (or even its first record header).</summary>
        Incomplete,

        /// <summary>The bytes at the start cannot be a TLS record.</summary>
        Invalid,
    }

    /// <summary>
    /// Walks the unit that starts at <paramref name="start"/>: one record, or the run of handshake records that completes a
    /// handshake message.
    /// </summary>
    /// <remarks>
    /// Shared by the boundary detector and by the TLS parser, so the parser decides exactly where a message that spans
    /// records is joined (the parser must join the same records the detector grouped). Allocation-free: it reads spans only.
    /// </remarks>
    /// <param name="data">Bytes that contain the unit.</param>
    /// <param name="start">Offset of the first record header of the unit.</param>
    /// <param name="unitEnd">Offset after the last record of the unit when the result is <see cref="UnitState.Complete"/>.</param>
    /// <param name="recordCount">Number of records in the unit when the result is <see cref="UnitState.Complete"/>.</param>
    /// <returns>Whether the unit is complete, needs more bytes, or is not TLS.</returns>
    internal static UnitState TryGetUnit(ReadOnlySpan<byte> data, int start, out int unitEnd, out int recordCount)
    {
        unitEnd = start;
        recordCount = 0;
        int offset = start;

        // Where the handshake message walk stands after the records walked so far. Not at a message boundary at a record
        // boundary means a message continues in the next record, so the unit cannot end there.
        HandshakeWalk walk = default;

        while (true)
        {
            // A header that is not fully buffered cannot be judged yet. At the start of a unit that is "need more bytes";
            // inside a unit that must still be completed it is the same.
            if (data.Length - offset < _RecordHeaderSize)
            {
                return UnitState.Incomplete;
            }

            byte contentType = data[offset];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]);
            bool plausible = contentType is >= 20 and <= 24 && data[offset + 1] == 3 && data[offset + 2] <= 4 && length <= _MaxFragment;
            if (!plausible)
            {
                // At the start of a unit the bytes are not TLS at all. Inside a unit (a message that should continue in
                // another handshake record) a broken continuation also invalidates the unit.
                return UnitState.Invalid;
            }

            int recordEnd = offset + _RecordHeaderSize + length;
            if (recordEnd > data.Length)
            {
                return UnitState.Incomplete;
            }

            if (contentType != _ContentTypeHandshake)
            {
                // A non-handshake record cannot continue a handshake message (RFC 8446 section 5.1): the unit is invalid when
                // a message was still pending, and this record alone otherwise.
                if (!walk.AtMessageBoundary)
                {
                    return UnitState.Invalid;
                }

                unitEnd = recordEnd;
                recordCount = 1;
                return UnitState.Complete;
            }

            walk.Feed(data[(offset + _RecordHeaderSize)..recordEnd]);
            offset = recordEnd;
            recordCount++;
            if (walk.AtMessageBoundary)
            {
                unitEnd = offset;
                return UnitState.Complete;
            }
        }
    }

    #endregion

    #region Handshake message walk

    /// <summary>
    /// Follows the handshake messages across record payloads without copying them: it knows whether the walk currently stands
    /// between two messages, inside a message body, or inside a message header that a record boundary split.
    /// </summary>
    /// <remarks>
    /// Why a state and not just "bytes still missing": the 4-byte handshake header (type and 24-bit length) can itself be cut
    /// by a record boundary. The length is only known once all four bytes are seen, so the bytes of a split header are kept
    /// (at most three) until the next payload completes it. Value type, so the walk allocates nothing.
    /// </remarks>
    private struct HandshakeWalk
    {
        /// <summary>Bytes of a split header seen so far, shifted in one by one (valid for <see cref="_headerHave"/> bytes).</summary>
        private int _header;

        /// <summary>How many of the four header bytes were seen (0 when the walk is not inside a header).</summary>
        private int _headerHave;

        /// <summary>Bytes of the current message body that the following payloads must still supply.</summary>
        private long _bodyRemaining;

        /// <summary>The walk stands exactly between two messages (or has not started): a unit may end here.</summary>
        internal readonly bool AtMessageBoundary => _headerHave == 0 && _bodyRemaining == 0;

        /// <summary>Walks one record payload; state carries over to the next payload.</summary>
        /// <param name="payload">Payload of one handshake record.</param>
        internal void Feed(ReadOnlySpan<byte> payload)
        {
            int position = 0;
            while (position < payload.Length)
            {
                // Inside a message body: skip as much of it as this payload holds.
                if (_bodyRemaining > 0)
                {
                    long take = Math.Min(_bodyRemaining, payload.Length - position);
                    _bodyRemaining -= take;
                    position += (int)take;
                    continue;
                }

                // Between messages or inside a split header: collect header bytes until all four are known.
                while (_headerHave < _HandshakeHeaderSize && position < payload.Length)
                {
                    _header = (_header << 8) | payload[position++];
                    _headerHave++;
                }

                if (_headerHave < _HandshakeHeaderSize)
                {
                    return; // The header continues in the next record.
                }

                // The low 24 bits of the header are the body length (the high byte is the message type).
                _bodyRemaining = _header & 0x00FF_FFFF;
                _header = 0;
                _headerHave = 0;
            }
        }
    }

    #endregion
}
