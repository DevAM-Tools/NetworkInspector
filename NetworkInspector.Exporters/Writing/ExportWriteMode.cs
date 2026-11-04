// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>How a batching exporter (DuckDB, Parquet, PBF, BLF) writes a full batch.</summary>
public enum ExportWriteMode
{
    /// <summary>
    /// A dedicated writer thread writes a full batch while the caller fills the next one, so parsing and writing overlap.
    /// Memory: two batches. A failed write is reported by the next <c>OnPacket</c>/<c>OnFrame</c> call or by <c>OnFinish</c>.
    /// With a caller-owned stream, the caller must not use the stream until <c>OnFinish</c> returned.
    /// </summary>
    Background = 0,

    /// <summary>The caller's thread writes each full batch inside the call that completed it. Memory: one batch.</summary>
    Synchronous = 1,
}
