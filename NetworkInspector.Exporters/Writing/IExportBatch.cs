// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Writing;

/// <summary>
/// A batch that a <see cref="BatchWriterPipeline{TBatch}"/> moves between the filling thread and the writer thread.
/// <para>
/// <b>Ownership:</b> exactly one thread owns a batch at a time — the caller while it fills it, the writer from the hand-off
/// until it returns the batch empty. Implementations need no synchronization of their own; the pipeline's hand-off is the
/// memory barrier that publishes the filled content to the writer and the emptied batch back to the caller.
/// </para>
/// </summary>
internal interface IExportBatch : IDisposable
{
    /// <summary>Items (packets or frames) in the batch; used for the lost-batch accounting.</summary>
    int PacketCount { get; }

    /// <summary>In-memory size estimate in bytes; used for <c>EstimatedOutputBytes</c> and the lost-batch accounting.</summary>
    long EstimatedSizeBytes { get; }

    /// <summary>Forgets all rows so the batch can be filled again; keeps pooled capacity.</summary>
    void Reset();
}
