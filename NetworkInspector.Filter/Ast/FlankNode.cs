// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Ast;

#region Window

/// <summary>
/// The mandatory <c>within:</c> argument of a <c>flank(…)</c> expression.
/// Either a wall-clock duration or a packet-count distance.
/// </summary>
internal readonly record struct FlankWindow
{
    #region Construction

    private FlankWindow(bool isPacketCount, long nanoseconds, int packetCount)
    {
        IsPacketCount = isPacketCount;
        Nanoseconds = nanoseconds;
        PacketCount = packetCount;
    }

    /// <summary>Creates a duration window.</summary>
    public static FlankWindow FromNanoseconds(long nanoseconds) => new(false, nanoseconds, 0);

    /// <summary>Creates a packet-count window.</summary>
    public static FlankWindow FromPackets(int packets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packets);
        if (packets > ArrayIndexIdRange.MaxCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(packets),
                packets,
                $"Packet window must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)} " +
                $"(valid packet indices 0..{ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)}).");
        }

        return new(true, 0, packets);
    }

    #endregion

    #region Properties

    /// <summary>Whether the window is expressed in packets rather than time.</summary>
    public bool IsPacketCount { get; }

    /// <summary>Window size in nanoseconds when <see cref="IsPacketCount"/> is <see langword="false"/>.</summary>
    public long Nanoseconds { get; }

    /// <summary>Window size in packets when <see cref="IsPacketCount"/> is <see langword="true"/>.</summary>
    public int PacketCount { get; }

    #endregion
}

#endregion

#region Endpoint

/// <summary>
/// A <c>from:</c> or <c>to:</c> endpoint. A bare literal implies <see cref="CompareOp.Equal"/>;
/// an explicit comparison operator turns the endpoint into a region test
/// (see language guide §5).
/// </summary>
/// <param name="Op">The comparison used to decide whether a sample lies in this endpoint's region.</param>
/// <param name="Value">The literal compared against.</param>
internal readonly record struct FlankEndpoint(CompareOp Op, FieldValueData Value);

#endregion

#region Delta

/// <summary>
/// A <c>by:</c> delta predicate. A bare integer implies <see cref="CompareOp.Equal"/>;
/// an explicit comparison operator tests <c>current − arm_or_last</c> against
/// <see cref="Value"/>.
/// </summary>
/// <param name="Op">The comparison applied to the signed delta.</param>
/// <param name="Value">The integer literal compared against the delta.</param>
internal readonly record struct FlankDelta(CompareOp Op, FieldValueData Value);

#endregion

#region Node

/// <summary>
/// A <c>flank(field [, from: …] [, to: …] [, by: …] [, changed], within: …[, when: …])</c>
/// expression.
/// <para>
/// <b>Armed</b> mode (<see cref="IsArmedMode"/>) is <c>from:</c> plus <c>to:</c> and/or
/// <c>by:</c>: the tracker latches the oldest in-window start sample. <b>Pairwise</b> mode is
/// everything else (arrival, departure, <see cref="IsAnyChange"/>, <c>by:</c> alone). Combining
/// <c>changed</c> with an endpoint or delta is a compile error, enforced by the parser.
/// </para>
/// </summary>
internal sealed class FlankNode(
    string fieldName,
    FlankEndpoint? from,
    FlankEndpoint? to,
    FlankDelta? by,
    bool isAnyChange,
    FlankWindow window,
    FilterNode? when,
    int position,
    int length)
    : FilterNode(position, length)
{
    /// <summary>The qualified field whose samples are tracked.</summary>
    public string FieldName { get; } = fieldName;

    /// <summary>Optional predecessor-region endpoint.</summary>
    public FlankEndpoint? From { get; } = from;

    /// <summary>Optional arrival-region endpoint.</summary>
    public FlankEndpoint? To { get; } = to;

    /// <summary>Optional numeric-delta predicate; <see langword="null"/> when unused.</summary>
    public FlankDelta? By { get; } = by;

    /// <summary>Whether the flank fires on any value change.</summary>
    public bool IsAnyChange { get; } = isAnyChange;

    /// <summary>The mandatory window.</summary>
    public FlankWindow Window { get; } = window;

    /// <summary>Optional gate; when it evaluates false the tracker is neither read nor updated.</summary>
    public FilterNode? When { get; } = when;

    /// <summary>
    /// Armed latch: <c>from:</c> plus <c>to:</c> and/or <c>by:</c>.
    /// <c>from:</c> alone is pairwise departure, not armed.
    /// </summary>
    public bool IsArmedMode => From is not null && (To is not null || By is not null);
}

#endregion
