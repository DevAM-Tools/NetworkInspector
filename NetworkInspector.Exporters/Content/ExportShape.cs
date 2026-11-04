// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// What an export contains per packet. Every field exporter (CSV, JSON, Text, PBF Standard, PBF Columnar, Parquet, DuckDB)
/// supports all three shapes.
/// </summary>
/// <remarks>
/// The shape decides the parse work an exporter demands (see <see cref="IParseDemandSource"/>): the two flat shapes need no
/// field tree, because the exporter's observer receives the fields while the packet is parsed; only <see cref="Hierarchy"/>
/// needs the tree, because parent-child relations and sibling order exist only there. That is the whole reason the shapes
/// exist as a type: they let a pipeline skip building field trees nobody reads.
/// </remarks>
public enum ExportShape
{
    /// <summary>Only the fields of a <see cref="FieldSelection"/>; each value carries its occurrence number in the packet.</summary>
    SelectedFields = 0,

    /// <summary>Every value field of every packet, without parent information; each value carries its occurrence number.</summary>
    AllFields = 1,

    /// <summary>Every field with its tree position (node id, parent, sibling order), so the field tree can be rebuilt.</summary>
    Hierarchy = 2,
}
