// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Mutable state for the JSON exporter, shared across packets within one export session.
/// Tracks field-info deduplication and same-as-previous optimizations for the compact format.
/// </summary>
internal sealed class JsonExporterState
{
    /// <summary>Tracks which fields have had their info (name, UI name, type) emitted.</summary>
    internal FieldBitmask FieldSeen
    {
        get;
    }

    /// <summary>Stores previous field values for same-as-previous detection.</summary>
    internal PreviousFieldStore PreviousFields
    {
        get;
    }

    /// <summary>The info string from the previous packet (for same-as-previous detection).</summary>
    internal string? PreviousPacketInfo
    {
        get; set;
    }

    /// <summary>Creates a new exporter state sized for the given field count.</summary>
    /// <param name="fieldCount">Expected number of distinct fields.</param>
    internal JsonExporterState(int fieldCount)
    {
        FieldSeen = new FieldBitmask(fieldCount);
        PreviousFields = new PreviousFieldStore(fieldCount);
    }
}
