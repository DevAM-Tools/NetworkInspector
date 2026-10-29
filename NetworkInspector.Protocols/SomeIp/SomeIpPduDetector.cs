// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Finds the boundaries of SOME/IP messages in a TCP byte stream. A stream has no datagram borders, so the length field of the
/// header is the only framing: a message is complete when <c>8 + length</c> bytes are buffered.
/// <para>
/// The detector is called by the stream buffer on the contiguous, in-order prefix of the stream, at the position where the next
/// message must start. It answers one of three things: <em>incomplete</em> (the header or the rest of the message has not
/// arrived), <em>complete</em> (with the total size), or <em>invalid</em> (the bytes here cannot begin a message, so the stream
/// lost its position and the resynchronization heuristic runs).
/// </para>
/// <para>
/// <b>Security:</b> the length field is untrusted 32-bit input. A hostile value would make the stream buffer wait for, and
/// reserve space for, a message that never arrives. The limit <c>someip.tcp_max_message_size</c> (default 16 MiB, secure by
/// default, configurable by the user) keeps such a header from ever being framed. Rejected alternative: clamping the length to the
/// limit, which would frame the stream at a position that is not a message border and desynchronize it silently.
/// </para>
/// <para><b>Thread-safety:</b> immutable after construction; stateless, so one instance serves every stream and every thread.</para>
/// </summary>
/// <param name="checkPlausibility">
/// Whether a header must look like a header (protocol version 1, a defined base type) to start a message. On, a stream that
/// lost its position is noticed at once; off, only the length rules apply and the first byte pattern is framed by its length.
/// </param>
/// <param name="maxMessageSize">The largest message in bytes, header included (<c>someip.tcp_max_message_size</c>).</param>
internal sealed class SomeIpPduDetector(bool checkPlausibility, int maxMessageSize) : IPduBoundaryDetector
{
    /// <inheritdoc/>
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        // The length field alone is not enough to judge plausibility (the version and the type sit behind it), so a judgement
        // waits for the whole header. A stream that ends inside a header is handed out as a short tail by the stream buffer.
        if (data.Length < SomeIpHeader.Size)
        {
            return PduBoundaryResult.Incomplete;
        }

        SomeIpHeader header = SomeIpHeader.Parse(data);

        // Lost synchronization: an implausible header means the stream position is inside a message, not at one.
        if (checkPlausibility && !header.IsPlausible)
        {
            return PduBoundaryResult.Invalid;
        }

        // A length below 8 cannot be a message even without the plausibility check: the message would not cover its own header.
        if (header.Length < SomeIpHeader.MinimumLength)
        {
            return PduBoundaryResult.Invalid;
        }

        // Limit someip.tcp_max_message_size. The sum is formed in long because the declared length can be 0xFFFFFFFF. A message
        // above the limit is never framed: the detector keeps answering "incomplete", the stream buffer fills until its own limit
        // (the message limit plus headroom, see the registration) is exceeded, and the buffer then drops the message in progress and
        // the TCP layer reports it as a reassembly limit. Rejected alternative: answering "invalid", which sends the stream to
        // resynchronization and, when nothing plausible is buffered yet, into its error state, where nothing is reported at all.
        long total = SomeIpHeader.LengthPrefixSize + (long)header.Length;
        if (total > maxMessageSize)
        {
            return PduBoundaryResult.Incomplete;
        }

        return data.Length >= total
            ? PduBoundaryResult.Complete((int)total)
            : PduBoundaryResult.Incomplete;
    }
}

/// <summary>
/// Finds the next SOME/IP header in a TCP stream that lost its position. It scans forward for the first offset whose 16 bytes form
/// a plausible header whose declared message fits the size limit, and reports how many bytes to discard.
/// <para>
/// The scan starts at offset 1: the detector just rejected the bytes at offset 0, so a hit there is impossible, and the skip is
/// therefore at least one byte (the stream buffer needs progress). The scan is linear in the buffered bytes and runs once per
/// loss of position, not per packet. A stream with no plausible header in the buffered bytes fails the resynchronization, and the
/// stream enters its error state; the user sees that by name instead of a silently dropped stream.
/// </para>
/// <para><b>Thread-safety:</b> immutable after construction; stateless.</para>
/// </summary>
/// <param name="maxMessageSize">The largest message in bytes, header included; a candidate that announces more is not a sync point.</param>
internal sealed class SomeIpResyncHeuristic(int maxMessageSize) : IResyncHeuristic
{
    /// <inheritdoc/>
    public ResyncResult Resync(ReadOnlySpan<byte> data)
    {
        // The last offset that can still hold a complete header is Length - 16, inclusive.
        for (int offset = 1; offset + SomeIpHeader.Size <= data.Length; offset++)
        {
            SomeIpHeader candidate = SomeIpHeader.Parse(data[offset..]);
            if (candidate.IsPlausible && SomeIpHeader.LengthPrefixSize + (long)candidate.Length <= maxMessageSize)
            {
                return ResyncResult.Skip(offset);
            }
        }

        return ResyncResult.Failure;
    }
}
