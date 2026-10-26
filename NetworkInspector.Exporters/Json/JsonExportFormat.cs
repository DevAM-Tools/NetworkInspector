// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Defines the JSON output format for the <see cref="JsonExporter"/>.
/// <para>
/// In every format the file is one JSON array of packet objects, and the packet timestamp is an integer number of
/// nanoseconds since the Unix epoch. Such values exceed 2^53, so consumers that parse JSON numbers as IEEE doubles lose
/// precision.
/// </para>
/// </summary>
public enum JsonExportFormat
{
    /// <summary>Compact format with short keys and same-as-previous deduplication.</summary>
    Compact = 0,

    /// <summary>Pretty-printed format with full keys and 2-space indentation.</summary>
    Pretty = 1,

    /// <summary>Flat JSON format with full keys, no indentation (NDJSON-style objects in array).</summary>
    Array = 2,
}
