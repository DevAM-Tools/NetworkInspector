// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Writer portion of <see cref="ValueCacheSeries"/>.
/// All methods in this file are called under ParseLock only (single-writer).
/// </summary>
public sealed partial class ValueCacheSeries
{
    #region Writer Methods

    // ── Writer Methods (called under ParseLock ONLY) ─────────

    /// <summary>
    /// Appends a value to the series. Called by <see cref="ValueCacheBuilder"/> during live parsing.
    /// Enforces monotonic timestamps: skips entry if timestamp is less than the last appended timestamp.
    /// Handles type-specific value extraction and storage via internal switch on
    /// (<see cref="OriginalFieldType"/>, <see cref="StorageMode"/>).
    ///
    /// <para>On success: captures <c>writeIndex = Volatile.Read(_Count)</c>, writes data at <c>writeIndex</c>,
    /// then publishes via <c>Volatile.Write(ref _Count, writeIndex + 1)</c>.</para>
    /// <para>On capacity overflow: calls <see cref="Grow"/> first (allocates new arrays, copies data).</para>
    /// </summary>
    internal bool TryAppend(long timestampNanos, int packetId, in FieldValueData value)
    {
        // Capture current count once via Volatile.Read (acquire fence) — satisfies the volatile-only contract
        // for _Count. Single-writer semantics (ParseLock) guarantee this value cannot change during this call.
        int writeIndex = Volatile.Read(ref Unsafe.AsRef(in _Count));

        // Monotonic enforcement: skip out-of-order timestamps
        if (writeIndex > 0 && timestampNanos < _LastTimestamp)
        {
            // Use Interlocked.Or to ensure the flag write is visible to reader threads
            // even though no Volatile.Write(_Count) follows this early-return path.
            Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasTimestampSkips);
            return false;
        }
        _LastTimestamp = timestampNanos;

        // Grow if needed — pass writeIndex so Grow does not re-read _Count directly
        if (writeIndex >= _Capacity)
        {
            Grow(writeIndex);
        }

        // Write common arrays at position writeIndex
        _Timestamps[writeIndex] = timestampNanos;
        _PacketIds[writeIndex] = packetId;

        // Write type-specific value — may set overflow flag; pass writeIndex to avoid re-reading _Count
        if (!AppendValue(writeIndex, value))
        {
            return false;
        }

        // Publish: release fence ensures all prior writes are visible to readers before count increment
        Volatile.Write(ref _Count, writeIndex + 1);
        return true;
    }

    /// <summary>Marks that a duplicate value was dropped for the current packet.
    /// Uses <see cref="System.Threading.Interlocked"/> Or to guarantee the flag is visible to reader threads
    /// even when no subsequent <see cref="System.Threading.Volatile"/> Write on <c>_Count</c> follows.</summary>
    internal void MarkDuplicateDrop() =>
        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasDuplicateDrops);
    #endregion

    #region Private Helpers
    // ── Private Helpers ──────────────────────────────────────

    /// <summary>
    /// Grows all parallel arrays to 2x capacity (minimum 256).
    /// Allocates new arrays, copies existing data, replaces references.
    /// Old readers with former array refs are safe — data persists until GC.
    /// </summary>
    /// <param name="currentCount">
    /// The number of valid entries already written (caller's writeIndex snapshot),
    /// passed in to avoid re-reading <c>_Count</c> directly inside this method.
    /// </param>
    private void Grow(int currentCount)
    {
        int newCapacity = Math.Max(_Capacity * 2, 256);

        // Grow timestamps and packetIds
        long[] newTimestamps = new long[newCapacity];
        int[] newPacketIds = new int[newCapacity];
        if (currentCount > 0)
        {
            Array.Copy(_Timestamps, newTimestamps, currentCount);
            Array.Copy(_PacketIds, newPacketIds, currentCount);
        }
        _Timestamps = newTimestamps;
        _PacketIds = newPacketIds;

        // Grow type-specific value arrays
        GrowValues(newCapacity, currentCount);
        _Capacity = newCapacity;
    }

    /// <summary>
    /// Type-specific grow for the value arrays in <see cref="ValueCacheData"/>.
    /// Switches on (<see cref="OriginalFieldType"/>, <see cref="StorageMode"/>) to cast and copy typed arrays.
    /// </summary>
    /// <param name="newCapacity">New array capacity to allocate.</param>
    /// <param name="currentCount">
    /// Number of valid entries to copy from old to new arrays,
    /// passed in to avoid re-reading <c>_Count</c> directly.
    /// </param>
    private void GrowValues(int newCapacity, int currentCount)
    {
        // Bool uses bit-packed byte array: 1 bit per entry, 8 entries per byte.
        if (_OriginalFieldType == FieldType.Bool)
        {
            int newByteCount = (newCapacity + 7) >> 3;
            byte[] oldBits = _Data.AsBoolBits();
            byte[] newBits = new byte[newByteCount];
            if (oldBits.Length > 0)
            {
                Array.Copy(oldBits, newBits, Math.Min(oldBits.Length, newByteCount));
            }
            _Data = new ValueCacheData(newBits);
            return;
        }

        // 128-bit types: dual ulong[] arrays
        if (_OriginalFieldType is FieldType.IPv6Address or FieldType.Uuid)
        {
            (ulong[] oldLow, ulong[] oldHigh) = _Data.AsDualUlong();
            ulong[] newLow = new ulong[newCapacity];
            ulong[] newHigh = new ulong[newCapacity];
            if (currentCount > 0)
            {
                Array.Copy(oldLow, newLow, currentCount);
                Array.Copy(oldHigh, newHigh, currentCount);
            }
            _Data = new ValueCacheData(newLow, newHigh);
            return;
        }

        // All other types: single typed array
        _Data = (_OriginalFieldType, _StorageMode) switch
        {
            (FieldType.U64, ValueCacheStorageMode.Native) => GrowSingleArray<ulong>(newCapacity, currentCount),
            (FieldType.I64, ValueCacheStorageMode.Native) => GrowSingleArray<long>(newCapacity, currentCount),
            (FieldType.F64, ValueCacheStorageMode.Native) => GrowSingleArray<double>(newCapacity, currentCount),
            (FieldType.Timestamp, ValueCacheStorageMode.Native) => GrowSingleArray<long>(newCapacity, currentCount),
            (FieldType.IPv4Address, ValueCacheStorageMode.Native) => GrowSingleArray<uint>(newCapacity, currentCount),
            (FieldType.MacAddress, ValueCacheStorageMode.Native) => GrowSingleArray<ulong>(newCapacity, currentCount),
            (FieldType.Eui64, ValueCacheStorageMode.Native) => GrowSingleArray<ulong>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactFloat) => GrowSingleArray<float>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactInt8) => GrowSingleArray<sbyte>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactInt16) => GrowSingleArray<short>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactInt32) => GrowSingleArray<int>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactUInt8) => GrowSingleArray<byte>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactUInt16) => GrowSingleArray<ushort>(newCapacity, currentCount),
            (_, ValueCacheStorageMode.CompactUInt32) => GrowSingleArray<uint>(newCapacity, currentCount),
            _ => throw new InvalidOperationException(
                $"Cannot grow value data: unsupported type/mode combination ({_OriginalFieldType}, {_StorageMode})."),
        };
    }

    /// <summary>Grows a single typed value array, preserving existing data.</summary>
    /// <param name="newCapacity">New array capacity to allocate.</param>
    /// <param name="currentCount">Number of valid entries to copy, passed in to avoid re-reading <c>_Count</c>.</param>
    private ValueCacheData GrowSingleArray<T>(int newCapacity, int currentCount) where T : unmanaged
    {
        T[] oldArray = _Data.AsArray<T>();
        T[] newArray = new T[newCapacity];
        if (currentCount > 0)
        {
            Array.Copy(oldArray, newArray, currentCount);
        }
        return new ValueCacheData(newArray);
    }

    /// <summary>
    /// Type-specific value extraction and storage at position <paramref name="writeIndex"/>.
    /// </summary>
    /// <param name="writeIndex">The array index at which to store the new value (caller's <c>_Count</c> snapshot).</param>
    /// <param name="value">The field value to extract and store.</param>
    private bool AppendValue(int writeIndex, in FieldValueData value)
    {
        switch (_OriginalFieldType, _StorageMode)
        {
            case (FieldType.U64, ValueCacheStorageMode.Native):
                if (!value.TryGetAsU64(out ulong nativeU64))
                {
                    return false;
                }
                _Data.AsArray<ulong>()[writeIndex] = nativeU64;
                break;
            case (FieldType.I64, ValueCacheStorageMode.Native):
                if (!value.TryGetAsI64(out long nativeI64))
                {
                    return false;
                }
                _Data.AsArray<long>()[writeIndex] = nativeI64;
                break;
            case (FieldType.F64, ValueCacheStorageMode.Native):
                if (!value.TryGetAsF64(out double nativeF64))
                {
                    return false;
                }
                _Data.AsArray<double>()[writeIndex] = nativeF64;
                break;
            case (FieldType.Timestamp, ValueCacheStorageMode.Native):
                if (!value.TryGetAsTimestamp(out Timestamp nativeTs))
                {
                    return false;
                }
                _Data.AsArray<long>()[writeIndex] = nativeTs.AsNanos;
                break;
            case (FieldType.Bool, ValueCacheStorageMode.Native):
                if (value.TryGetAsBool(out bool boolVal) && boolVal)
                {
                    _Data.AsBoolBits()[writeIndex >> 3] |= (byte)(1 << (_Count & 7));
                }
                break;
            case (FieldType.IPv4Address, ValueCacheStorageMode.Native):
                if (!value.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    return false;
                }
                _Data.AsArray<uint>()[writeIndex] = ipv4.RawValue;
                break;
            case (FieldType.MacAddress, ValueCacheStorageMode.Native):
                if (!value.TryGetAsMacAddress(out MacAddress mac))
                {
                    return false;
                }
                _Data.AsArray<ulong>()[writeIndex] = mac.RawValue;
                break;
            case (FieldType.Eui64, ValueCacheStorageMode.Native):
                if (!value.TryGetAsEui64(out Eui64 eui64))
                {
                    return false;
                }
                _Data.AsArray<ulong>()[writeIndex] = eui64.RawValue;
                break;
            case (FieldType.IPv6Address, ValueCacheStorageMode.Native):
                {
                    if (!value.TryGetAsIPv6(out IPv6Address addr))
                    {
                        return false;
                    }
                    (ulong[] low, ulong[] high) = _Data.AsDualUlong();
                    low[writeIndex] = addr.Low;
                    high[writeIndex] = addr.High;
                    break;
                }
            case (FieldType.Uuid, ValueCacheStorageMode.Native):
                {
                    if (!value.TryGetAsUuid(out Values.Uuid uuid))
                    {
                        return false;
                    }
                    (ulong[] low, ulong[] high) = _Data.AsDualUlong();
                    low[writeIndex] = uuid.Low;
                    high[writeIndex] = uuid.High;
                    break;
                }
            case (FieldType.U64, ValueCacheStorageMode.CompactFloat):
                if (!value.TryGetAsU64(out ulong compactU64))
                {
                    return false;
                }
                _Data.AsArray<float>()[writeIndex] = (float)compactU64;
                break;
            case (FieldType.I64, ValueCacheStorageMode.CompactFloat):
                if (!value.TryGetAsI64(out long compactI64))
                {
                    return false;
                }
                _Data.AsArray<float>()[writeIndex] = (float)compactI64;
                break;
            case (FieldType.F64, ValueCacheStorageMode.CompactFloat):
                if (!value.TryGetAsF64(out double compactF64))
                {
                    return false;
                }
                _Data.AsArray<float>()[writeIndex] = (float)compactF64;
                break;
            case (FieldType.Timestamp, ValueCacheStorageMode.CompactFloat):
                {
                    if (!value.TryGetAsTimestamp(out Timestamp compactTs))
                    {
                        return false;
                    }
                    long nanos = compactTs.AsNanos;
                    double seconds = nanos / 1_000_000_000.0;
                    if (!_BaseTimestampSet)
                    {
                        _BaseTimestamp = seconds;
                        // _BaseTimestampSet is writer-only: only read in this same method
                        // under ParseLock. No cross-thread visibility issue (BUG-VC-02).
                        _BaseTimestampSet = true;
                    }
                    _Data.AsArray<float>()[writeIndex] = (float)(seconds - _BaseTimestamp);
                    break;
                }
            case (FieldType.I64, ValueCacheStorageMode.CompactInt8):
                {
                    if (!value.TryGetAsI64(out long val))
                    {
                        return false;
                    }
                    if (val > sbyte.MaxValue)
                    {
                        _Data.AsArray<sbyte>()[writeIndex] = sbyte.MaxValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else if (val < sbyte.MinValue)
                    {
                        _Data.AsArray<sbyte>()[writeIndex] = sbyte.MinValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else
                    {
                        _Data.AsArray<sbyte>()[writeIndex] = (sbyte)val;
                    }
                    break;
                }
            case (FieldType.I64, ValueCacheStorageMode.CompactInt16):
                {
                    if (!value.TryGetAsI64(out long val))
                    {
                        return false;
                    }
                    if (val > short.MaxValue)
                    {
                        _Data.AsArray<short>()[writeIndex] = short.MaxValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else if (val < short.MinValue)
                    {
                        _Data.AsArray<short>()[writeIndex] = short.MinValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else
                    {
                        _Data.AsArray<short>()[writeIndex] = (short)val;
                    }
                    break;
                }
            case (FieldType.I64, ValueCacheStorageMode.CompactInt32):
                {
                    if (!value.TryGetAsI64(out long val))
                    {
                        return false;
                    }
                    if (val > int.MaxValue)
                    {
                        _Data.AsArray<int>()[writeIndex] = int.MaxValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else if (val < int.MinValue)
                    {
                        _Data.AsArray<int>()[writeIndex] = int.MinValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else
                    {
                        _Data.AsArray<int>()[writeIndex] = (int)val;
                    }
                    break;
                }
            case (FieldType.U64, ValueCacheStorageMode.CompactUInt8):
                {
                    if (!value.TryGetAsU64(out ulong val))
                    {
                        return false;
                    }
                    if (val > byte.MaxValue)
                    {
                        _Data.AsArray<byte>()[writeIndex] = byte.MaxValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else
                    {
                        _Data.AsArray<byte>()[writeIndex] = (byte)val;
                    }
                    break;
                }
            case (FieldType.U64, ValueCacheStorageMode.CompactUInt16):
                {
                    if (!value.TryGetAsU64(out ulong val))
                    {
                        return false;
                    }
                    if (val > ushort.MaxValue)
                    {
                        _Data.AsArray<ushort>()[writeIndex] = ushort.MaxValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else
                    {
                        _Data.AsArray<ushort>()[writeIndex] = (ushort)val;
                    }
                    break;
                }
            case (FieldType.U64, ValueCacheStorageMode.CompactUInt32):
                {
                    if (!value.TryGetAsU64(out ulong val))
                    {
                        return false;
                    }
                    if (val > uint.MaxValue)
                    {
                        _Data.AsArray<uint>()[writeIndex] = uint.MaxValue;
                        Interlocked.Or(ref _CompletenessRaw, (int)ValueCacheCompleteness.HasOverflow); // use Interlocked for consistent ARM64 visibility
                    }
                    else
                    {
                        _Data.AsArray<uint>()[writeIndex] = (uint)val;
                    }
                    break;
                }
            default:
                throw new InvalidOperationException(
                    $"Unsupported type/mode combination: ({_OriginalFieldType}, {_StorageMode})");
        }
        return true;
    }

    /// <summary>Creates an empty <see cref="ValueCacheData"/> for the given field type and storage mode.</summary>
    private static ValueCacheData CreateEmptyData(FieldType fieldType, ValueCacheStorageMode mode)
    {
        if (fieldType == FieldType.Bool)
        {
            return new ValueCacheData(Array.Empty<byte>());
        }

        if (fieldType is FieldType.IPv6Address or FieldType.Uuid)
        {
            return new ValueCacheData(Array.Empty<ulong>(), Array.Empty<ulong>());
        }

        return (fieldType, mode) switch
        {
            (FieldType.U64, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<ulong>()),
            (FieldType.I64, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<long>()),
            (FieldType.F64, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<double>()),
            (FieldType.Timestamp, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<long>()),
            (FieldType.IPv4Address, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<uint>()),
            (FieldType.MacAddress, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<ulong>()),
            (FieldType.Eui64, ValueCacheStorageMode.Native) => new ValueCacheData(Array.Empty<ulong>()),
            (_, ValueCacheStorageMode.CompactFloat) => new ValueCacheData(Array.Empty<float>()),
            (_, ValueCacheStorageMode.CompactInt8) => new ValueCacheData(Array.Empty<sbyte>()),
            (_, ValueCacheStorageMode.CompactInt16) => new ValueCacheData(Array.Empty<short>()),
            (_, ValueCacheStorageMode.CompactInt32) => new ValueCacheData(Array.Empty<int>()),
            (_, ValueCacheStorageMode.CompactUInt8) => new ValueCacheData(Array.Empty<byte>()),
            (_, ValueCacheStorageMode.CompactUInt16) => new ValueCacheData(Array.Empty<ushort>()),
            (_, ValueCacheStorageMode.CompactUInt32) => new ValueCacheData(Array.Empty<uint>()),
            _ => throw new InvalidOperationException(
                $"Unsupported field type / storage mode combination: {fieldType} / {mode}"),
        };
    }

    /// <summary>Slices a primary typed array to the current <see cref="Count"/> snapshot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<T> SliceSpan<T>() where T : unmanaged
    {
        int count = Volatile.Read(ref Unsafe.AsRef(in _Count));
        return _Data.AsArray<T>().AsSpan(0, count);
    }

    /// <summary>Finds the first index where timestamps[i] >= value.</summary>
    private static int LowerBound(ReadOnlySpan<long> timestamps, long value)
    {
        int lo = 0;
        int hi = timestamps.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (timestamps[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>Finds the first index where timestamps[i] > value, starting from <paramref name="start"/>.</summary>
    private static int UpperBound(ReadOnlySpan<long> timestamps, long value, int start)
    {
        int lo = start;
        int hi = timestamps.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (timestamps[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>Reconstructs an IPv6Address from dual arrays.</summary>
    private FieldValueData ReconstructIPv6(int index)
    {
        (ulong[] low, ulong[] high) = _Data.AsDualUlong();
        return FieldValueData.NewIPv6(new IPv6Address(high[index], low[index]));
    }

    /// <summary>Reconstructs a Uuid from dual arrays.</summary>
    private FieldValueData ReconstructUuid(int index)
    {
        (ulong[] low, ulong[] high) = _Data.AsDualUlong();
        return FieldValueData.NewUuid(new Values.Uuid(high[index], low[index]));
    }

    /// <summary>Reconstructs a Timestamp from CompactFloat delta + base.</summary>
    private FieldValueData ReconstructTimestampFromCompactFloat(int index)
    {
        double seconds = _BaseTimestamp + _Data.AsSpan<float>()[index];
        long nanos = (long)(seconds * 1_000_000_000.0);
        return FieldValueData.NewTimestamp(new Timestamp(nanos));
    }

    #endregion
}
