// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Builder for 128-bit types (IPv6Address, Uuid) stored as dual <see langword="ulong"/>[] arrays (low + high).
/// </summary>
internal sealed class ValueCacheFieldBuilder128 : IValueCacheFieldBuilderBase
{
    #region Fields

    private readonly FieldId _FieldId;
    private readonly FieldType _OriginalFieldType;
    private readonly Func<FieldValueData, (ulong Low, ulong High)> _Extractor;

    private long[] _Timestamps;
    private int[] _PacketIds;
    private ulong[] _ValuesLow;
    private ulong[] _ValuesHigh;
    private int _Count;
    private long _LastTimestamp;
    private ValueCacheCompleteness _Completeness;

    #endregion

    #region Constructors

    /// <summary>Creates a 128-bit field builder with the given dual-value extractor.</summary>
    internal ValueCacheFieldBuilder128(
        FieldId fieldId,
        FieldType originalFieldType,
        Func<FieldValueData, (ulong Low, ulong High)> extractor,
        int initialCapacity = 0)
    {
        _FieldId = fieldId;
        _OriginalFieldType = originalFieldType;
        _Extractor = extractor;
        _Timestamps = new long[initialCapacity];
        _PacketIds = new int[initialCapacity];
        _ValuesLow = new ulong[initialCapacity];
        _ValuesHigh = new ulong[initialCapacity];
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public FieldId FieldId => _FieldId;

    /// <inheritdoc/>
    public FieldType OriginalFieldType => _OriginalFieldType;

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

        (ulong low, ulong high) = _Extractor(value);

        // Grow if needed
        if (_Count == _Timestamps.Length)
        {
            Grow();
        }

        _Timestamps[_Count] = timestamp;
        _PacketIds[_Count] = packetId;
        _ValuesLow[_Count] = low;
        _ValuesHigh[_Count] = high;
        _Count++;
    }

    /// <inheritdoc/>
    public void MarkDuplicateDrop() => _Completeness |= ValueCacheCompleteness.HasDuplicateDrops;

    /// <inheritdoc/>
    public void MarkEvictedPacket() => _Completeness |= ValueCacheCompleteness.HasEvictedPackets;

    /// <inheritdoc/>
    public ValueCacheSeries BuildSeries()
    {
        // Hand over the (possibly over-allocated) backing arrays directly via the
        // count-aware ctor. Avoids four full-array copies (timestamps, packetIds,
        // low, high) for builders that allocated more capacity than was filled.
        return new ValueCacheSeries(
            _FieldId, _OriginalFieldType, ValueCacheStorageMode.Native,
            _Timestamps, _PacketIds,
            new ValueCacheData(_ValuesLow, _ValuesHigh),
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
        Array.Resize(ref _ValuesLow, newCapacity);
        Array.Resize(ref _ValuesHigh, newCapacity);
    }

    #endregion
}
