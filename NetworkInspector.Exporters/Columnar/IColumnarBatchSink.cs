// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// A destination that consumes flushed <see cref="ColumnarPacketBatch"/> instances and writes
/// them in a format-specific representation (PBF columnar blocks, Parquet row groups, DuckDB
/// tables). Implementations own their own I/O and buffering; the caller is responsible for
/// calling <see cref="Complete"/> exactly once after the last <see cref="WriteBatch"/> call.
/// <para>
/// <b>Thread safety:</b> Implementations need not be thread-safe. The owning exporter calls <see cref="WriteBatch"/> and
/// <see cref="Complete"/> sequentially from the single thread that drives the export.
/// </para>
/// </summary>
internal interface IColumnarBatchSink
{
    /// <summary>
    /// Writes one flushed batch. The batch's contents are valid only for the duration of this
    /// call — the caller may call <see cref="ColumnarPacketBatch.Reset"/> on it immediately
    /// afterwards, so implementations must copy any data they need to retain. The batch clears and reuses its lists and
    /// field bags for the next block instead of allocating new ones, so a retained reference to one of those lists would
    /// be emptied (element values such as strings and byte arrays remain valid).
    /// </summary>
    /// <param name="batch">The batch to write.</param>
    void WriteBatch(ColumnarPacketBatch batch);

    /// <summary>Finalizes the sink (e.g. writes trailers/footers, flushes underlying streams).</summary>
    void Complete();
}
