// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// Receives the fields of one packet for one exporter while they arrive; implemented by <c>ColumnarPacketBatch</c> (columnar
/// formats) and <see cref="RowFieldStage"/> (row formats).
/// </summary>
/// <remarks>
/// <para>
/// <b>Commit rule:</b> <see cref="BeginPacket"/> opens a staging; <see cref="EndPacket"/> closes it (complete) or marks it failed
/// (incomplete); <see cref="TryCommit"/> with the same packet id makes the rows part of the output. A new
/// <see cref="BeginPacket"/> or <see cref="Discard"/> drops a staging that was never committed. The pipeline therefore needs no
/// reject call: a packet the filter rejects is simply never committed, and a replay cannot write twice.
/// </para>
/// <para>
/// <b>Lifetime:</b> the value passed to <see cref="AddValue"/> is valid only for the call (it may alias a packet buffer that is
/// recycled after the parse): implementations copy bytes at once. The <see cref="LazyString"/> structs are kept and evaluated in
/// <see cref="EndPacket"/>, because a dissector may append a deferred text that is completed only later in the parse.
/// </para>
/// <para><b>Thread safety:</b> not thread-safe; one parse thread calls every member, and the exporter commits on the same thread.</para>
/// </remarks>
internal interface IFieldStage
{
    /// <summary>Packet id of the open or closed staging, or -1 when there is none.</summary>
    int StagedPacketId { get; }

    /// <summary>Opens the staging of a packet; drops an earlier staging that was never committed.</summary>
    /// <param name="packetId">The packet id of the parse.</param>
    /// <param name="timestampNanos">The packet timestamp in nanoseconds.</param>
    void BeginPacket(int packetId, long timestampNanos);

    /// <summary>Stages one value of a field.</summary>
    /// <param name="fieldId">Field id in the stack of the parse.</param>
    /// <param name="occurrence">Occurrence number of the value in the packet (0 for the first).</param>
    /// <param name="value">The value; valid only for the call.</param>
    /// <param name="customRepresentation">Custom representation (kept, evaluated at the end of the packet).</param>
    /// <param name="customText">Custom text (kept, evaluated at the end of the packet).</param>
    void AddValue(int fieldId, int occurrence, in FieldValueData value, LazyString customRepresentation, LazyString customText);

    /// <summary>Overwrites the last staged value of a field in this packet (occurrence choice <c>Last</c>).</summary>
    /// <param name="fieldId">Field id in the stack of the parse.</param>
    /// <param name="value">The new value; valid only for the call.</param>
    /// <param name="customRepresentation">Custom representation of the new value.</param>
    /// <param name="customText">Custom text of the new value.</param>
    void ReplaceLastValue(int fieldId, in FieldValueData value, LazyString customRepresentation, LazyString customText);

    /// <summary>Closes the staging (complete) or marks it failed (incomplete); evaluates the kept texts.</summary>
    /// <param name="complete">Whether the observer received every field of the packet.</param>
    void EndPacket(bool complete);

    /// <summary>
    /// Makes the staged fields part of the output when <paramref name="packet"/> is the staged, closed packet. An implementation
    /// that reads packet-level data at the commit (the columnar batch reads the info text) refuses the commit and drops the
    /// staging when that read fails, so the output is exactly as it was before.
    /// </summary>
    /// <param name="packet">The packet the exporter was asked to write.</param>
    /// <param name="kind">The error kind when the method returns <see langword="false"/>.</param>
    /// <param name="message">The reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the staging was committed.</returns>
    bool TryCommit(Packet packet, out ExportErrorKind kind, out string? message);

    /// <summary>Drops a staging that was not committed.</summary>
    void Discard();
}
