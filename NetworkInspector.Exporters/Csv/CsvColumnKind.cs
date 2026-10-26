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

    /// <summary>A specific protocol field value.</summary>
    Field,
}
