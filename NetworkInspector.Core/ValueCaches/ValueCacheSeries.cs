// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Non-generic reader façade for one recorded series (payload, custom text, or custom representation).
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. The writer stages rows during
/// <see cref="ValueCache.BeginPacket"/> … <see cref="ValueCache.EndPacket"/> (or
/// <see cref="ValueCache.RecordPacket"/>). Concurrent readers may observe <see cref="Count"/>,
/// <see cref="ByteSize"/>, and published chunk spans for indices strictly less than the
/// <see cref="Count"/> they loaded. Rows from a packet that has not finished
/// <see cref="ValueCache.EndPacket"/> are not visible. There is no public growth callback on
/// Core <see cref="ValueCache"/>; poll <see cref="Count"/>.
/// </para>
/// </summary>
public abstract class ValueCacheSeries
{
    #region Properties

    /// <summary>Recorded field.</summary>
    public abstract FieldId FieldId { get; }

    /// <summary>Payload <see cref="Fields.FieldType"/> of the stack field, or <see cref="FieldType.String"/> for custom-text/representation series.</summary>
    public abstract FieldType FieldType { get; }

    /// <summary>Capture mode used when staging occurrences.</summary>
    public abstract ValueCaptureMode CaptureMode { get; }

    /// <summary>Number of committed rows. Volatile; readers must treat this as the exclusive upper bound for indexed reads.</summary>
    public abstract int Count { get; }

    /// <summary>Cumulative C13 byte charge for this series. Readable concurrently with <see cref="Count"/>.</summary>
    public abstract long ByteSize { get; }

    #endregion

    #region Public API

    /// <summary>
    /// Returns the published packet-id chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/> (the caller's previously loaded <see cref="Count"/>).
    /// </summary>
    public abstract bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span);

    /// <summary>
    /// Returns the published timestamp chunk at <paramref name="chunkIndex"/> clipped to
    /// <paramref name="observedCount"/>.
    /// </summary>
    public abstract bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span);

    #endregion

    #region Internal lifecycle

    /// <summary>Resets per-packet occurrence state. Writer only.</summary>
    internal abstract void BeginPacket();

    /// <summary>Publishes staged rows or retracts a torn packet when capacity was reached. Writer only.</summary>
    internal abstract void Commit();

    #endregion
}

/// <summary>
/// Shared capacity flag and cache-wide byte/row accounting for all series of one <see cref="ValueCache"/>.
/// Writer-only except for volatile <see cref="Reached"/> / <see cref="BytesCharged"/>.
/// </summary>
internal sealed class ValueCacheCapacity
{
    #region Fields

    internal volatile int Reached;
    private long _BytesCharged;

    private int _StagedRowCount;

    #endregion

    #region Constructors

    internal ValueCacheCapacity(ValueCacheLimits limits) => Limits = limits;

    #endregion

    #region Properties

    internal ValueCacheLimits Limits { get; }

    internal bool IsReached => Reached != 0;

    #endregion

    #region Public API

    internal void MarkReached() => Reached = 1;

    internal bool WouldExceedRowCount()
    {
        int? maxRows = Limits.MaxRowCount;
        return maxRows is int max && _StagedRowCount >= max;
    }

    internal void AddStagedRow() => _StagedRowCount++;

    internal void RemoveStagedRows(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _StagedRowCount -= count;
        if (_StagedRowCount < 0)
        {
            _StagedRowCount = 0;
        }
    }

    internal long BytesCharged => Volatile.Read(ref _BytesCharged);

    internal bool WouldExceedBytes(long addedBytes)
    {
        long? maxBytes = Limits.MaxBytes;
        if (maxBytes is not long max)
        {
            return false;
        }

        long current = Volatile.Read(ref _BytesCharged);
        if (addedBytes < 0)
        {
            return false;
        }

        try
        {
            return checked(current + addedBytes) > max;
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    internal void AddBytes(long addedBytes)
    {
        long current = Volatile.Read(ref _BytesCharged);
        long next;
        try
        {
            next = checked(current + addedBytes);
        }
        catch (OverflowException)
        {
            MarkReached();
            return;
        }

        Volatile.Write(ref _BytesCharged, next);
    }

    internal void SubtractBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        long current = Volatile.Read(ref _BytesCharged);
        long next = current - bytes;
        if (next < 0)
        {
            next = 0;
        }

        Volatile.Write(ref _BytesCharged, next);
    }

    #endregion
}

/// <summary>
/// Packet-id and timestamp columns plus committed-count publish for one series.
/// Single-writer; readers use <see cref="CommittedCount"/> as the exclusive upper bound.
/// </summary>
internal sealed class ValueCacheColumnState
{
    #region Constants

    internal const int ChunkShift = 12;
    internal const int ChunkSize = 1 << ChunkShift;
    internal const int PacketIdSlotBytes = 4;
    internal const int TimestampSlotBytes = 8;
    internal const int StringObjectOverheadBytes = 24;

    #endregion

    #region Fields

    private readonly ChunkedGrowOnlyStore<int> _PacketIds;
    private readonly ChunkedGrowOnlyStore<long> _Timestamps;

    private volatile int _CommittedCount;
    private long _ByteSize;

    private int _StagedCount;
    private int _StagedThisPacket;
    private ushort _OccurrenceInPacket;
    private long _BytesChargedThisPacket;

    #endregion

    #region Constructors

    internal ValueCacheColumnState(ValueCacheCapacity capacity, ValueCaptureMode captureMode)
    {
        Capacity = capacity;
        CaptureMode = captureMode;
        _PacketIds = new(ChunkShift);
        _Timestamps = new(ChunkShift);
    }

    #endregion

    #region Properties

    internal ValueCaptureMode CaptureMode { get; }

    internal int CommittedCount => _CommittedCount;

    /// <summary>
    /// Clips a caller-supplied chunk bound to the published row count so
    /// <c>TryGet*Chunk(..., int.MaxValue)</c> cannot return staged or unset slots.
    /// </summary>
    internal int ClipObservedCount(int observedCount)
    {
        int committed = _CommittedCount;
        if (observedCount > committed)
        {
            observedCount = committed;
        }

        return observedCount;
    }

    internal long ByteSize => Volatile.Read(ref _ByteSize);

    internal int StagedCount => _StagedCount;

    internal ushort OccurrenceInPacket => _OccurrenceInPacket;

    internal ValueCacheCapacity Capacity { get; }

    internal ChunkedGrowOnlyStore<int> PacketIds => _PacketIds;

    internal ChunkedGrowOnlyStore<long> Timestamps => _Timestamps;

    #endregion

    #region Public API

    internal void BeginPacket()
    {
        _StagedThisPacket = 0;
        _OccurrenceInPacket = 0;
        _BytesChargedThisPacket = 0;
    }

    /// <summary>
    /// Decides whether this occurrence should write a new row, overwrite the last staged row, or skip.
    /// Returns <see langword="false"/> when the caller must not write.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPrepareStage(out int index, out bool overwrite)
    {
        index = 0;
        overwrite = false;

        if (Capacity.IsReached)
        {
            return false;
        }

        switch (CaptureMode)
        {
            case ValueCaptureMode.FirstOccurrence:
                if (_OccurrenceInPacket != 0)
                {
                    return false;
                }

                break;
            case ValueCaptureMode.LastOccurrence:
                if (_OccurrenceInPacket != 0)
                {
                    overwrite = true;
                    index = _StagedCount - 1;
                    return true;
                }

                break;
            default:
                if (_OccurrenceInPacket == ushort.MaxValue)
                {
                    return false;
                }

                break;
        }

        if (_StagedCount == ArrayIndexIdRange.MaxValue || Capacity.WouldExceedRowCount())
        {
            Capacity.MarkReached();
            return false;
        }

        index = _StagedCount;
        return true;
    }

    internal bool TryChargeNewRow(long addedBytes, int packetId, long timestampNanos, out int index)
    {
        index = _StagedCount;
        if (addedBytes < 0 || Capacity.WouldExceedBytes(addedBytes))
        {
            Capacity.MarkReached();
            return false;
        }

        _PacketIds.Set(index, packetId);
        _Timestamps.Set(index, timestampNanos);
        _CompleteNewRow(addedBytes);
        return true;
    }

    internal void CompleteOverwrite(long addedBytes)
    {
        if (addedBytes > 0)
        {
            if (Capacity.WouldExceedBytes(addedBytes))
            {
                Capacity.MarkReached();
                return;
            }

            _AddCharged(addedBytes);
        }

        if (_OccurrenceInPacket == 0)
        {
            _OccurrenceInPacket = 1;
        }
    }

    internal void Commit()
    {
        if (Capacity.IsReached && _StagedThisPacket > 0)
        {
            _RetractStagedThisPacket();
            return;
        }

        _CommittedCount = _StagedCount;
        _StagedThisPacket = 0;
        _OccurrenceInPacket = 0;
        _BytesChargedThisPacket = 0;
    }

    /// <summary>
    /// Retracts the LastOccurrence row staged in this packet (CustomText cleared).
    /// No-op when nothing was staged this packet.
    /// </summary>
    internal void RetractLastOccurrenceRow()
    {
        if (CaptureMode != ValueCaptureMode.LastOccurrence || _StagedThisPacket <= 0)
        {
            return;
        }

        _RetractStagedThisPacket();
        _OccurrenceInPacket = 0;
    }

    internal bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span) =>
        _PacketIds.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

    internal bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _Timestamps.TryGetPublishedChunk(chunkIndex, ClipObservedCount(observedCount), out span);

    internal int GetPacketId(int index) => _PacketIds.Get(index);

    internal long GetTimestamp(int index) => _Timestamps.Get(index);

    /// <summary>
    /// C13 charge for a new row: packet id + timestamp + value slots + payload + new inner chunks
    /// when <paramref name="stagedCount"/> is a multiple of <see cref="ChunkSize"/>.
    /// </summary>
    internal static long ComputeNewRowCharge(int stagedCount, int valueSlotBytes, int extraPayloadBytes, int extraChunkBytes)
    {
        try
        {
            return checked(_ComputeNewRowChargeUnchecked(stagedCount, valueSlotBytes, extraPayloadBytes, extraChunkBytes));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    internal static int StringHeapBytes(int charCount)
    {
        try
        {
            return checked(StringObjectOverheadBytes + (2 * charCount));
        }
        catch (OverflowException)
        {
            return int.MaxValue;
        }
    }

    #endregion

    #region Private helpers

    private static long _ComputeNewRowChargeUnchecked(int stagedCount, int valueSlotBytes, int extraPayloadBytes, int extraChunkBytes)
    {
        long charge = PacketIdSlotBytes + TimestampSlotBytes + valueSlotBytes + extraPayloadBytes;
        if (stagedCount % ChunkSize == 0)
        {
            charge += ChunkSize * PacketIdSlotBytes;
            charge += ChunkSize * TimestampSlotBytes;
            charge += extraChunkBytes;
        }

        return charge;
    }

    private void _CompleteNewRow(long addedBytes)
    {
        _AddCharged(addedBytes);
        _StagedCount++;
        _StagedThisPacket++;
        Capacity.AddStagedRow();
        if (_OccurrenceInPacket < ushort.MaxValue)
        {
            _OccurrenceInPacket++;
        }
    }

    private void _AddCharged(long addedBytes)
    {
        Capacity.AddBytes(addedBytes);
        long current = Volatile.Read(ref _ByteSize);
        long next;
        try
        {
            next = checked(current + addedBytes);
        }
        catch (OverflowException)
        {
            Capacity.MarkReached();
            return;
        }

        Volatile.Write(ref _ByteSize, next);
        _BytesChargedThisPacket += addedBytes;
    }

    private void _RetractStagedThisPacket()
    {
        int retract = _StagedThisPacket;
        if (retract <= 0)
        {
            _StagedThisPacket = 0;
            _BytesChargedThisPacket = 0;
            return;
        }

        _StagedCount -= retract;
        if (_StagedCount < 0)
        {
            _StagedCount = 0;
        }

        Capacity.RemoveStagedRows(retract);
        long refund = _BytesChargedThisPacket;
        Capacity.SubtractBytes(refund);
        long current = Volatile.Read(ref _ByteSize);
        long next = current - refund;
        if (next < 0)
        {
            next = 0;
        }

        Volatile.Write(ref _ByteSize, next);
        _StagedThisPacket = 0;
        _BytesChargedThisPacket = 0;
        _OccurrenceInPacket = 0;
    }

    #endregion
}
