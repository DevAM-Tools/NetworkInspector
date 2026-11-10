// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// SoA series of payload values plus packet-id and timestamp columns on the base type, each a
/// <see cref="ChunkedGrowOnlyStore{T}"/>. Published <see cref="ValueCacheSeries.Count"/> is written
/// only after all three stores hold the new row.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. See <see cref="ValueCacheSeries"/>.
/// Readers must load <see cref="ValueCacheSeries.Count"/> or <see cref="Handle"/> and must not read
/// store <c>Count</c>. Contended <see cref="Record"/> waits on a series-level CAS gate so the three
/// column <c>Append</c>s and the published <see cref="ValueCacheSeries.Count"/> write stay one row.
/// </para>
/// </summary>
/// <typeparam name="T">Payload type selected from the stack <see cref="FieldType"/>.</typeparam>
public sealed class ValueCacheSeries<T> : ValueCacheSeries, IReadOnlyValueCacheSeries<T>
{
    #region Configuration and state

    /// <summary>
    /// Value column of the structure-of-arrays row; the packet-id and timestamp columns live on the base type. Chunked
    /// storage grows without copying already published values, so readers keep spans into old chunks valid while the
    /// writer appends (a single contiguous array would have to be reallocated and copied under concurrent readers).
    /// </summary>
    private readonly ChunkedGrowOnlyStore<T> _Values;

    /// <summary>
    /// Cheap CAS gate (not <see cref="Monitor"/>) around the three column appends and published
    /// <see cref="ValueCacheSeries.Count"/>. Contended <see cref="Record"/> spins until the gate is free
    /// (serialized, no throw). Without it a second writer can interleave packet-id, timestamp, and value slots.
    /// 0 = free, 1 = held. The contract is a single writer, so the gate is uncontended in normal operation and costs
    /// one compare-exchange; a Monitor would add an object and kernel waits for a case that should not happen.
    /// </summary>
    private volatile int _AppendGate;

    /// <summary>Creates an empty series.</summary>
    /// <param name="fieldId">The recorded field.</param>
    /// <param name="fieldType">Payload type of the stack field, or <see cref="FieldType.String"/> for text series.</param>
    /// <param name="captureMode">How several occurrences in one packet are captured.</param>
    /// <param name="chunkShift">Log₂ of rows per inner column chunk, in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is outside [4, 20] (rejected by the column stores).</exception>
    internal ValueCacheSeries(
        FieldId fieldId,
        FieldType fieldType,
        ValueCaptureMode captureMode,
        int chunkShift)
        : base(fieldId, fieldType, captureMode, chunkShift)
    {
        _Values = new(chunkShift);
    }

    #endregion

    #region Reads

    /// <summary>
    /// Frozen prefix including the value column. Index, range, and <c>foreach</c> hide chunk layout
    /// and stop at the loaded <see cref="ValueCacheSeries.Count"/>.
    /// </summary>
    public new ValueCacheSeriesHandle<T> Handle
    {
        get
        {
            // Count first, flags second: the writer clears a monotonic flag before it publishes the row that breaks the order,
            // so loading the count first can only make a flag too pessimistic for the prefix, never too optimistic (see the
            // flag fields in ValueCacheSeries). Reversing these two loads would let a handle claim a sorted prefix that is not.
            int observed = Count;
            ValueCacheSeriesHandle keys = new ValueCacheSeriesHandle(
                this,
                observed,
                PacketIdsMonotonic,
                TimestampsMonotonic);
            return new ValueCacheSeriesHandle<T>(this, keys);
        }
    }

    /// <summary>
    /// Gathered row at <paramref name="index"/>. Throws when <paramref name="index"/> is not strictly less than <see cref="ValueCacheSeries.Count"/>.
    /// </summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not published.</exception>
    public ValueCacheRow<T> this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");
            }

            // The value is non-null for a published index: the value column is appended before Count is published, and
            // Get only returns its unset default for an unpublished index (excluded by the range check above).
            return new ValueCacheRow<T>(
                GetPacketId(index),
                GetTimestamp(index),
                _Values.Get(index)!); // Invariant: index is below Count, so the value slot was written before Count was published.
        }
    }

    /// <summary>Payload at <paramref name="index"/> against the live published <see cref="ValueCacheSeries.Count"/>.</summary>
    /// <param name="index">Row index in <c>[0, Count)</c>.</param>
    /// <returns>The stored payload of that row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in <c>[0, Count)</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetValue(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            _ThrowIndexOutOfRange(index);
        }

        return ValueAt(index);
    }

    /// <summary>
    /// Payload with no live-count check. Caller proved <paramref name="index"/> is in a snapshot prefix (a frozen handle
    /// count), which skips the volatile <see cref="ValueCacheSeries.Count"/> load per element in scan loops.
    /// </summary>
    /// <param name="index">Row index below a count the caller loaded earlier.</param>
    /// <returns>The stored payload of that row.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T ValueAt(int index) =>
        _Values.Get(index)!; // Invariant: index is below a loaded Count, so the value slot was written before Count was published.

    /// <summary>
    /// Returns the published value chunk at <paramref name="chunkIndex"/> clipped to
    /// the lesser of <paramref name="observedCount"/> and the published <see cref="ValueCacheSeries.Count"/>.
    /// A caller who passes <see cref="int.MaxValue"/> still cannot see unpublished slots.
    /// Prefer <see cref="Handle"/> for scans.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index (row index shifted right by <see cref="ValueCacheSeries.ChunkShift"/>).</param>
    /// <param name="observedCount">Exclusive upper bound of the rows to expose. A negative value is treated as 0.</param>
    /// <param name="span">The values of the chunk below the bound; empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the chunk exists and holds at least one row below the bound.</returns>
    public bool TryGetValueChunk(int chunkIndex, int observedCount, out ReadOnlySpan<T> span) =>
        _Values.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Zero-allocation read-only view. Keep the compile-time type as
    /// <see cref="ReadOnlyValueCacheSeries{T}"/>; assigning to <see cref="IReadOnlyValueCacheSeries{T}"/> boxes.
    /// </summary>
    /// <returns>A view that forwards to this series and offers no writer members.</returns>
    public new ReadOnlyValueCacheSeries<T> AsReadOnlyView() => new(this);

    #endregion

    #region Row append and clear

    /// <summary>
    /// Appends one occurrence or skips it (FirstOccurrence same consecutive packet id).
    /// Contended callers wait on the series gate so the three column appends stay atomic as a row.
    /// AllOccurrences is bounded by the packet field cap (<c>ushort.MaxValue - 1</c>), not by a series counter.
    /// The gate is also what lets the three column stores skip their own single-writer compare-exchange: the row is
    /// appended with <c>AppendOwned</c>, whose precondition is that every append and clear of the store is serialized by
    /// the caller. Never call <c>AppendOwned</c> without this gate.
    /// </summary>
    /// <param name="packetId">Packet id of the row.</param>
    /// <param name="timestampNanos">Packet timestamp of the row in nanoseconds.</param>
    /// <param name="value">The payload to store; reference types are stored as given, so the caller must not change them afterwards.</param>
    /// <exception cref="InvalidOperationException">The columns reached their maximum number of rows; nothing was appended.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Record(int packetId, long timestampNanos, T value)
    {
        _EnterAppendGate();
        try
        {
            // FirstOccurrence: a repeat within the same packet is dropped. LastPacketId is writer state, so it is only
            // read under the gate. The skip happens before any column is touched, so nothing is half-appended.
            if (CaptureMode == ValueCaptureMode.FirstOccurrence && packetId == LastPacketId)
            {
                return;
            }

            // Order matters: key columns first, value column second, Count last. Count is the only thing readers trust,
            // so a row is invisible until all three slots are written. All three stores share one row index, so an
            // index-limit failure surfaces from the first append before any column has changed.
            AppendKeys(packetId, timestampNanos);
            _Values.AppendOwned(in value);
            PublishCount();
        }
        finally
        {
            // Release the gate on every exit, including the FirstOccurrence skip and an exception from a column append.
            _ = Interlocked.Exchange(ref _AppendGate, 0);
        }
    }

    /// <summary>
    /// Drops all published rows. Takes the same gate as <see cref="Record"/>, which is the precondition of the column
    /// stores' <c>Clear</c> (serialized with every append). Count and monotonic flags are reset by the key-column clear.
    /// </summary>
    internal override void ClearRows()
    {
        _EnterAppendGate();
        try
        {
            ClearKeyColumns();
            _Values.Clear();
        }
        finally
        {
            _ = Interlocked.Exchange(ref _AppendGate, 0);
        }
    }

    /// <summary>
    /// Takes the series write gate. Uncontended: one CAS. Contended: spin until free. Only the uncontended CAS is
    /// inlined into <see cref="Record"/>; the spin loop is split into a NoInlining slow path to keep the hot caller small.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EnterAppendGate()
    {
        if (Interlocked.CompareExchange(ref _AppendGate, 1, 0) == 0)
        {
            return;
        }

        _EnterAppendGateSlow();
    }

    /// <summary>Wait until this caller owns the gate. <see cref="SpinWait"/> yields after a short spin.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _EnterAppendGateSlow()
    {
        SpinWait spinner = default;
        while (Interlocked.CompareExchange(ref _AppendGate, 1, 0) != 0)
        {
            spinner.SpinOnce();
        }
    }

    #endregion
}
