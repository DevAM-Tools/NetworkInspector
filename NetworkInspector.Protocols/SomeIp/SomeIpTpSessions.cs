// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Maps SOME/IP-TP onto the generic Core reassembler: it chooses the options that make the generic reassembler behave like
/// SOME/IP-TP, builds the key, and passes the segments on. It owns no buffer, no eviction rule and no limit logic; those live in
/// <see cref="FragmentReassembler{TKey}"/> once, for every fragmenting protocol.
/// <para>
/// What is SOME/IP-specific is the set of option values and the wording of the diagnostics (written by the protocol, which sees
/// the outcome):
/// </para>
/// <list type="bullet">
/// <item><description><b>Alignment 16.</b> The offset field counts 16-byte units, so every segment but the last carries a multiple of
/// 16 bytes.</description></item>
/// <item><description><b>First bytes win on overlap.</b> A deterministic, replay-stable choice; differing overlapping bytes are
/// reported as a conflict. The previous parser let the later bytes win, which depends on arrival order in a way that a re-parse
/// cannot reproduce.</description></item>
/// <item><description><b>Restart on offset 0.</b> A sender that restarts a transfer begins at offset 0 again, so a new segment at 0
/// replaces the old partial transfer instead of piling onto it.</description></item>
/// <item><description><b>Least recently used eviction.</b> An active long transfer survives a burst of short ones; evicting the oldest
/// created session would drop exactly the transfer that is still making progress.</description></item>
/// <item><description><b>Prefix, then complete.</b> The last segment fixes the length. If a hole remains, the decodable prefix is
/// delivered once; the session stays open, and a late segment that closes the hole delivers the full message. The previous parser
/// closed the session at the last segment, which lost every segment that arrived out of order behind it.</description></item>
/// </list>
/// <para>
/// <b>Thread-safety:</b> single writer. Only the ordered first parse of the protocol calls <see cref="Add"/> (guarded by the ingest
/// gate); a re-parse replays the outcome recorded in the message effect and never reaches this type. No lock, because there is no
/// second caller to exclude.
/// </para>
/// </summary>
internal sealed class SomeIpTpSessions
{
    private readonly FragmentReassembler<SomeIpTpKey> _Reassembler;

    /// <summary>
    /// Creates the adapter. The limits come from the settings, which the settings manager has already bounded; the Core options
    /// validate again, so a violated precondition is reported by name.
    /// </summary>
    /// <param name="settings">The effective SOME/IP settings.</param>
    internal SomeIpTpSessions(in SomeIpSettings settings)
    {
        FragmentReassemblyOptions options = new()
        {
            // Limit someip.tp_max_sessions: sessions that never complete hold fragment copies forever.
            MaxPending = settings.TpMaxSessions,

            // Limit someip.tp_max_buffered_bytes: the session and size limits multiply, this one bounds the product.
            MaxBufferedBytes = settings.TpMaxBufferedBytes,

            // Limit someip.tp_max_message_size, measured as the extent (offset plus length) so that one segment at a huge
            // offset cannot ask for a huge buffer.
            MaxDatagramLength = settings.TpMaxMessageSize,

            // The number of segments is bounded by the extent and the 16-byte alignment (length / 16), so a separate bound would
            // only be a second, tighter-looking number for the same limit.
            MaxFragments = FragmentReassemblyOptions.Unbounded,
            NonLastAlignment = SomeIpTables.TpSegmentAlignment,
            OverlapPolicy = FragmentOverlapPolicy.KeepFirst,
            EvictionPolicy = FragmentEvictionPolicy.LeastRecentlyUsed,
            RestartOnZeroOffset = true,
            GapPolicy = FragmentGapPolicy.PrefixOnLastThenComplete,
        };
        _Reassembler = new FragmentReassembler<SomeIpTpKey>(in options);
    }

    /// <summary>Number of open sessions. Owner thread only.</summary>
    internal int PendingCount => _Reassembler.PendingCount;

    /// <summary>Bytes held by all open sessions. Owner thread only.</summary>
    internal long BufferedBytes => _Reassembler.BufferedBytes;

    /// <summary>
    /// Adds one segment. The contributor is the packet id: the Core reassembler keeps it per kept segment and returns the ids in
    /// offset order with a delivering outcome, which is the segment list of the delivering packet.
    /// </summary>
    /// <param name="key">The identity of the message.</param>
    /// <param name="byteOffset">The offset of the segment in bytes (28 bits of 16-byte units, so a multiple of 16 below 2^32).</param>
    /// <param name="data">The segment's payload behind the TP word; copied by the reassembler, not kept.</param>
    /// <param name="moreSegments">Whether further segments follow.</param>
    /// <param name="packetId">The id of the packet that carries the segment.</param>
    /// <returns>The outcome; immutable, safe to store in an effect.</returns>
    internal FragmentOutcome Add(in SomeIpTpKey key, uint byteOffset, ReadOnlySpan<byte> data, bool moreSegments, int packetId) =>
        _Reassembler.Add(in key, byteOffset, moreSegments, data, packetId);

    /// <summary>Drops every open session. Owner thread only.</summary>
    internal void Clear() => _Reassembler.Clear();
}
