// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
