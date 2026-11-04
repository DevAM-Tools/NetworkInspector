// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Writing;

/// <summary>A full batch on its way to the writer, with the data the lost-batch accounting needs.</summary>
/// <param name="Batch">The full batch.</param>
/// <param name="PacketCount">Items in the batch at the hand-off.</param>
/// <param name="FirstItemIndex">Index of the batch's first item.</param>
/// <param name="EstimatedBytes">Size estimate at the hand-off.</param>
/// <typeparam name="TBatch">The batch type.</typeparam>
internal readonly record struct WriteJob<TBatch>(TBatch Batch, int PacketCount, int FirstItemIndex, long EstimatedBytes)
    where TBatch : class;

/// <summary>
/// Moves full batches to the writer thread and empty ones back, with exactly two batches in the system.
/// <para>
/// <b>Primitive:</b> a blocking hand-off, because both sides must wait by design — the caller when the writer is behind (back
/// pressure that bounds memory to two batches), the writer when there is nothing to write. It runs once per batch (50 000
/// packets by default), never per packet, so its cost is two thread wake-ups per batch at most. With exactly two batches in the
/// system at most one full batch can wait and at most two empty ones can be on their way back, so no queue is needed: the full
/// direction is one reference slot guarded by two <see cref="SemaphoreSlim"/> (slot free, slot ready), the empty direction a ring
/// of two places behind one semaphore. <see cref="SemaphoreSlim"/> spins briefly before it blocks in the kernel, which suits the
/// short waits between two threads that alternate, and its <c>Release</c>/<c>Wait</c> are full barriers that publish the slot
/// content (store the value before the flag). A channel would need a sync-over-async wait on the caller's thread.
/// </para>
/// <para>
/// <b>Choice (measured):</b> against two <see cref="BlockingCollection{T}"/> (capacity 1 and 2), this variant hands off 6.9 %
/// faster in the isolated scenario <c>writer-handoff</c> and allocates nothing per hand-off (the collections allocated about 300
/// bytes per exchange), and the DuckDB export is unchanged inside its noise band. Decision record:
/// <c>docs/decisions/batch-hand-off.md</c> (numbers, A/B method, rejected options). <b>Why a ring and not one slot for the empty
/// direction:</b> the writer may return a batch while the caller already took the token of the previous return but has not read its
/// place yet; one slot would be overwritten and the caller would get back the batch it just handed over (the 100 000-exchange case
/// of <c>BatchHandOffTests</c> proves the ring).
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Exchange"/>, <see cref="Publish"/>, <see cref="Close"/> from the caller's thread;
/// <see cref="TryTakeFull"/>, <see cref="ReturnEmpty"/> from the writer thread. The read index of the ring belongs to the
/// caller's thread, the write index to the writer thread; the two never address the same place at the same time because only two
/// batches exist.
/// </para>
/// </summary>
/// <typeparam name="TBatch">The batch type.</typeparam>
/// <seealso cref="BatchWriterPipeline{TBatch}"/>
internal sealed class BatchHandOff<TBatch> : IDisposable
    where TBatch : class
{
    #region Full direction (caller to writer)

    /// <summary>
    /// The full batch waiting for the writer. One reference slot, because with exactly two batches at most one full batch can wait.
    /// The job's numbers are plain fields written before the semaphore release and read after the wait: SemaphoreSlim
    /// Release/Wait are full barriers, so no volatile is needed (store the value before the flag). SemaphoreSlim spins briefly
    /// before it blocks in the kernel, which suits the short waits between two threads that alternate; it allocates nothing per
    /// hand-off.
    /// </summary>
    private TBatch? _FullSlot;

    /// <summary>Item count of the job in <see cref="_FullSlot"/>.</summary>
    private int _FullPacketCount;

    /// <summary>Index of the first item of the job in <see cref="_FullSlot"/>.</summary>
    private int _FullFirstItemIndex;

    /// <summary>Size estimate of the job in <see cref="_FullSlot"/>.</summary>
    private long _FullEstimatedBytes;

    /// <summary>Counts the free full slot (0 or 1): the caller takes it to publish, the writer gives it back after it took the job.</summary>
    private readonly SemaphoreSlim _FullFree = new(1, 1);

    /// <summary>Counts the full slot's content (0 or 1): a job is waiting, or <see cref="Close"/> was called.</summary>
    private readonly SemaphoreSlim _FullReady = new(0, 1);

    /// <summary>Hands the last batch to the writer without taking another one.</summary>
    /// <param name="job">The last batch.</param>
    internal void Publish(in WriteJob<TBatch> job)
    {
        _FullFree.Wait();
        _FullPacketCount = job.PacketCount;
        _FullFirstItemIndex = job.FirstItemIndex;
        _FullEstimatedBytes = job.EstimatedBytes;
        Volatile.Write(ref _FullSlot, job.Batch);
        _FullReady.Release();
    }

    /// <summary>Writer: waits for the next full batch; false once <see cref="Close"/> was called and the slot is empty.</summary>
    /// <param name="job">The next job on success.</param>
    /// <returns><see langword="true"/> when <paramref name="job"/> holds a batch to write; <see langword="false"/> after <see cref="Close"/>.</returns>
    /// <exception cref="InvalidOperationException">The semaphore was released without a job or a close (a broken invariant).</exception>
    internal bool TryTakeFull(out WriteJob<TBatch> job)
    {
        _FullReady.Wait();
        TBatch? batch = Interlocked.Exchange(ref _FullSlot, null);
        if (batch is null)
        {
            // Only Close releases the semaphore without a job; anything else is a broken invariant of this class.
            if (!_Closed)
            {
                throw new InvalidOperationException("The batch hand-off was signalled without a job or a close.");
            }

            job = default;
            return false;
        }

        job = new WriteJob<TBatch>(batch, _FullPacketCount, _FullFirstItemIndex, _FullEstimatedBytes);
        _FullFree.Release();
        return true;
    }

    #endregion

    #region Empty direction (writer to caller)

    /// <summary>
    /// Empty direction: a ring of two places. Invariant: the writer may return a batch while the caller holds the token of the
    /// previous return but has not read its place yet; one slot would be overwritten there and the caller would receive the batch
    /// it just handed over. The write index belongs to the writer thread, the read index to the caller's thread; only two batches
    /// exist, so the two never address the same place at the same time.
    /// </summary>
    private readonly TBatch?[] _EmptyRing = new TBatch?[2];

    /// <summary>Running read position of the ring; touched by the caller's thread only (masked to the ring size when used).</summary>
    private int _EmptyReadIndex;

    /// <summary>Running write position of the ring, one ahead of the spare's place; touched by the writer thread only.</summary>
    private int _EmptyWriteIndex = 1;

    /// <summary>
    /// Counts the returned batches in the ring. Maximum 2: after the last Publish nobody takes the batch the writer returns, so
    /// the count reaches 2 once; both places then hold a batch and nothing is overwritten (the pipeline tracks and disposes
    /// every batch itself).
    /// </summary>
    private readonly SemaphoreSlim _EmptyReady = new(1, 2);

    /// <summary>Creates the hand-off with its second batch.</summary>
    /// <param name="spare">The second batch; the caller already holds the first.</param>
    internal BatchHandOff(TBatch spare) => _EmptyRing[0] = spare;

    /// <summary>Hands <paramref name="job"/> to the writer and waits for an empty batch to fill next.</summary>
    /// <param name="job">The full batch.</param>
    /// <returns>The empty batch the caller fills next.</returns>
    internal TBatch Exchange(in WriteJob<TBatch> job)
    {
        // Publish first, then wait for the empty batch: the writer takes the new job as soon as it finished the previous one,
        // without waiting for the caller to wake up (same overlap as the queue variant).
        Publish(in job);
        _EmptyReady.Wait();

        int index = _EmptyReadIndex++ & 1;
        TBatch batch = _EmptyRing[index]!; // Invariant: _EmptyReady counted exactly one returned batch at this place.
        _EmptyRing[index] = null;
        return batch;
    }

    /// <summary>Writer: gives an emptied batch back to the caller.</summary>
    /// <param name="batch">The emptied batch.</param>
    internal void ReturnEmpty(TBatch batch)
    {
        // The semaphore release below is the full barrier that publishes the place to the caller.
        _EmptyRing[_EmptyWriteIndex++ & 1] = batch;
        _EmptyReady.Release();
    }

    #endregion

    #region Shutdown

    /// <summary>Set before the <see cref="_FullReady"/> release of <see cref="Close"/>, read after the wait in <see cref="TryTakeFull"/>.</summary>
    private bool _Closed;

    /// <summary>No more batches follow; the writer ends after the queued ones.</summary>
    internal void Close()
    {
        // The writer took the last job; the slot is free for the stop signal.
        _FullFree.Wait();
        _Closed = true;
        _FullReady.Release();
    }

    /// <summary>Releases the semaphores; call after the writer thread ended.</summary>
    public void Dispose()
    {
        _FullFree.Dispose();
        _FullReady.Dispose();
        _EmptyReady.Dispose();
    }

    #endregion
}
