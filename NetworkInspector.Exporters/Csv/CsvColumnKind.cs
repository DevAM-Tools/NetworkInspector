// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Defines the type of a CSV column.
/// </summary>
public enum CsvColumnKind
{
    /// <summary>
    /// The packet number: the raw <c>PacketId</c> value of the packet, written as decimal. It is not renumbered by the
    /// exporter, so it is exactly the id the packet carries in the pipeline (ids are array indices in Core).
    /// </summary>
    PacketNumber,

    /// <summary>The packet timestamp in ISO-8601 format with nanosecond resolution, UTC (<c>Timestamp.TryFormat</c>).</summary>
    Timestamp,

    /// <summary>The packet info/summary string.</summary>
    Info,

    /// <summary>The frame length in bytes.</summary>
    FrameLength,

    /// <summary>
    /// The raw frame bytes as upper-case hexadecimal digits without separators (two digits per byte, no prefix); an empty frame is an
    /// empty cell. Valid in the packet-rows layout and in the selected-fields (wide) layout only: the all-fields and hierarchy
    /// layouts repeat the packet columns on every field row, so the frame would be written once per field.
    /// </summary>
    /// <remarks>Sync: the CSV README § Frame bytes and the message of <c>CsvExporter.Builder.Build</c>.</remarks>
    FrameBytes,

    /// <summary>
    /// The additional packet buffers (index 1 to 255: reassembled datagrams, TCP PDUs, decoded bodies) as one cell of records
    /// <c>&lt;index&gt;:&lt;HEX&gt;</c> joined by <c>;</c> in ascending index order, for example <c>1:0A0B;2:</c> (a bound empty buffer is a record
    /// with no digits); a packet without additional buffers has an empty cell. The hex is upper case without separators, as for
    /// <see cref="FrameBytes"/>. Valid in the packet-rows layout and in the selected-fields (wide) layout only, for the same reason as
    /// <see cref="FrameBytes"/>. A cell is quoted when the delimiter could split it (a digit, a hex letter, <c>:</c>, or <c>;</c>).
    /// </summary>
    /// <remarks>Sync: the CSV README § Buffers and the message of <c>CsvExporter.Builder.Build</c>.</remarks>
    Buffers,
}
