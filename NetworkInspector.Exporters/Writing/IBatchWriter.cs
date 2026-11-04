// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Writing;

/// <summary>
/// Writes one full batch to the output. Called on the writer thread in <see cref="ExportWriteMode.Background"/> and on the
/// caller's thread in <see cref="ExportWriteMode.Synchronous"/>, never concurrently.
/// </summary>
/// <remarks>
/// Completing the output (catalog, trailer, header patch) is not part of this interface: the exporter does it itself after
/// <see cref="BatchWriterPipeline{TBatch}.Complete"/> returned and the lost batches were accounted, so a trailer or catalog
/// states only what is really in the output. The join inside <c>Complete</c> makes everything the writer thread wrote visible
/// to the caller.
/// </remarks>
/// <typeparam name="TBatch">The batch type the exporter fills.</typeparam>
internal interface IBatchWriter<in TBatch>
    where TBatch : class, IExportBatch
{
    /// <summary>Writes <paramref name="batch"/>; throws on failure (the pipeline turns it into a <see cref="LostBatch"/>).</summary>
    /// <param name="batch">The full batch; owned by the writer until the call returns.</param>
    void Write(TBatch batch);
}
