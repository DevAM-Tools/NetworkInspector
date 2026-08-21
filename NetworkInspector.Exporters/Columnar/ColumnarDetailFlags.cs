// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Controls which optional data is included when building a <see cref="ColumnarPacketBatch"/>.
/// Shared across all columnar exporters (PBF columnar blocks, Parquet, DuckDB) so callers
/// configure detail once regardless of the target format.
/// </summary>
[Flags]
public enum ColumnarDetailFlags
{
    #region Enum Values

    /// <summary>No optional data is included; only packet identifiers, timestamps, and field values.</summary>
    None = 0,

    /// <summary>Includes the per-packet summary <see cref="Packet.Info"/> string.</summary>
    IncludeInfo = 1,

    /// <summary>Includes the raw captured frame bytes for each packet.</summary>
    IncludeFrameBytes = 2,

    /// <summary>Includes <see cref="FieldValue.CustomRepresentation"/> text for fields that define one.</summary>
    IncludeCustomRepresentation = 4,

    /// <summary>Includes <see cref="Field.CustomText"/> for fields that define one.</summary>
    IncludeCustomText = 8,

    /// <summary>Includes the field-tree topology (parent/child node relationships) for each packet.</summary>
    IncludeTopology = 16,

    /// <summary>All optional data is included.</summary>
    All = IncludeInfo | IncludeFrameBytes | IncludeCustomRepresentation | IncludeCustomText | IncludeTopology,

    #endregion
}
