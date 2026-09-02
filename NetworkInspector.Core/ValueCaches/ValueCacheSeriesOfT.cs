// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// SoA series of unmanaged payload values plus packet id and timestamp columns.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. See <see cref="ValueCacheSeries"/>.
/// </para>
/// </summary>
/// <typeparam name="T">Minimal payload type selected from the stack <see cref="FieldType"/>.</typeparam>
public sealed class ValueCacheSeries<T> : ValueCacheSeries
    where T : unmanaged
{
    #region Fields

    private readonly ValueCacheColumnState _State;
    private readonly ChunkedGrowOnlyStore<T> _Values;
    private readonly int _ValueSlotBytes;
    private readonly int _ExtraChunkBytes;

    #endregion

    #region Constructors

    internal ValueCacheSeries(ValueCacheCapacity capacity, FieldId fieldId, FieldType fieldType, ValueCaptureMode captureMode)
    {
        FieldId = fieldId;
        FieldType = fieldType;
        _State = new(capacity, captureMode);
        _Values = new(ValueCacheColumnState.ChunkShift);
        _ValueSlotBytes = Unsafe.SizeOf<T>();
        _ExtraChunkBytes = ValueCacheColumnState.ChunkSize * _ValueSlotBytes;
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override FieldId FieldId { get; }

    /// <inheritdoc/>
    public override FieldType FieldType { get; }

    /// <inheritdoc/>
    public override ValueCaptureMode CaptureMode => _State.CaptureMode;

    /// <inheritdoc/>
    public override int Count => _State.CommittedCount;

    /// <inheritdoc/>
    public override long ByteSize => _State.ByteSize;

    /// <summary>
    /// Gathered row at <paramref name="index"/>. Throws when <paramref name="index"/> is not strictly less than <see cref="Count"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not committed.</exception>
    public ValueCacheRow<T> this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");
            }

            return new ValueCacheRow<T>(_State.GetPacketId(index), _State.GetTimestamp(index), _Values.Get(index));
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

    /// <summary>
    /// Returns the published value chunk at <paramref name="chunkIndex"/> clipped to
    /// the lesser of <paramref name="observedCount"/> and the committed <see cref="Count"/>.
    /// A caller who passes <see cref="int.MaxValue"/> still cannot see staged or unset slots.
    /// </summary>
    public bool TryGetValueChunk(int chunkIndex, int observedCount, out ReadOnlySpan<T> span) =>
        _Values.TryGetPublishedChunk(chunkIndex, _State.ClipObservedCount(observedCount), out span);

    /// <summary>Stages one occurrence. Writer only. No-op when capacity is reached or the capture mode skips this occurrence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stage(int packetId, long timestampNanos, T value)
    {
        if (!_State.TryPrepareStage(out int index, out bool overwrite))
        {
            return;
        }

        if (overwrite)
        {
            _Values.Set(index, value);
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

        _Values.Set(index, value);
        _ = _State.TryChargeNewRow(addedBytes, packetId, timestampNanos, out _);
    }

    #endregion

    #region Internal lifecycle

    internal override void BeginPacket() => _State.BeginPacket();

    internal override void Commit() => _State.Commit();

    #endregion
}
