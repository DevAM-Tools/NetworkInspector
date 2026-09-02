// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// SoA series for <see cref="FieldType.Uuid"/> payloads stored as high/low <see cref="ulong"/> columns.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. See <see cref="ValueCacheSeries"/>.
/// </para>
/// </summary>
public sealed class ValueCacheUuidSeries : ValueCacheSeries
{
    #region Constants

    private const int _ValueSlotBytes = 16;
    private const int _ExtraChunkBytes = ValueCacheColumnState.ChunkSize * _ValueSlotBytes;

    #endregion

    #region Fields

    private readonly ValueCacheColumnState _State;
    private readonly ChunkedGrowOnlyStore<ulong> _High;
    private readonly ChunkedGrowOnlyStore<ulong> _Low;

    #endregion

    #region Constructors

    internal ValueCacheUuidSeries(ValueCacheCapacity capacity, FieldId fieldId, ValueCaptureMode captureMode)
    {
        FieldId = fieldId;
        _State = new(capacity, captureMode);
        _High = new(ValueCacheColumnState.ChunkShift);
        _Low = new(ValueCacheColumnState.ChunkShift);
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override FieldId FieldId { get; }

    /// <inheritdoc/>
    public override FieldType FieldType => FieldType.Uuid;

    /// <inheritdoc/>
    public override ValueCaptureMode CaptureMode => _State.CaptureMode;

    /// <inheritdoc/>
    public override int Count => _State.CommittedCount;

    /// <inheritdoc/>
    public override long ByteSize => _State.ByteSize;

    /// <summary>
    /// Gathered UUID row at <paramref name="index"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not committed.</exception>
    public (int PacketId, long TimestampNanos, Uuid Value) this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");
            }

            return (_State.GetPacketId(index), _State.GetTimestamp(index), new Uuid(_High.Get(index), _Low.Get(index)));
        }
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span) =>
        _State.TryGetPacketIdChunk(chunkIndex, observedCount, out span);

    /// <inheritdoc/>
    public override bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _State.TryGetTimestampChunk(chunkIndex, observedCount, out span);

    /// <summary>Published high-64-bit chunk clipped to <paramref name="observedCount"/>.</summary>
    public bool TryGetHighChunk(int chunkIndex, int observedCount, out ReadOnlySpan<ulong> span) =>
        _High.TryGetPublishedChunk(chunkIndex, _State.ClipObservedCount(observedCount), out span);

    /// <summary>Published low-64-bit chunk clipped to <paramref name="observedCount"/>.</summary>
    public bool TryGetLowChunk(int chunkIndex, int observedCount, out ReadOnlySpan<ulong> span) =>
        _Low.TryGetPublishedChunk(chunkIndex, _State.ClipObservedCount(observedCount), out span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stage(int packetId, long timestampNanos, ulong high, ulong low)
    {
        if (!_State.TryPrepareStage(out int index, out bool overwrite))
        {
            return;
        }

        if (overwrite)
        {
            _High.Set(index, high);
            _Low.Set(index, low);
            _State.CompleteOverwrite(0);
            return;
        }

        long addedBytes = ValueCacheColumnState.ComputeNewRowCharge(
            _State.StagedCount, _ValueSlotBytes, extraPayloadBytes: 0, _ExtraChunkBytes);
        if (addedBytes == long.MaxValue || _State.Capacity.WouldExceedBytes(addedBytes))
        {
            _State.Capacity.MarkReached();
            return;
        }

        _High.Set(index, high);
        _Low.Set(index, low);
        _ = _State.TryChargeNewRow(addedBytes, packetId, timestampNanos, out _);
    }

    #endregion

    #region Internal lifecycle

    internal override void BeginPacket() => _State.BeginPacket();

    internal override void Commit() => _State.Commit();

    #endregion
}
