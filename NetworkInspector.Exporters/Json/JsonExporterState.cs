// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Mutable state for the JSON exporter, shared across packets within one export session.
/// Tracks field-info deduplication and same-as-previous optimizations for the compact format.
/// <para>
/// The state lives for the whole output document, not per packet: a reader of the compact format must replay the packets
/// in file order and apply the same bookkeeping to resolve omitted values and field names. It is therefore only
/// meaningful for one complete export and must not be reused or reset between packets. The one exception is
/// <see cref="Reset"/> after a packet that failed to reach the output (see there).
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; used from the single thread that drives the owning exporter.
/// </para>
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

    /// <summary>
    /// Forgets everything the state knows: no field counts as seen, no previous value or info exists. The exporter calls this
    /// after a packet failed to reach the output. That packet updated the state while it was serialized, so a later
    /// same-as-previous flag would point at a packet the file does not contain; after the reset the next packet is written
    /// in full (field names, values, info) and the document stays decodable from the start.
    /// </summary>
    internal void Reset()
    {
        FieldSeen.Clear();
        PreviousFields.Reset();
        PreviousPacketInfo = null;
    }

    /// <summary>Creates a new exporter state sized for the given field count.</summary>
    /// <param name="fieldCount">
    /// Expected number of distinct field ids (ids <c>0</c> to <c>fieldCount - 1</c>). A sizing hint for the bitmask and the
    /// dense arrays of the previous-value store; larger ids still work (they grow the mask or use the store's overflow
    /// path). Must not be negative.
    /// </param>
    internal JsonExporterState(int fieldCount)
    {
        FieldSeen = new FieldBitmask(fieldCount);
        PreviousFields = new PreviousFieldStore(fieldCount);
    }
}
