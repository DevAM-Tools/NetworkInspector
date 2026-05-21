// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Flag constants for same-as-previous optimizations in exporters.
/// When consecutive packets or fields share identical values, the exporter
/// stores only a bitmask indicating which values are repeated, saving space.
/// </summary>
internal static class SameFlags
{
    // --- Packet-level flags ---

    /// <summary>Packet info string is identical to the previous packet.</summary>
    internal const uint PacketSameInfo = 0x01;

    // --- Field-level flags ---

    /// <summary>Field value (data) is identical to the same field in the previous packet.</summary>
    internal const uint FieldSameValue = 0x01;

    /// <summary>Field value's custom representation text is identical to the previous packet.</summary>
    internal const uint FieldSameCustomRepresentation = 0x02;

    /// <summary>Field custom text is identical to the same field in the previous packet.</summary>
    internal const uint FieldSameCustomText = 0x04;
}
