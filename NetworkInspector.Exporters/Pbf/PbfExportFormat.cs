// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Defines the PBF block format.
/// </summary>
public enum PbfExportFormat
{
    /// <summary>
    /// Standard row-oriented block format: one nested message per packet with its full field tree inline.
    /// Repeated values are stored as same-as-previous flags and field names/types only once per block.
    /// Written by <c>StandardBlockBuilder</c>.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Columnar block format with per-field columns: packet IDs, timestamps, topology and one typed value column
    /// per field ID (the same model as the Parquet and DuckDB exporters). Written by <c>ColumnarBlockBuilder</c>.
    /// The enum value itself is not stored in the file; each block records its own type
    /// (<c>BlockTypeStandard</c> or <c>BlockTypeColumnar</c>).
    /// </summary>
    Columnar = 1,
}
