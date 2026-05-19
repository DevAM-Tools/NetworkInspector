// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Defines the type of a CSV column.
/// </summary>
public enum CsvColumnKind
{
    /// <summary>The packet number (1-based ID).</summary>
    PacketNumber,

    /// <summary>The packet timestamp in ISO-8601 format.</summary>
    Timestamp,

    /// <summary>The packet info/summary string.</summary>
    Info,

    /// <summary>The frame length in bytes.</summary>
    FrameLength,

    /// <summary>A specific protocol field value.</summary>
    Field,
}
