// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Growable typed array builder for a single field's cache values.
/// Uses doubling growth strategy with direct arrays.
/// Handles type-specific extraction from <see cref="FieldValueData"/> and compact-mode clamping.
/// </summary>
internal sealed class ValueCacheFieldBuilder<T> : IValueCacheFieldBuilderBase where T : unmanaged
{
    #region Fields

    private readonly FieldId _FieldId;
    private readonly FieldType _OriginalFieldType;
    private readonly ValueCacheStorageMode _StorageMode;
    private readonly Func<FieldValueData, (T Value, bool Overflow)> _Extractor;

    private long[] _Timestamps;
    private int[] _PacketIds;
    private T[] _Values;
    private int _Count;
    private long _LastTimestamp;
    private ValueCacheCompleteness _Completeness;

    // For CompactFloat + Timestamp: the base timestamp (seconds since epoch)
    private double _BaseTimestamp;
    private bool _BaseTimestampSet;

    #endregion

    #region Constructors

    /// <summary>Creates a typed field builder with the given value extractor.</summary>
    internal ValueCacheFieldBuilder(
        FieldId fieldId,
        FieldType originalFieldType,
        ValueCacheStorageMode storageMode,
        Func<FieldValueData, (T Value, bool Overflow)> extractor,
        int initialCapacity = 0)
    {
        _FieldId = fieldId;
        _OriginalFieldType = originalFieldType;
        _StorageMode = storageMode;
        _Extractor = extractor;
        _Timestamps = new long[initialCapacity];
        _PacketIds = new int[initialCapacity];
        _Values = new T[initialCapacity];
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public FieldId FieldId => _FieldId;

    /// <inheritdoc/>
    public FieldType OriginalFieldType => _OriginalFieldType;

    /// <inheritdoc/>
    public ValueCacheStorageMode StorageMode => _StorageMode;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFromFieldValue(long timestamp, int packetId, in FieldValueData value)
    {
        // Monotonic enforcement: skip out-of-order timestamps
        if (_Count > 0 && timestamp < _LastTimestamp)
        {
            _Completeness |= ValueCacheCompleteness.HasTimestampSkips;
            return;
        }
        _LastTimestamp = timestamp;

        // Extract and convert value
        (T converted, bool overflow) = _Extractor(value);
        if (overflow)
        {
            _Completeness |= ValueCacheCompleteness.HasOverflow;
        }

        // Grow if needed
        if (_Count == _Timestamps.Length)
        {
            Grow();
        }

        _Timestamps[_Count] = timestamp;
        _PacketIds[_Count] = packetId;
        _Values[_Count] = converted;
        _Count++;
    }

    /// <inheritdoc/>
    public void MarkDuplicateDrop() => _Completeness |= ValueCacheCompleteness.HasDuplicateDrops;

    /// <inheritdoc/>
    public void MarkEvictedPacket() => _Completeness |= ValueCacheCompleteness.HasEvictedPackets;

    /// <inheritdoc/>
    public ValueCacheSeries BuildSeries()
    {
        // Hand over the (possibly over-allocated) backing arrays directly. The series
        // ctor accepts a separate count, avoiding three full-array copies for a builder
        // that allocated more capacity than was filled.
        return new ValueCacheSeries(
            _FieldId, _OriginalFieldType, _StorageMode,
            _Timestamps, _PacketIds,
            new ValueCacheData(_Values),
            _Completeness,
            count: _Count,
            baseTimestamp: _BaseTimestamp);
    }

    /// <summary>Sets the base timestamp for CompactFloat Timestamp mode.</summary>
    internal void SetBaseTimestamp(double baseTimestamp)
    {
        _BaseTimestamp = baseTimestamp;
        _BaseTimestampSet = true;
    }

    /// <summary>Whether the base timestamp has been set.</summary>
    internal bool HasBaseTimestamp => _BaseTimestampSet;

    #endregion

    #region Private Helpers

    private void Grow()
    {
        int newCapacity = Math.Max(_Timestamps.Length * 2, 256);
        Array.Resize(ref _Timestamps, newCapacity);
        Array.Resize(ref _PacketIds, newCapacity);
        Array.Resize(ref _Values, newCapacity);
    }

    #endregion
}
