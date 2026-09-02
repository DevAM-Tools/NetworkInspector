// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Bytes series matching <see cref="FieldValueData"/> packed <c>_Data</c> (offset|length) plus a
/// <c>_Ref</c> byte-array column. Payloads are copied out of frame memory so later buffer reuse
/// cannot mutate published rows.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. See <see cref="ValueCacheSeries"/>.
/// </para>
/// </summary>
public sealed class ValueCacheBytesSeries : ValueCacheSeries
{
    #region Constants

    private const int _DataSlotBytes = 8;
    private const int _RefSlotBytes = 8;
    private const int _ValueSlotBytes = _DataSlotBytes + _RefSlotBytes;
    private const int _ExtraChunkBytes = ValueCacheColumnState.ChunkSize * _ValueSlotBytes;
    private const int _ArenaChunkBytes = 65536;

    #endregion

    #region Fields

    private readonly ValueCacheColumnState _State;
    private readonly ChunkedGrowOnlyStore<ulong> _Data;
    private readonly ChunkedGrowOnlyStore<byte[]?> _Refs;

    private byte[]? _Arena;
    private int _ArenaOffset;

    #endregion

    #region Constructors

    internal ValueCacheBytesSeries(ValueCacheCapacity capacity, FieldId fieldId, ValueCaptureMode captureMode)
    {
        FieldId = fieldId;
        _State = new(capacity, captureMode);
        _Data = new(ValueCacheColumnState.ChunkShift);
        _Refs = new(ValueCacheColumnState.ChunkShift);
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override FieldId FieldId { get; }

    /// <inheritdoc/>
    public override FieldType FieldType => FieldType.Bytes;

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

    /// <summary>Published packed offset|length chunk clipped to <paramref name="observedCount"/>.</summary>
    public bool TryGetDataChunk(int chunkIndex, int observedCount, out ReadOnlySpan<ulong> span) =>
        _Data.TryGetPublishedChunk(chunkIndex, _State.ClipObservedCount(observedCount), out span);

    /// <summary>Published byte-array ref chunk clipped to <paramref name="observedCount"/>.</summary>
    public bool TryGetRefChunk(int chunkIndex, int observedCount, out ReadOnlySpan<byte[]?> span) =>
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
    /// Copies the published bytes at <paramref name="index"/>, matching
    /// <see cref="FieldValueData.TryGetAsBytes"/>.
    /// </summary>
    public bool TryGetAsBytes(int index, out ReadOnlyMemory<byte> value)
    {
        if ((uint)index >= (uint)Count)
        {
            value = default;
            return false;
        }

        byte[]? array = _Refs.Get(index);
        if (array is null)
        {
            value = default;
            return false;
        }

        ulong packed = _Data.Get(index);
        int offset = (int)(packed & 0xFFFFFFFF);
        int length = (int)(packed >> 32);
        value = new ReadOnlyMemory<byte>(array, offset, length);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stage(int packetId, long timestampNanos, ReadOnlySpan<byte> payload)
    {
        if (!_State.TryPrepareStage(out int index, out bool overwrite))
        {
            return;
        }

        int length = payload.Length;
        int arenaCharge = 0;
        if (!overwrite)
        {
            arenaCharge = _ArenaChargeForCopy(length);
        }

        if (overwrite)
        {
            int extra = length + _ArenaChargeForCopy(length);
            if (_State.Capacity.WouldExceedBytes(extra))
            {
                _State.Capacity.MarkReached();
                return;
            }

            _WriteCopy(index, payload, out int chargedArena);
            _State.CompleteOverwrite(length + chargedArena);
            return;
        }

        long addedBytes = ValueCacheColumnState.ComputeNewRowCharge(
            _State.StagedCount, _ValueSlotBytes, length, _ExtraChunkBytes);
        if (addedBytes == long.MaxValue || _State.Capacity.WouldExceedBytes(addedBytes))
        {
            _State.Capacity.MarkReached();
            return;
        }

        try
        {
            addedBytes = checked(addedBytes + arenaCharge);
        }
        catch (OverflowException)
        {
            _State.Capacity.MarkReached();
            return;
        }

        if (_State.Capacity.WouldExceedBytes(addedBytes))
        {
            _State.Capacity.MarkReached();
            return;
        }

        _WriteCopy(index, payload, out _);
        _ = _State.TryChargeNewRow(addedBytes, packetId, timestampNanos, out _);
    }

    #endregion

    #region Internal lifecycle

    internal override void BeginPacket() => _State.BeginPacket();

    internal override void Commit() => _State.Commit();

    #endregion

    #region Private helpers

    private int _ArenaChargeForCopy(int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        if (length > _ArenaChunkBytes)
        {
            return 0;
        }

        byte[]? arena = _Arena;
        if (arena is null || _ArenaOffset > arena.Length - length)
        {
            return _ArenaChunkBytes;
        }

        return 0;
    }

    private void _WriteCopy(int index, ReadOnlySpan<byte> payload, out int chargedArena)
    {
        chargedArena = 0;
        int length = payload.Length;
        if (length == 0)
        {
            _Data.Set(index, 0);
            _Refs.Set(index, []);
            return;
        }

        if (length > _ArenaChunkBytes)
        {
            byte[] dedicated = new byte[length];
            payload.CopyTo(dedicated);
            ulong packedDedicated = (ulong)(uint)length << 32;
            _Data.Set(index, packedDedicated);
            _Refs.Set(index, dedicated);
            return;
        }

        byte[]? arena = _Arena;
        if (arena is null || _ArenaOffset > arena.Length - length)
        {
            arena = new byte[_ArenaChunkBytes];
            _Arena = arena;
            _ArenaOffset = 0;
            chargedArena = _ArenaChunkBytes;
        }

        int offset = _ArenaOffset;
        payload.CopyTo(arena.AsSpan(offset, length));
        _ArenaOffset = offset + length;
        ulong packed = (ulong)(uint)offset | ((ulong)(uint)length << 32);
        _Data.Set(index, packed);
        _Refs.Set(index, arena);
    }

    private void _ThrowIfNotPublished(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be in the published range.");
        }
    }

    #endregion
}
