// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// String series matching <see cref="FieldValueData"/> string layout (ref column of interned/copied strings).
/// Used for <see cref="FieldType.String"/> payloads, field <c>CustomText</c>, and value
/// <c>CustomRepresentation</c>.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. See <see cref="ValueCacheSeries"/>.
/// </para>
/// </summary>
public sealed class ValueCacheStringSeries : ValueCacheSeries
{
    #region Constants

    private const int _RefSlotBytes = 8;
    private const int _ExtraChunkBytes = ValueCacheColumnState.ChunkSize * _RefSlotBytes;

    #endregion
    #region Fields
    private readonly ValueCacheColumnState _State;
    private readonly ChunkedGrowOnlyStore<string?> _Refs;

    #endregion

    #region Constructors

    internal ValueCacheStringSeries(
        ValueCacheCapacity capacity,
        FieldId fieldId,
        FieldType fieldType,
        ValueCaptureMode captureMode)
    {
        FieldId = fieldId;
        FieldType = fieldType;
        _State = new(capacity, captureMode);
        _Refs = new(ValueCacheColumnState.ChunkShift);
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

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span) =>
        _State.TryGetPacketIdChunk(chunkIndex, observedCount, out span);

    /// <inheritdoc/>
    public override bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _State.TryGetTimestampChunk(chunkIndex, observedCount, out span);

    /// <summary>Published string-ref chunk clipped to <paramref name="observedCount"/>.</summary>
    public bool TryGetRefChunk(int chunkIndex, int observedCount, out ReadOnlySpan<string?> span) =>
        _Refs.TryGetPublishedChunk(chunkIndex, _State.ClipObservedCount(observedCount), out span);

    /// <summary>
    /// Packet id of the committed row at <paramref name="index"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not committed.</exception>
    public int GetPacketId(int index)
    {
        _ThrowIfNotPublished(index);
        return _State.GetPacketId(index);
    }

    /// <summary>
    /// Timestamp in nanoseconds of the committed row at <paramref name="index"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not committed.</exception>
    public long GetTimestampNanos(int index)
    {
        _ThrowIfNotPublished(index);
        return _State.GetTimestamp(index);
    }

    /// <summary>
    /// Copies the published string at <paramref name="index"/>, matching
    /// <see cref="FieldValueData.TryGetAsString"/>.
    /// </summary>
    public bool TryGetAsString(int index, out string value)
    {
        if ((uint)index >= (uint)Count)
        {
            value = string.Empty;
            return false;
        }

        string? stored = _Refs.Get(index);
        if (stored is null)
        {
            value = string.Empty;
            return false;
        }

        value = stored;
        return true;
    }

    /// <summary>
    /// Stages a non-null string. Null / empty-skip is handled by the caller for CustomText
    /// (FirstOccurrence / AllOccurrences no-op; LastOccurrence retract uses <see cref="RetractLastOccurrenceRow"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stage(int packetId, long timestampNanos, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_State.TryPrepareStage(out int index, out bool overwrite))
        {
            return;
        }

        int heapBytes = ValueCacheColumnState.StringHeapBytes(value.Length);
        if (overwrite)
        {
            if (_State.Capacity.WouldExceedBytes(heapBytes))
            {
                _State.Capacity.MarkReached();
                return;
            }

            _Refs.Set(index, value);
            _State.CompleteOverwrite(heapBytes);
            return;
        }

        long addedBytes = ValueCacheColumnState.ComputeNewRowCharge(
            _State.StagedCount, _RefSlotBytes, heapBytes, _ExtraChunkBytes);
        if (addedBytes == long.MaxValue || _State.Capacity.WouldExceedBytes(addedBytes))
        {
            _State.Capacity.MarkReached();
            return;
        }

        _Refs.Set(index, value);
        _ = _State.TryChargeNewRow(addedBytes, packetId, timestampNanos, out _);
    }

    /// <summary>Stages from a <see cref="LazyString"/>; no-op when <see cref="LazyString.IsNull"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stage(int packetId, long timestampNanos, LazyString text)
    {
        if (text.IsNull)
        {
            return;
        }

        Stage(packetId, timestampNanos, text.AsString);
    }

    internal void RetractLastOccurrenceRow() => _State.RetractLastOccurrenceRow();

    internal bool HasStagedThisPacket => _State.OccurrenceInPacket != 0;

    #endregion

    #region Internal lifecycle

    internal override void BeginPacket() => _State.BeginPacket();

    internal override void Commit() => _State.Commit();

    #endregion

    #region Private helpers

    private void _ThrowIfNotPublished(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");
        }
    }

    #endregion
}
