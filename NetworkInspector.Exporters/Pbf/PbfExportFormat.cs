// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Defines the PBF block format.
/// </summary>
public enum PbfExportFormat
{
    /// <summary>Standard row-oriented block format.</summary>
    Standard = 0,

    /// <summary>Columnar block format with per-field columns.</summary>
    Columnar = 1,
}
