// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// How multiple occurrences of one field within a single packet are captured.
/// Parse-time record uses append/prepend/insert order. <see cref="ValueCache.RecordPacket"/> uses
/// <see cref="Packet.TryGetNextField"/> lookup order. After <c>PrependChild</c> those orders can
/// disagree, so a parse-filled cache and a <see cref="ValueCache.RecordPacket"/> cache of the same
/// field are not interchangeable for <see cref="FirstOccurrence"/>.
/// </summary>
public enum ValueCaptureMode : byte
{
    /// <summary>
    /// Only the first occurrence is stored (default; densest). The series implements this by skipping a record whose
    /// packet id equals the packet id of the last stored row, so it relies on the writer recording packets one after
    /// another and on "first" meaning the first occurrence delivered to the series (see the order remark above).
    /// </summary>
    FirstOccurrence = 0,

    /// <summary>
    /// Every occurrence is stored as its own row with the same packet id and timestamp. Rows of one packet are adjacent,
    /// so with packets recorded in id order the packet ids stay non-decreasing and a range lookup covers all duplicates. The per-packet count is bounded by
    /// the packet field capacity, so no extra counter is needed.
    /// </summary>
    AllOccurrences = 1,
}
