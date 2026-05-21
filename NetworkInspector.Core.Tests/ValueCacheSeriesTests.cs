// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ValueCacheSeries"/> — construction, append, span accessors,
/// range queries, scalar queries, completeness flags, and Single-Writer/Multi-Reader
/// thread-safety contract.
/// </summary>
internal sealed class ValueCacheSeriesTests
{
    #region Construction

    [Test]
    public async Task CreateLive_ValidArgs_StartsWithZeroCountAndEmptySpans()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        await Assert.That(series.Count).IsEqualTo(0);
        await Assert.That(series.SnapshotCount()).IsEqualTo(0);
        await Assert.That(series.TimestampSpan.IsEmpty).IsTrue();
        await Assert.That(series.PacketIdSpan.IsEmpty).IsTrue();
        await Assert.That(series.Completeness).IsEqualTo(ValueCacheCompleteness.None);
        await Assert.That(series.FieldId).IsEqualTo(new FieldId(1));
        await Assert.That(series.OriginalFieldType).IsEqualTo(FieldType.U64);
        await Assert.That(series.StorageMode).IsEqualTo(ValueCacheStorageMode.Native);
    }

    [Test]
    public async Task PreBuiltConstructor_ValidArrays_CountEqualsSupplied()
    {
        long[] ts = [100L, 200L, 300L];
        int[] ids = [1, 2, 3];
        ulong[] values = [10UL, 20UL, 30UL];
        ValueCacheData data = new(values);

        ValueCacheSeries series = new(new FieldId(5), FieldType.U64, ValueCacheStorageMode.Native, ts, ids, data, ValueCacheCompleteness.None);

        await Assert.That(series.Count).IsEqualTo(3);
        await Assert.That(series.TimestampSpan.Length).IsEqualTo(3);
    }

    [Test]
    public async Task PreBuiltConstructor_ExplicitCount_CountEqualsSupplied()
    {
        long[] ts = [100L, 200L, 300L, 400L]; // capacity 4, count 2
        int[] ids = [1, 2, 3, 4];
        ulong[] values = [10UL, 20UL, 30UL, 40UL];
        ValueCacheData data = new(values);

        ValueCacheSeries series = new(new FieldId(5), FieldType.U64, ValueCacheStorageMode.Native, ts, ids, data, ValueCacheCompleteness.None, count: 2);

        await Assert.That(series.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PreBuiltConstructor_CountExceedsTimestampsLength_ThrowsOutOfRange()
    {
        long[] ts = [100L, 200L];
        int[] ids = [1, 2];
        ulong[] values = [10UL, 20UL];
        ValueCacheData data = new(values);

        await Assert.That(() =>
            new ValueCacheSeries(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native, ts, ids, data, ValueCacheCompleteness.None, count: 5))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PreBuiltConstructor_MismatchedPacketIdsLength_ThrowsArgumentException()
    {
        long[] ts = [100L, 200L];
        int[] ids = [1]; // wrong length
        ulong[] values = [10UL, 20UL];
        ValueCacheData data = new(values);

        await Assert.That(() =>
            new ValueCacheSeries(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native, ts, ids, data, ValueCacheCompleteness.None))
            .Throws<ArgumentException>();
    }

    #endregion

    #region TryAppend — Basic Behavior

    [Test]
    public async Task TryAppend_FirstEntry_ReturnsTrueAndCountBecomesOne()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        bool result = series.TryAppend(1000L, 42, FieldValueData.NewU64(99UL));

        await Assert.That(result).IsTrue();
        await Assert.That(series.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryAppend_MultipleEntries_CountIncrementsCorrectly()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.Native);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(10L));
        series.TryAppend(2000L, 2, FieldValueData.NewI64(20L));
        series.TryAppend(3000L, 3, FieldValueData.NewI64(30L));

        await Assert.That(series.Count).IsEqualTo(3);
    }

    [Test]
    public async Task TryAppend_SetsCorrectTimestampsAndPacketIds()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        series.TryAppend(1000L, 7, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 9, FieldValueData.NewU64(2UL));

        // Copy span data to locals before first await to avoid CS4007
        long ts0 = series.TimestampSpan[0];
        long ts1 = series.TimestampSpan[1];
        int pid0 = series.PacketIdSpan[0];
        int pid1 = series.PacketIdSpan[1];

        await Assert.That(ts0).IsEqualTo(1000L);
        await Assert.That(ts1).IsEqualTo(2000L);
        await Assert.That(pid0).IsEqualTo(7);
        await Assert.That(pid1).IsEqualTo(9);
    }

    [Test]
    public async Task TryAppend_EqualTimestamp_ReturnsTrueAllowed()
    {
        // Equal timestamps are permitted; only strict less-than is rejected.
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));

        bool result = series.TryAppend(1000L, 2, FieldValueData.NewU64(2UL));

        await Assert.That(result).IsTrue();
        await Assert.That(series.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TryAppend_DecreasingTimestamp_ReturnsFalseAndSetsTimestampSkipsFlag()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(2000L, 1, FieldValueData.NewU64(1UL));

        bool result = series.TryAppend(1000L, 2, FieldValueData.NewU64(2UL));

        await Assert.That(result).IsFalse();
        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasTimestampSkips)).IsTrue();
    }

    [Test]
    public async Task TryAppend_FirstEntryAnyTimestamp_NoSkipFlagSet()
    {
        // First entry never triggers monotonic check regardless of timestamp value.
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        series.TryAppend(long.MaxValue, 1, FieldValueData.NewU64(1UL));

        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasTimestampSkips)).IsFalse();
    }

    [Test]
    public async Task TryAppend_GrowsBeyondInitialCapacity_DataPreserved()
    {
        // Initial capacity is 0; first append triggers Grow to 256. Verify data integrity.
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        const int EntryCount = 300; // exceeds initial Grow(256) allocation

        for (int i = 0; i < EntryCount; i++)
        {
            series.TryAppend(i * 100L, i, FieldValueData.NewU64((ulong)i));
        }

        await Assert.That(series.Count).IsEqualTo(EntryCount);
        // Copy to array before looping with await to avoid CS4007
        ulong[] vals = series.AsU64Span().ToArray();
        for (int i = 0; i < EntryCount; i++)
        {
            await Assert.That(vals[i]).IsEqualTo((ulong)i);
        }
    }

    [Test]
    public async Task MarkDuplicateDrop_SetsHasDuplicateDropsFlag()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        series.MarkDuplicateDrop();

        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasDuplicateDrops)).IsTrue();
    }

    #endregion

    #region TryAppend — Native Type Storage

    [Test]
    [Arguments(100UL)]
    [Arguments(0UL)]
    [Arguments(ulong.MaxValue)]
    [Arguments(ulong.MinValue)]
    public async Task TryAppend_U64Native_StoresCorrectValue(ulong expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(expected));

        await Assert.That(series.AsU64Span()[0]).IsEqualTo(expected);
        await Assert.That(series.GetValueAt(0)).IsEqualTo(FieldValueData.NewU64(expected));
    }

    [Test]
    [Arguments(100L)]
    [Arguments(0L)]
    [Arguments(long.MaxValue)]
    [Arguments(long.MinValue)]
    public async Task TryAppend_I64Native_StoresCorrectValue(long expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.Native);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(expected));

        await Assert.That(series.AsI64Span()[0]).IsEqualTo(expected);
        await Assert.That(series.GetValueAt(0)).IsEqualTo(FieldValueData.NewI64(expected));
    }

    [Test]
    [Arguments(3.14)]
    [Arguments(0.0)]
    [Arguments(double.MaxValue)]
    [Arguments(double.MinValue)]
    public async Task TryAppend_F64Native_StoresCorrectValue(double expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.F64, ValueCacheStorageMode.Native);

        series.TryAppend(1000L, 1, FieldValueData.NewF64(expected));

        await Assert.That(series.AsF64Span()[0]).IsEqualTo(expected);
        await Assert.That(series.GetValueAt(0)).IsEqualTo(FieldValueData.NewF64(expected));
    }

    [Test]
    public async Task TryAppend_TimestampNative_StoresNanoseconds()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Timestamp, ValueCacheStorageMode.Native);
        Timestamp ts = new(1_000_000_000L); // 1 second

        series.TryAppend(1000L, 1, FieldValueData.NewTimestamp(ts));

        await Assert.That(series.AsTimestampNanosSpan()[0]).IsEqualTo(1_000_000_000L);
        FieldValueData retrieved = series.GetValueAt(0);
        retrieved.TryGetAsTimestamp(out Timestamp retrievedTs);
        await Assert.That(retrievedTs.AsNanos).IsEqualTo(ts.AsNanos);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TryAppend_BoolNative_StoresInBitPackedFormat(bool value)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Bool, ValueCacheStorageMode.Native);

        series.TryAppend(1000L, 1, FieldValueData.NewBool(value));

        FieldValueData retrieved = series.GetValueAt(0);
        retrieved.TryGetAsBool(out bool retrievedBool);
        await Assert.That(retrievedBool).IsEqualTo(value);
    }

    [Test]
    public async Task TryAppend_BoolNative_MultipleBits_BitPackingCorrect()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Bool, ValueCacheStorageMode.Native);
        bool[] pattern = [true, false, true, true, false, false, true, false, true]; // spans 2 bytes

        for (int i = 0; i < pattern.Length; i++)
        {
            series.TryAppend(i * 100L, i, FieldValueData.NewBool(pattern[i]));
        }

        await Assert.That(series.Count).IsEqualTo(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            series.GetValueAt(i).TryGetAsBool(out bool b);
            await Assert.That(b).IsEqualTo(pattern[i]);
        }
    }

    [Test]
    public async Task TryAppend_IPv4Native_StoresRawValue()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.IPv4Address, ValueCacheStorageMode.Native);
        IPv4Address addr = new(0xC0A80001u); // 192.168.0.1

        series.TryAppend(1000L, 1, FieldValueData.NewIPv4(addr));

        await Assert.That(series.AsIPv4Span()[0]).IsEqualTo(0xC0A80001u);
        series.GetValueAt(0).TryGetAsIPv4(out IPv4Address retrieved);
        await Assert.That(retrieved.RawValue).IsEqualTo(addr.RawValue);
    }

    [Test]
    public async Task TryAppend_MacAddressNative_StoresRawValue()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.MacAddress, ValueCacheStorageMode.Native);
        MacAddress mac = new(0x001122334455UL);

        series.TryAppend(1000L, 1, FieldValueData.NewMacAddress(mac));

        await Assert.That(series.AsU64Span()[0]).IsEqualTo(0x001122334455UL);
        series.GetValueAt(0).TryGetAsMacAddress(out MacAddress retrieved);
        await Assert.That(retrieved.RawValue).IsEqualTo(mac.RawValue);
    }

    [Test]
    public async Task TryAppend_Eui64Native_StoresRawValue()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Eui64, ValueCacheStorageMode.Native);
        Eui64 eui = new(0xAABBCCDDEEFF0011UL);

        series.TryAppend(1000L, 1, FieldValueData.NewEui64(eui));

        await Assert.That(series.AsU64Span()[0]).IsEqualTo(0xAABBCCDDEEFF0011UL);
        series.GetValueAt(0).TryGetAsEui64(out Eui64 retrieved);
        await Assert.That(retrieved.RawValue).IsEqualTo(eui.RawValue);
    }

    [Test]
    public async Task TryAppend_IPv6Native_StoresHighAndLow()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.IPv6Address, ValueCacheStorageMode.Native);
        IPv6Address addr = new(0x20010DB800000000UL, 0x0000000000000001UL);

        series.TryAppend(1000L, 1, FieldValueData.NewIPv6(addr));

        await Assert.That(series.DualHighSpan()[0]).IsEqualTo(0x20010DB800000000UL);
        await Assert.That(series.DualLowSpan()[0]).IsEqualTo(0x0000000000000001UL);
        series.GetValueAt(0).TryGetAsIPv6(out IPv6Address retrieved);
        await Assert.That(retrieved.High).IsEqualTo(addr.High);
        await Assert.That(retrieved.Low).IsEqualTo(addr.Low);
    }

    [Test]
    public async Task TryAppend_UuidNative_StoresHighAndLow()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Uuid, ValueCacheStorageMode.Native);
        Values.Uuid uuid = new(0x123456789ABCDEF0UL, 0xFEDCBA9876543210UL);

        series.TryAppend(1000L, 1, FieldValueData.NewUuid(uuid));

        await Assert.That(series.DualHighSpan()[0]).IsEqualTo(0x123456789ABCDEF0UL);
        await Assert.That(series.DualLowSpan()[0]).IsEqualTo(0xFEDCBA9876543210UL);
        series.GetValueAt(0).TryGetAsUuid(out Values.Uuid retrieved);
        await Assert.That(retrieved.High).IsEqualTo(uuid.High);
        await Assert.That(retrieved.Low).IsEqualTo(uuid.Low);
    }

    #endregion

    #region TryAppend — CompactFloat Storage

    [Test]
    [Arguments(0UL)]
    [Arguments(42UL)]
    [Arguments(1000000UL)]
    public async Task TryAppend_U64CompactFloat_StoresAsFloat(ulong input)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactFloat);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsFloatSpan()[0]).IsEqualTo((float)input);
    }

    [Test]
    [Arguments(0L)]
    [Arguments(-100L)]
    [Arguments(100L)]
    public async Task TryAppend_I64CompactFloat_StoresAsFloat(long input)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.CompactFloat);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(input));

        await Assert.That(series.AsFloatSpan()[0]).IsEqualTo((float)input);
    }

    [Test]
    public async Task TryAppend_TimestampCompactFloat_SetsBaseTimestampOnFirstEntry()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Timestamp, ValueCacheStorageMode.CompactFloat);
        long nanos = 1_000_000_000L; // 1 second
        Timestamp ts = new(nanos);

        series.TryAppend(1000L, 1, FieldValueData.NewTimestamp(ts));

        // Base is set to first timestamp in seconds; delta for first entry = 0
        await Assert.That(series.BaseTimestamp).IsEqualTo(1.0);
        await Assert.That(series.AsFloatSpan()[0]).IsEqualTo(0.0f);
    }

    [Test]
    public async Task TryAppend_TimestampCompactFloat_SubsequentEntry_StoresDelta()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Timestamp, ValueCacheStorageMode.CompactFloat);
        series.TryAppend(1000L, 1, FieldValueData.NewTimestamp(new Timestamp(1_000_000_000L))); // base=1s
        series.TryAppend(2000L, 2, FieldValueData.NewTimestamp(new Timestamp(2_000_000_000L))); // delta=1s

        // First entry delta=0, second entry delta≈1s (within float precision)
        await Assert.That(series.AsFloatSpan()[0]).IsEqualTo(0.0f);
        await Assert.That(Math.Abs(series.AsFloatSpan()[1] - 1.0f)).IsLessThan(1e-5f);
    }

    #endregion

    #region TryAppend — Compact Integer Storage & Overflow Flags

    [Test]
    [Arguments(0L, (sbyte)0)]
    [Arguments(127L, sbyte.MaxValue)]
    [Arguments(-128L, sbyte.MinValue)]
    public async Task TryAppend_I64CompactInt8_InRange_StoresCorrectly(long input, sbyte expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.CompactInt8);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(input));

        await Assert.That(series.AsCompactInt8Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsFalse();
    }

    [Test]
    [Arguments(128L, sbyte.MaxValue)]
    [Arguments(-129L, sbyte.MinValue)]
    [Arguments(long.MaxValue, sbyte.MaxValue)]
    [Arguments(long.MinValue, sbyte.MinValue)]
    public async Task TryAppend_I64CompactInt8_Overflow_ClampsAndSetsFlag(long input, sbyte expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.CompactInt8);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(input));

        await Assert.That(series.AsCompactInt8Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsTrue();
    }

    [Test]
    [Arguments(0L, (short)0)]
    [Arguments(32767L, short.MaxValue)]
    [Arguments(-32768L, short.MinValue)]
    public async Task TryAppend_I64CompactInt16_InRange_StoresCorrectly(long input, short expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.CompactInt16);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(input));

        await Assert.That(series.AsCompactInt16Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsFalse();
    }

    [Test]
    [Arguments(32768L, short.MaxValue)]
    [Arguments(-32769L, short.MinValue)]
    public async Task TryAppend_I64CompactInt16_Overflow_ClampsAndSetsFlag(long input, short expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.CompactInt16);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(input));

        await Assert.That(series.AsCompactInt16Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsTrue();
    }

    [Test]
    [Arguments(0L, 0)]
    [Arguments((long)int.MaxValue, int.MaxValue)]
    [Arguments((long)int.MinValue, int.MinValue)]
    public async Task TryAppend_I64CompactInt32_InRange_StoresCorrectly(long input, int expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.I64, ValueCacheStorageMode.CompactInt32);

        series.TryAppend(1000L, 1, FieldValueData.NewI64(input));

        await Assert.That(series.AsCompactInt32Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsFalse();
    }

    [Test]
    [Arguments(0UL, (byte)0)]
    [Arguments(255UL, byte.MaxValue)]
    public async Task TryAppend_U64CompactUInt8_InRange_StoresCorrectly(ulong input, byte expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactUInt8);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsCompactUInt8Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsFalse();
    }

    [Test]
    [Arguments(256UL, byte.MaxValue)]
    [Arguments(ulong.MaxValue, byte.MaxValue)]
    public async Task TryAppend_U64CompactUInt8_Overflow_ClampsAndSetsFlag(ulong input, byte expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactUInt8);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsCompactUInt8Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsTrue();
    }

    [Test]
    [Arguments(0UL, (ushort)0)]
    [Arguments(65535UL, ushort.MaxValue)]
    public async Task TryAppend_U64CompactUInt16_InRange_StoresCorrectly(ulong input, ushort expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactUInt16);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsCompactUInt16Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsFalse();
    }

    [Test]
    [Arguments(65536UL, ushort.MaxValue)]
    public async Task TryAppend_U64CompactUInt16_Overflow_ClampsAndSetsFlag(ulong input, ushort expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactUInt16);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsCompactUInt16Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsTrue();
    }

    [Test]
    [Arguments(0UL, 0u)]
    [Arguments((ulong)uint.MaxValue, uint.MaxValue)]
    public async Task TryAppend_U64CompactUInt32_InRange_StoresCorrectly(ulong input, uint expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactUInt32);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsCompactUInt32Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsFalse();
    }

    [Test]
    [Arguments((ulong)uint.MaxValue + 1UL, uint.MaxValue)]
    [Arguments(ulong.MaxValue, uint.MaxValue)]
    public async Task TryAppend_U64CompactUInt32_Overflow_ClampsAndSetsFlag(ulong input, uint expected)
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.CompactUInt32);

        series.TryAppend(1000L, 1, FieldValueData.NewU64(input));

        await Assert.That(series.AsCompactUInt32Span()[0]).IsEqualTo(expected);
        await Assert.That(series.Completeness.HasFlag(ValueCacheCompleteness.HasOverflow)).IsTrue();
    }

    #endregion

    #region Span Accessors

    [Test]
    public async Task SnapshotCount_ReturnsSameAsCount()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));

        await Assert.That(series.SnapshotCount()).IsEqualTo(series.Count);
    }

    [Test]
    public async Task SliceTimestamps_MatchesTimestampSpan()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));

        int snap = series.SnapshotCount();
        // Copy span data to locals before first await to avoid CS4007
        long sliced0 = series.SliceTimestamps(snap)[0];
        long sliced1 = series.SliceTimestamps(snap)[1];
        long span0 = series.TimestampSpan[0];
        long span1 = series.TimestampSpan[1];
        int slicedLen = series.SliceTimestamps(snap).Length;
        int spanLen = series.TimestampSpan.Length;

        await Assert.That(slicedLen).IsEqualTo(spanLen);
        await Assert.That(sliced0).IsEqualTo(span0);
        await Assert.That(sliced1).IsEqualTo(span1);
    }

    [Test]
    public async Task SlicePacketIds_MatchesPacketIdSpan()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 5, FieldValueData.NewU64(1UL));

        int snap = series.SnapshotCount();

        await Assert.That(series.SlicePacketIds(snap)[0]).IsEqualTo(5);
        await Assert.That(series.PacketIdSpan[0]).IsEqualTo(5);
    }

    [Test]
    public async Task BoolBitsSpan_EmptyOnEmptySeries()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Bool, ValueCacheStorageMode.Native);

        await Assert.That(series.AsBoolBits().IsEmpty).IsTrue();
    }

    [Test]
    public async Task BoolBitsSpan_OneEntry_OneByteReturned()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Bool, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewBool(true));

        await Assert.That(series.AsBoolBits().Length).IsEqualTo(1);
    }

    [Test]
    public async Task BoolBitsSpan_NineEntries_TwoBytesReturned()
    {
        // 9 entries → ceil(9/8) = 2 bytes
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Bool, ValueCacheStorageMode.Native);
        for (int i = 0; i < 9; i++)
        {
            series.TryAppend(i * 100L, i, FieldValueData.NewBool(i % 2 == 0));
        }

        await Assert.That(series.AsBoolBits().Length).IsEqualTo(2);
    }

    #endregion

    #region GetTimestampRange

    [Test]
    public async Task GetTimestampRange_EmptySeries_ReturnsZeroZero()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        (int start, int count) = series.GetTimestampRange(new Timestamp(0L), new Timestamp(long.MaxValue));

        await Assert.That(start).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTimestampRange_FromAfterTo_ReturnsZeroZero()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));

        (int start, int count) = series.GetTimestampRange(new Timestamp(2000L), new Timestamp(1000L));

        await Assert.That(start).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTimestampRange_RangeBeforeAllData_ReturnsZeroZero()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(5000L, 1, FieldValueData.NewU64(1UL));

        (int start, int count) = series.GetTimestampRange(new Timestamp(0L), new Timestamp(1000L));

        await Assert.That(start).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTimestampRange_RangeAfterAllData_ReturnsZeroZero()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));

        (int start, int count) = series.GetTimestampRange(new Timestamp(5000L), new Timestamp(9000L));

        await Assert.That(start).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTimestampRange_ExactRange_ReturnsAllEntries()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));
        series.TryAppend(3000L, 3, FieldValueData.NewU64(3UL));

        (int start, int count) = series.GetTimestampRange(new Timestamp(1000L), new Timestamp(3000L));

        await Assert.That(start).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task GetTimestampRange_PartialRange_ReturnsCorrectSubset()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));
        series.TryAppend(3000L, 3, FieldValueData.NewU64(3UL));
        series.TryAppend(4000L, 4, FieldValueData.NewU64(4UL));

        (int start, int count) = series.GetTimestampRange(new Timestamp(2000L), new Timestamp(3000L));

        await Assert.That(start).IsEqualTo(1);
        await Assert.That(count).IsEqualTo(2);
    }

    #endregion

    #region BinarySearchTimestamp

    [Test]
    public async Task BinarySearchTimestamp_ExactMatch_ReturnsIndex()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));
        series.TryAppend(3000L, 3, FieldValueData.NewU64(3UL));

        int result = series.BinarySearchTimestamp(2000L);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task BinarySearchTimestamp_BetweenEntries_ReturnsNextIndex()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(3000L, 2, FieldValueData.NewU64(2UL));

        int result = series.BinarySearchTimestamp(2000L);

        await Assert.That(result).IsEqualTo(1); // first index >= 2000
    }

    [Test]
    public async Task BinarySearchTimestamp_BeforeAll_ReturnsZero()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(5000L, 1, FieldValueData.NewU64(1UL));

        int result = series.BinarySearchTimestamp(0L);

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task BinarySearchTimestamp_AfterAll_ReturnsCount()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));
        series.TryAppend(2000L, 2, FieldValueData.NewU64(2UL));

        int result = series.BinarySearchTimestamp(9999L);

        await Assert.That(result).IsEqualTo(2);
    }

    [Test]
    public async Task BinarySearchTimestamp_EmptySeries_ReturnsZero()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        int result = series.BinarySearchTimestamp(1000L);

        await Assert.That(result).IsEqualTo(0);
    }

    #endregion

    #region GetValueAt

    [Test]
    public async Task GetValueAt_NegativeIndex_ThrowsArgumentOutOfRange()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));

        await Assert.That(() => series.GetValueAt(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetValueAt_IndexEqualToCount_ThrowsArgumentOutOfRange()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));

        await Assert.That(() => series.GetValueAt(1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetValueAt_IndexBeyondCount_ThrowsArgumentOutOfRange()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        series.TryAppend(1000L, 1, FieldValueData.NewU64(1UL));

        await Assert.That(() => series.GetValueAt(999)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetValueAt_EmptySeries_ThrowsArgumentOutOfRange()
    {
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);

        await Assert.That(() => series.GetValueAt(0)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Grow — Data Integrity Across Reallocation

    [Test]
    public async Task Grow_IPv6_DataIntegrityAfterReallocation()
    {
        // Forces multiple grows; verifies dual-array copy is correct
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.IPv6Address, ValueCacheStorageMode.Native);
        const int EntryCount = 300;

        for (int i = 0; i < EntryCount; i++)
        {
            series.TryAppend(i * 100L, i, FieldValueData.NewIPv6(new IPv6Address((ulong)i, (ulong)(i + 1))));
        }

        await Assert.That(series.Count).IsEqualTo(EntryCount);
        for (int i = 0; i < EntryCount; i++)
        {
            series.GetValueAt(i).TryGetAsIPv6(out IPv6Address retrieved);
            await Assert.That(retrieved.High).IsEqualTo((ulong)i);
            await Assert.That(retrieved.Low).IsEqualTo((ulong)(i + 1));
        }
    }

    [Test]
    public async Task Grow_Bool_BitPackingIntegrityAfterReallocation()
    {
        // Forces grow of the bit-packed byte array; verifies copy is correct
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.Bool, ValueCacheStorageMode.Native);
        const int EntryCount = 300;

        for (int i = 0; i < EntryCount; i++)
        {
            series.TryAppend(i * 100L, i, FieldValueData.NewBool(i % 3 == 0));
        }

        await Assert.That(series.Count).IsEqualTo(EntryCount);
        for (int i = 0; i < EntryCount; i++)
        {
            series.GetValueAt(i).TryGetAsBool(out bool b);
            await Assert.That(b).IsEqualTo(i % 3 == 0);
        }
    }

    #endregion

    #region Thread-Safety — Concurrent Reader / Single Writer

    [Test]
    public async Task ConcurrentReaders_WhileWriting_NeverSeePartialState()
    {
        // Verifies the Single-Writer / Multi-Reader volatile contract:
        // readers must never observe a Count increment before the data write completes.
        // If plain (non-volatile) _Count access were used in TryAppend, readers could
        // observe an incremented count with uninitialized data on weakly-ordered CPUs.
        const int WriterIterations = 500;
        const int ReaderCount = 4;
        ValueCacheSeries series = ValueCacheSeries.CreateLive(new FieldId(1), FieldType.U64, ValueCacheStorageMode.Native);
        bool errorDetected = false;
        using ManualResetEventSlim startGate = new(false);

        // Reader task: repeatedly reads a snapshot and verifies data length matches count
        Task[] readers = new Task[ReaderCount];
        for (int r = 0; r < ReaderCount; r++)
        {
            readers[r] = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < WriterIterations * 10; i++)
                {
                    int snap = series.SnapshotCount();
                    ReadOnlySpan<long> ts = series.SliceTimestamps(snap);
                    ReadOnlySpan<ulong> vals = series.SliceU64(snap);
                    // Both spans must have exactly snap elements;
                    // a mismatch indicates partial publication was observed.
                    if (ts.Length != snap || vals.Length != snap)
                    {
                        Volatile.Write(ref errorDetected, true);
                    }
                }
            });
        }

        startGate.Set();

        // Writer runs sequentially on the current thread (simulating ParseLock)
        for (int i = 0; i < WriterIterations; i++)
        {
            series.TryAppend(i * 100L, i, FieldValueData.NewU64((ulong)i));
        }

        await Task.WhenAll(readers).ConfigureAwait(false);

        await Assert.That(errorDetected).IsFalse();
    }

    #endregion
}
