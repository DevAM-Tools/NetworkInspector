// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// A destination that consumes flushed <see cref="ColumnarPacketBatch"/> instances and writes
/// them in a format-specific representation (PBF columnar blocks, Parquet row groups, DuckDB
/// tables). Implementations own their own I/O and buffering; the caller is responsible for
/// calling <see cref="Complete"/> exactly once after the last <see cref="WriteBatch"/> call.
/// </summary>
internal interface IColumnarBatchSink
{
    /// <summary>
    /// Writes one flushed batch. The batch's contents are valid only for the duration of this
    /// call — the caller may call <see cref="ColumnarPacketBatch.Reset"/> on it immediately
    /// afterwards, so implementations must copy any data they need to retain.
    /// </summary>
    /// <param name="batch">The batch to write.</param>
    void WriteBatch(ColumnarPacketBatch batch);

    /// <summary>Finalizes the sink (e.g. writes trailers/footers, flushes underlying streams).</summary>
    void Complete();
}
