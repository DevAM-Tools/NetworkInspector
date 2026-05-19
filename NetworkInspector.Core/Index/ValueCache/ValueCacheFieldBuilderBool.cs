// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Builder for Bool fields stored as a dense bit-packed <see langword="byte"/>[] array.
/// Only <see langword="true"/> values set the corresponding bit; the entry index
/// (not PacketId) is used as the bit position.
/// </summary>
internal sealed class ValueCacheFieldBuilderBool : IValueCacheFieldBuilderBase
{
    #region Fields

    private readonly FieldId _FieldId;

    /// <summary>Dense bit array — bit at position [entryIndex] is set for true values.</summary>
    private byte[] _BoolBits;

    private long[] _Timestamps;
    private int[] _PacketIds;
    private int _Count;
    private long _LastTimestamp;
    private ValueCacheCompleteness _Completeness;

    #endregion

    #region Constructors

    /// <summary>Creates a Bool field builder.</summary>
    internal ValueCacheFieldBuilderBool(FieldId fieldId, int initialCapacity = 0)
    {
        _FieldId = fieldId;
        _BoolBits = new byte[(initialCapacity + 7) >> 3];
        _Timestamps = new long[initialCapacity];
        _PacketIds = new int[initialCapacity];
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public FieldId FieldId => _FieldId;

    /// <inheritdoc/>
    public FieldType OriginalFieldType => FieldType.Bool;

    /// <inheritdoc/>
    public ValueCacheStorageMode StorageMode => ValueCacheStorageMode.Native;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFromFieldValue(long timestamp, int packetId, in FieldValueData value)
    {
        // Monotonic enforcement
        if (_Count > 0 && timestamp < _LastTimestamp)
        {
            _Completeness |= ValueCacheCompleteness.HasTimestampSkips;
            return;
        }
        _LastTimestamp = timestamp;

        // Grow if needed
        if (_Count == _Timestamps.Length)
        {
            Grow();
        }

        _Timestamps[_Count] = timestamp;
        _PacketIds[_Count] = packetId;

        // Set the bit for true values in the dense bit array
        if (value.TryGetAsBool(out bool boolVal) && boolVal)
        {
            _BoolBits[_Count >> 3] |= (byte)(1 << (_Count & 7));
        }

        _Count++;
    }

    /// <inheritdoc/>
    public void MarkDuplicateDrop() => _Completeness |= ValueCacheCompleteness.HasDuplicateDrops;

    /// <inheritdoc/>
    public void MarkEvictedPacket() => _Completeness |= ValueCacheCompleteness.HasEvictedPackets;

    /// <inheritdoc/>
    public ValueCacheSeries BuildSeries()
    {
        // Hand over the (possibly over-allocated) backing arrays directly. The bit-packed
        // bool buffer is sized in bytes; the count-aware ctor only requires that the
        // existing buffer holds at least ((count + 7) / 8) bytes — which is guaranteed
        // because Grow() already maintains that invariant.
        return new ValueCacheSeries(
            _FieldId, FieldType.Bool, ValueCacheStorageMode.Native,
            _Timestamps, _PacketIds,
            new ValueCacheData(_BoolBits),
            _Completeness,
            count: _Count);
    }

    #endregion

    #region Private Helpers

    private void Grow()
    {
        int newCapacity = Math.Max(_Timestamps.Length * 2, 256);
        Array.Resize(ref _Timestamps, newCapacity);
        Array.Resize(ref _PacketIds, newCapacity);
        Array.Resize(ref _BoolBits, (newCapacity + 7) >> 3);
    }

    #endregion
}