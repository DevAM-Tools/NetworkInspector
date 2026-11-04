// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// The wording and the range of the per-packet total of derived bytes that HTTP, WebSocket, and SOME/IP-TP each apply.
/// <para>
/// Limit: the most bytes one protocol may bind to one packet as buffers it derived from untrusted input (decoded HTTP bodies,
/// unmasked or inflated WebSocket payloads, reassembled SOME/IP-TP messages). The bound buffers stay referenced until the
/// packet is recycled and one packet can complete many messages, so the per-item limit of a protocol alone does not bound the
/// sum. Each protocol owns its own total and its own setting, relaxed by default (a multiple of its per-item limit) and
/// configurable by the operator or user; the packet keeps one sum of all bound bytes (<see cref="MutField.BoundBytes"/>), the
/// protocol applies its total to that sum (<see cref="TryBind"/>) and names the setting in the error. The sum counts the bytes of
/// every protocol that bound to the packet, so a total bounds the memory the packet pins when that protocol binds. Secure by
/// default, because the bytes come from untrusted peers; raising a total is opt-in.
/// </para>
/// <para>
/// This type keeps the three protocols consistent: one sentence pattern for the parse error, so a user who knows one limit
/// recognizes the others, and one definition of the range shared by the settings of HTTP and WebSocket (which declare their
/// settings with attributes and therefore repeat the numbers in a comment next to them: <see cref="MinimumBytes"/> and
/// <see cref="MaximumBytes"/> are the values those declarations use, and a test pins them).
/// </para>
/// <para><b>Thread-safety:</b> stateless static helpers.</para>
/// </summary>
internal static class DerivedBytesLimit
{
    /// <summary>
    /// Smallest value of the HTTP and WebSocket totals in bytes (2 KiB, above the per-item minimum of 1 KiB). Constant: a
    /// range of a setting, not a protocol fact; the total counts every byte bound to the packet, the TCP PDU of a message
    /// included, so a smaller minimum could refuse every body (the PDU alone would pass it).
    /// </summary>
    internal const ulong MinimumBytes = 2048UL;

    /// <summary>
    /// Largest value of the HTTP and WebSocket totals in bytes (128 GiB, the same maximum as the per-item limits). Constant:
    /// a range of a setting; a total above the host's memory is the operator's explicit choice.
    /// </summary>
    internal const ulong MaximumBytes = 137_438_953_472UL;

    /// <summary>
    /// Binds bytes that a protocol derived from untrusted input (decoded, unmasked, inflated, reassembled) unless they would take
    /// the bytes bound to the packet past the protocol's total.
    /// </summary>
    /// <param name="container">The cursor the protocol was called with; the packet it belongs to receives the buffer.</param>
    /// <param name="bytes">Owned bytes that outlive this parse (see <see cref="MutField.TryBindBuffer"/>).</param>
    /// <param name="totalLimit">The most bytes that may be bound to the packet in all, inclusive; the protocol's setting.</param>
    /// <param name="bound">The stored slice to hand to the nested parse; empty when the method returns <see langword="false"/>.</param>
    /// <param name="failure">
    /// <see cref="DerivedBytesFailure.TotalExceeded"/> when the total would be passed, <see cref="DerivedBytesFailure.TableFull"/>
    /// when the packet takes no further buffer, else <see cref="DerivedBytesFailure.None"/>.
    /// </param>
    /// <returns><see langword="true"/> when the bytes were bound; a refusal changes nothing, so a smaller buffer may still fit.</returns>
    /// <remarks>
    /// Security: the threat is one packet that completes many messages whose derived bytes stay referenced until the packet is
    /// recycled. The packet holds no limit (it knows no protocol); each protocol states its own, and this helper keeps the
    /// comparison and the failure mapping in one place. The comparison uses the remaining budget (<c>limit - bound</c>), so it
    /// cannot overflow whatever the limit is. The total is checked before the table, so with both exceeded the error names the
    /// total. A sealed packet reports <see cref="DerivedBytesFailure.TableFull"/> as well: for the caller both mean "nothing more
    /// can be attached to this packet". Residual risk: a protocol that does not call this helper binds without a byte limit and
    /// is bounded only by the 255 buffers of the table.
    /// Cost: one read of the counter and one compare; no allocation.
    /// </remarks>
    /// <seealso cref="MutField.TryBindBuffer"/>
    /// <seealso cref="MutField.BoundBytes"/>
    internal static bool TryBind(
        in MutField container,
        ReadOnlyMemory<byte> bytes,
        ulong totalLimit,
        out ReadOnlyMemory<byte> bound,
        out DerivedBytesFailure failure)
    {
        // Remaining budget instead of "bound + length > limit": the subtraction cannot wrap, the addition could.
        ulong alreadyBound = container.BoundBytes;
        ulong remaining = totalLimit > alreadyBound
            ? totalLimit - alreadyBound
            : 0;
        if ((ulong)bytes.Length > remaining)
        {
            bound = ReadOnlyMemory<byte>.Empty;
            failure = DerivedBytesFailure.TotalExceeded;
            return false;
        }

        if (!container.TryBindBuffer(bytes, out bound, out _))
        {
            failure = DerivedBytesFailure.TableFull;
            return false;
        }

        failure = DerivedBytesFailure.None;
        return true;
    }

    /// <summary>
    /// The text of the parse error for derived bytes that would pass the total of one packet. Every protocol uses this pattern,
    /// so the message names what could not be parsed, the total, that it is the total of one packet, and the setting.
    /// </summary>
    /// <param name="subject">What the packet holds too much of, plural and capitalized (for example "Decoded HTTP bodies").</param>
    /// <param name="limit">The effective total in bytes.</param>
    /// <param name="settingKey">The key of the setting that changes the total (for example <c>http.max_derived_bytes_per_packet</c>).</param>
    /// <returns>The error text; allocates one string, on the error path only.</returns>
    internal static string Text(string subject, ulong limit, string settingKey) => string.Concat(
        subject,
        " cannot be parsed further: together they would pass ",
        limit.ToString(CultureInfo.InvariantCulture),
        " bytes in one packet (setting ",
        settingKey,
        " is the total per packet)");
}
