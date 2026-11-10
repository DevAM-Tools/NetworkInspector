// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// Tests for <see cref="SettingValue"/> tagged union struct — all 12 types,
/// equality, implicit conversions, and edge cases.
/// </summary>
internal sealed class SettingValueTests
{
    // === Bool ===

    [Test]
    public async Task BoolTrueRoundtrip()
    {
        SettingValue v = SettingValue.Bool(true);
        await Assert.That(v.Type).IsEqualTo(SettingType.Bool);
        bool success = v.TryGetAsBool(out bool result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task BoolFalseRoundtrip()
    {
        SettingValue v = SettingValue.Bool(false);
        bool success = v.TryGetAsBool(out bool result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task BoolWrongAccessorReturnsFalse()
    {
        SettingValue v = SettingValue.Bool(true);
        await Assert.That(v.TryGetAsString(out _)).IsFalse();
        await Assert.That(v.TryGetAsF64(out _)).IsFalse();
        await Assert.That(v.TryGetAsU64(out _)).IsFalse();
        await Assert.That(v.TryGetAsI64(out _)).IsFalse();
        await Assert.That(v.TryGetAsBytes(out _)).IsFalse();
        await Assert.That(v.TryGetAsEnum(out _)).IsFalse();
    }

    // === String ===

    [Test]
    public async Task StringRoundtrip()
    {
        SettingValue v = SettingValue.String("hello");
        await Assert.That(v.Type).IsEqualTo(SettingType.String);
        bool success = v.TryGetAsString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task StringEmptyRoundtrip()
    {
        SettingValue v = SettingValue.String("");
        bool success = v.TryGetAsString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task StringWrongAccessorReturnsFalse()
    {
        SettingValue v = SettingValue.String("test");
        await Assert.That(v.TryGetAsBool(out _)).IsFalse();
        await Assert.That(v.TryGetAsF64(out _)).IsFalse();
    }

    // === F64 ===

    [Test]
    public async Task F64Roundtrip()
    {
        SettingValue v = SettingValue.F64(3.14);
        await Assert.That(v.Type).IsEqualTo(SettingType.F64);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(3.14);
    }

    [Test]
    public async Task F64NegativeRoundtrip()
    {
        SettingValue v = SettingValue.F64(-1.5);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(-1.5);
    }

    [Test]
    public async Task F64ZeroRoundtrip()
    {
        SettingValue v = SettingValue.F64(0.0);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(0.0);
    }

    [Test]
    public async Task F64NaNRoundtrip()
    {
        SettingValue v = SettingValue.F64(double.NaN);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(double.NaN);
    }

    [Test]
    public async Task F64InfinityRoundtrip()
    {
        SettingValue v = SettingValue.F64(double.PositiveInfinity);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(double.PositiveInfinity);
    }

    // === U64 ===

    [Test]
    public async Task U64Roundtrip()
    {
        SettingValue v = SettingValue.U64(42UL);
        await Assert.That(v.Type).IsEqualTo(SettingType.U64);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(42UL);
    }

    [Test]
    public async Task U64MaxValueRoundtrip()
    {
        SettingValue v = SettingValue.U64(ulong.MaxValue);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task U64ZeroRoundtrip()
    {
        SettingValue v = SettingValue.U64(0UL);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(0UL);
    }

    // === I64 ===

    [Test]
    public async Task I64Roundtrip()
    {
        SettingValue v = SettingValue.I64(-42L);
        await Assert.That(v.Type).IsEqualTo(SettingType.I64);
        bool success = v.TryGetAsI64(out long result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(-42L);
    }

    [Test]
    public async Task I64MaxValueRoundtrip()
    {
        SettingValue v = SettingValue.I64(long.MaxValue);
        bool success = v.TryGetAsI64(out long result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task I64MinValueRoundtrip()
    {
        SettingValue v = SettingValue.I64(long.MinValue);
        bool success = v.TryGetAsI64(out long result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(long.MinValue);
    }

    // === Bytes ===

    [Test]
    public async Task BytesRoundtrip()
    {
        byte[] data = [1, 2, 3, 4, 5];
        SettingValue v = SettingValue.Bytes(data);
        await Assert.That(v.Type).IsEqualTo(SettingType.Bytes);
        bool success = v.TryGetAsBytes(out byte[] result);
        await Assert.That(success).IsTrue();
        await Assert.That(result.Length).IsEqualTo(5);
        await Assert.That(result[0]).IsEqualTo((byte)1);
        await Assert.That(result[4]).IsEqualTo((byte)5);
    }

    [Test]
    public async Task BytesEmptyRoundtrip()
    {
        byte[] data = [];
        SettingValue v = SettingValue.Bytes(data);
        bool success = v.TryGetAsBytes(out byte[] result);
        await Assert.That(success).IsTrue();
        await Assert.That(result.Length).IsEqualTo(0);
    }

    // === Enum ===

    [Test]
    public async Task EnumRoundtrip()
    {
        SettingValue v = SettingValue.Enum("High", 2);
        await Assert.That(v.Type).IsEqualTo(SettingType.Enum);
        bool success = v.TryGetAsEnum(out (string Name, ulong Value) e);
        await Assert.That(success).IsTrue();
        await Assert.That(e.Name).IsEqualTo("High");
        await Assert.That(e.Value).IsEqualTo(2UL);
    }

    // === Array types ===

    [Test]
    public async Task BoolArrayRoundtripIncludingEmpty()
    {
        SettingValue filled = SettingValue.BoolArray([true, false]);
        await Assert.That(filled.Type).IsEqualTo(SettingType.BoolArray);
        await Assert.That(filled.TryGetAsBoolArray(out bool[] filledCopy)).IsTrue();
        await Assert.That(filledCopy.Length).IsEqualTo(2);
        await Assert.That(filledCopy[0]).IsTrue();
        await Assert.That(filledCopy[1]).IsFalse();

        SettingValue empty = SettingValue.BoolArray([]);
        await Assert.That(empty.TryGetAsBoolArray(out bool[] emptyCopy)).IsTrue();
        await Assert.That(emptyCopy.Length).IsEqualTo(0);
    }

    [Test]
    public async Task StringArrayRoundtripIncludingEmpty()
    {
        SettingValue filled = SettingValue.StringArray(["a", "b"]);
        await Assert.That(filled.Type).IsEqualTo(SettingType.StringArray);
        await Assert.That(filled.TryGetAsStringArray(out string[] filledCopy)).IsTrue();
        await Assert.That(filledCopy.Length).IsEqualTo(2);
        await Assert.That(filledCopy[0]).IsEqualTo("a");
        await Assert.That(filledCopy[1]).IsEqualTo("b");

        SettingValue empty = SettingValue.StringArray([]);
        await Assert.That(empty.TryGetAsStringArray(out string[] emptyCopy)).IsTrue();
        await Assert.That(emptyCopy.Length).IsEqualTo(0);
    }

    [Test]
    public void StringArrayNullArrayThrows() =>
        _ = Assert.Throws<ArgumentNullException>(() => SettingValue.StringArray(null!));

    [Test]
    public void StringArrayNullElementThrows() =>
        _ = Assert.Throws<ArgumentNullException>(() => SettingValue.StringArray(["a", null!]));

    [Test]
    public async Task F64ArrayRoundtripIncludingEmpty()
    {
        SettingValue filled = SettingValue.F64Array([1.5, -2.0]);
        await Assert.That(filled.Type).IsEqualTo(SettingType.F64Array);
        await Assert.That(filled.TryGetAsF64Array(out double[] filledCopy)).IsTrue();
        await Assert.That(filledCopy.Length).IsEqualTo(2);
        await Assert.That(filledCopy[0]).IsEqualTo(1.5);
        await Assert.That(filledCopy[1]).IsEqualTo(-2.0);

        SettingValue empty = SettingValue.F64Array([]);
        await Assert.That(empty.TryGetAsF64Array(out double[] emptyCopy)).IsTrue();
        await Assert.That(emptyCopy.Length).IsEqualTo(0);
    }

    [Test]
    public async Task U64ArrayRoundtripIncludingEmpty()
    {
        SettingValue filled = SettingValue.U64Array([1UL, 2UL]);
        await Assert.That(filled.Type).IsEqualTo(SettingType.U64Array);
        await Assert.That(filled.TryGetAsU64Array(out ulong[] filledCopy)).IsTrue();
        await Assert.That(filledCopy.Length).IsEqualTo(2);
        await Assert.That(filledCopy[0]).IsEqualTo(1UL);
        await Assert.That(filledCopy[1]).IsEqualTo(2UL);

        SettingValue empty = SettingValue.U64Array([]);
        await Assert.That(empty.TryGetAsU64Array(out ulong[] emptyCopy)).IsTrue();
        await Assert.That(emptyCopy.Length).IsEqualTo(0);
    }

    [Test]
    public async Task I64ArrayRoundtripIncludingEmpty()
    {
        SettingValue filled = SettingValue.I64Array([-1L, 2L]);
        await Assert.That(filled.Type).IsEqualTo(SettingType.I64Array);
        await Assert.That(filled.TryGetAsI64Array(out long[] filledCopy)).IsTrue();
        await Assert.That(filledCopy.Length).IsEqualTo(2);
        await Assert.That(filledCopy[0]).IsEqualTo(-1L);
        await Assert.That(filledCopy[1]).IsEqualTo(2L);

        SettingValue empty = SettingValue.I64Array([]);
        await Assert.That(empty.TryGetAsI64Array(out long[] emptyCopy)).IsTrue();
        await Assert.That(emptyCopy.Length).IsEqualTo(0);
    }

    [Test]
    public async Task U64ArrayWrongAccessorReturnsFalseAndEmpty()
    {
        SettingValue v = SettingValue.U64(1);
        await Assert.That(v.TryGetAsU64Array(out ulong[] result)).IsFalse();
        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SiblingArrayWrongAccessorReturnsFalseAndEmpty()
    {
        SettingValue v = SettingValue.U64(1);
        await Assert.That(v.TryGetAsBoolArray(out bool[] b)).IsFalse();
        await Assert.That(b.Length).IsEqualTo(0);
        await Assert.That(v.TryGetAsStringArray(out string[] s)).IsFalse();
        await Assert.That(s.Length).IsEqualTo(0);
        await Assert.That(v.TryGetAsF64Array(out double[] d)).IsFalse();
        await Assert.That(d.Length).IsEqualTo(0);
        await Assert.That(v.TryGetAsI64Array(out long[] i)).IsFalse();
        await Assert.That(i.Length).IsEqualTo(0);
    }

    [Test]
    public async Task U64ArrayMutatingTryGetCopyDoesNotAffectStoredValue()
    {
        SettingValue v = SettingValue.U64Array([10UL, 20UL]);
        v.TryGetAsU64Array(out ulong[] first);
        first[0] = 99UL;
        v.TryGetAsU64Array(out ulong[] second);
        await Assert.That(second[0]).IsEqualTo(10UL);
    }

    [Test]
    public async Task U64ArrayEqualCopiesAreEqual()
    {
        SettingValue a = SettingValue.U64Array([1UL, 2UL]);
        SettingValue b = SettingValue.U64Array([1UL, 2UL]);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task U64ArrayUnequalLengthsAreNotEqual()
    {
        SettingValue a = SettingValue.U64Array([1UL]);
        SettingValue b = SettingValue.U64Array([1UL, 2UL]);
        await Assert.That(a == b).IsFalse();
    }

    [Test]
    public async Task F64ArrayNaNBitsAreEqual()
    {
        SettingValue a = SettingValue.F64Array([double.NaN]);
        SettingValue b = SettingValue.F64Array([double.NaN]);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task U64ArrayAndScalarU64AreNotEqual()
    {
        SettingValue a = SettingValue.U64Array([1UL]);
        SettingValue b = SettingValue.U64(1UL);
        await Assert.That(a == b).IsFalse();
    }

    [Test]
    public async Task U64ArrayFormatEmptyAndTwoElements()
    {
        await _AssertTryFormat(SettingValue.U64Array([]), "[0 u64]");
        await _AssertTryFormat(SettingValue.U64Array([1UL, 2UL]), "[2 u64]");
    }

    [Test]
    public async Task GetHashCodeEqualU64ArraysAreStableAndEqual()
    {
        SettingValue a = SettingValue.U64Array([3UL, 4UL]);
        SettingValue b = SettingValue.U64Array([3UL, 4UL]);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.GetHashCode()).IsEqualTo(a.GetHashCode());
    }

    [Test]
    public void BoolArrayNullArrayThrows() =>
        _ = Assert.Throws<ArgumentNullException>(() => SettingValue.BoolArray(null!));

    [Test]
    public void F64ArrayNullArrayThrows() =>
        _ = Assert.Throws<ArgumentNullException>(() => SettingValue.F64Array(null!));

    [Test]
    public void U64ArrayNullArrayThrows() =>
        _ = Assert.Throws<ArgumentNullException>(() => SettingValue.U64Array(null!));

    [Test]
    public void I64ArrayNullArrayThrows() =>
        _ = Assert.Throws<ArgumentNullException>(() => SettingValue.I64Array(null!));

    // === Equality ===

    [Test]
    public async Task EqualitySameBoolAreEqual()
    {
        SettingValue a = SettingValue.Bool(true);
        SettingValue b = SettingValue.Bool(true);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task EqualityDifferentBoolAreNotEqual()
    {
        SettingValue a = SettingValue.Bool(true);
        SettingValue b = SettingValue.Bool(false);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task EqualitySameF64AreEqual()
    {
        SettingValue a = SettingValue.F64(3.14);
        SettingValue b = SettingValue.F64(3.14);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task EqualityNaNF64AreEqual()
    {
        // NaN-safe: NaN == NaN should be true via bit comparison
        SettingValue a = SettingValue.F64(double.NaN);
        SettingValue b = SettingValue.F64(double.NaN);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task EqualityDifferentTypeAreNotEqual()
    {
        SettingValue a = SettingValue.Bool(true);
        SettingValue b = SettingValue.U64(1);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task EqualitySameStringAreEqual()
    {
        SettingValue a = SettingValue.String("test");
        SettingValue b = SettingValue.String("test");
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task EqualityDifferentStringAreNotEqual()
    {
        SettingValue a = SettingValue.String("foo");
        SettingValue b = SettingValue.String("bar");
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task EqualitySameBytesAreEqual()
    {
        SettingValue a = SettingValue.Bytes([1, 2, 3]);
        SettingValue b = SettingValue.Bytes([1, 2, 3]);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task EqualityDifferentBytesAreNotEqual()
    {
        SettingValue a = SettingValue.Bytes([1, 2, 3]);
        SettingValue b = SettingValue.Bytes([1, 2, 4]);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task EqualitySameEnumAreEqual()
    {
        SettingValue a = SettingValue.Enum("High", 2);
        SettingValue b = SettingValue.Enum("High", 2);
        await Assert.That(a == b).IsTrue();
    }

    // === Implicit Conversions ===

    [Test]
    public async Task ImplicitConversionBool()
    {
        SettingValue v = true;
        await Assert.That(v.Type).IsEqualTo(SettingType.Bool);
        bool success = v.TryGetAsBool(out bool result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ImplicitConversionString()
    {
        SettingValue v = "hello";
        await Assert.That(v.Type).IsEqualTo(SettingType.String);
        bool success = v.TryGetAsString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task ImplicitConversionDouble()
    {
        SettingValue v = 2.5;
        await Assert.That(v.Type).IsEqualTo(SettingType.F64);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(2.5);
    }

    [Test]
    public async Task ImplicitConversionUInt64()
    {
        const ulong sentinel = 0xBADC0FFEUL;
        SettingValue v = sentinel;
        await Assert.That(v.Type).IsEqualTo(SettingType.U64);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(sentinel);
    }

    // === Bytes immutability ===

    [Test]
    public async Task BytesMutatingSourceArrayDoesNotAffectStoredValue()
    {
        // Verify that SettingValue.Bytes() takes a defensive copy of the input array,
        // so subsequent mutations of the caller's array do not affect the stored value.
        byte[] source = [10, 20, 30];
        SettingValue v = SettingValue.Bytes(source);

        source[0] = 99; // mutate source after storage

        bool success = v.TryGetAsBytes(out byte[] stored);
        await Assert.That(success).IsTrue();
        await Assert.That(stored[0]).IsEqualTo((byte)10); // original value preserved
    }

    [Test]
    public async Task BytesMutatingOutputArrayDoesNotAffectStoredValue()
    {
        // Verify that TryGetAsBytes() returns a defensive copy, so mutations of the
        // returned array do not affect the internal storage.
        byte[] source = [10, 20, 30];
        SettingValue v = SettingValue.Bytes(source);

        v.TryGetAsBytes(out byte[] first);
        first[0] = 99; // mutate the returned copy

        v.TryGetAsBytes(out byte[] second);
        await Assert.That(second[0]).IsEqualTo((byte)10); // internal storage unchanged
    }

    [Test]
    public async Task BytesTwoCallsToTryGetAsBytesReturnDistinctArrayInstances()
    {
        // Each TryGetAsBytes() call must return a fresh copy, not the same reference.
        SettingValue v = SettingValue.Bytes([1, 2, 3]);
        v.TryGetAsBytes(out byte[] a);
        v.TryGetAsBytes(out byte[] b);
        await Assert.That(ReferenceEquals(a, b)).IsFalse();
    }

    // === Default struct ===

    [Test]
    public async Task DefaultHasBoolType()
    {
        // Default struct: all zeros → Type = Bool(0), Bits = 0 (false)
        SettingValue v = default;
        await Assert.That(v.Type).IsEqualTo(SettingType.Bool);
    }

    // === GetHashCode ===

    [Test]
    public async Task GetHashCodeAllTypesAreStable()
    {
        SettingValue[] values =
        [
            SettingValue.Bool(true),
            SettingValue.String("hash"),
            SettingValue.F64(1.5),
            SettingValue.U64(99),
            SettingValue.I64(-7),
            SettingValue.Bytes([1, 2]),
            SettingValue.Enum("Mid", 1),
            SettingValue.BoolArray([true]),
            SettingValue.StringArray(["x"]),
            SettingValue.F64Array([1.5]),
            SettingValue.U64Array([99UL]),
            SettingValue.I64Array([-7L]),
        ];

        foreach (SettingValue v in values)
        {
            int h1 = v.GetHashCode();
            int h2 = v.GetHashCode();
            await Assert.That(h1).IsEqualTo(h2);
            await Assert.That(((object)v).GetHashCode()).IsEqualTo(h1);
        }
    }

    [Test]
    public async Task EqualsObjectBoxedWorks()
    {
        SettingValue a = SettingValue.String("x");
        object boxed = a;
        await Assert.That(a.Equals(boxed)).IsTrue();
        await Assert.That(a.Equals((object)SettingValue.U64(1))).IsFalse();
        object? nullBox = null;
        await Assert.That(a.Equals(nullBox)).IsFalse();
    }

    // === TryFormat (char) ===

    [Test]
    public async Task TryFormatAllTypesWriteExpectedText()
    {
        await _AssertTryFormat(SettingValue.Bool(true), "True");
        await _AssertTryFormat(SettingValue.Bool(false), "False");
        await _AssertTryFormat(SettingValue.F64(3.5), "3.5");
        await _AssertTryFormat(SettingValue.U64(42), "42");
        await _AssertTryFormat(SettingValue.I64(-9), "-9");
        await _AssertTryFormat(SettingValue.String("hello"), "hello");
        await _AssertTryFormat(SettingValue.Bytes([1, 2, 3]), "[3 bytes]");
        await _AssertTryFormat(SettingValue.Enum("High", 2), "High (2)");
        await _AssertTryFormat(SettingValue.BoolArray([true]), "[1 bool]");
        await _AssertTryFormat(SettingValue.StringArray(["a"]), "[1 string]");
        await _AssertTryFormat(SettingValue.F64Array([1.0]), "[1 f64]");
        await _AssertTryFormat(SettingValue.I64Array([1L]), "[1 i64]");
    }

    [Test]
    public async Task TryFormatInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.String("toolong");
        Span<char> tiny = stackalloc char[2];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task ToStringWithFormatUsesTryFormat()
    {
        SettingValue v = SettingValue.F64(2.0);
        await Assert.That(v.ToString("F1", CultureInfo.InvariantCulture)).IsEqualTo("2.0");
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("2");
    }

    // === TryFormat (UTF-8) ===

    [Test]
    public async Task TryFormatUtf8AllTypes()
    {
        await _AssertUtf8TryFormat(SettingValue.Bool(true), "True"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.String("hello"), "hello"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.U64(7), "7"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.Bytes([0]), "[1 bytes]"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.U64Array([1UL, 2UL]), "[2 u64]"u8.ToArray());
    }

    [Test]
    public async Task TryFormatUtf8InsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.String("abcdef");
        Span<byte> tiny = stackalloc byte[2];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === Bytes(ReadOnlyMemory) factory ===

    [Test]
    public async Task BytesReadOnlyMemoryRoundtrip()
    {
        ReadOnlyMemory<byte> mem = new byte[] { 9, 8, 7 };
        SettingValue v = SettingValue.Bytes(mem);
        bool ok = v.TryGetAsBytes(out byte[] stored);
        await Assert.That(ok).IsTrue();
        await Assert.That(stored.Length).IsEqualTo(3);
        await Assert.That(stored[0]).IsEqualTo((byte)9);
        await Assert.That(stored[1]).IsEqualTo((byte)8);
        await Assert.That(stored[2]).IsEqualTo((byte)7);
    }

    [Test]
    public async Task EqualitySameI64AreEqual()
    {
        SettingValue a = SettingValue.I64(100);
        SettingValue b = SettingValue.I64(100);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task TryFormatBoolInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Bool(true);
        Span<char> tiny = stackalloc char[3];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatBytesEmptyBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes([]);
        Span<char> empty = [];
        bool ok = v.TryFormat(empty, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8CharFallbackInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("VeryLongEnumName", 999999);
        Span<byte> tiny = stackalloc byte[4];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task StringToStringReturnsRawStringNotFormatted()
    {
        SettingValue v = SettingValue.String("plain");
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("plain");
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("plain");
    }

    [Test]
    public async Task EnumLabelPartialBufferFailuresReturnFalse()
    {
        SettingValue parenFail = SettingValue.Enum("A", 1);
        Span<char> twoChars = stackalloc char[2];
        bool parenResult = parenFail.TryFormat(twoChars, out int parenWritten, default, CultureInfo.InvariantCulture);
        await Assert.That(parenResult).IsFalse();
        await Assert.That(parenWritten).IsEqualTo(0);

        SettingValue closeFail = SettingValue.Enum("X", ulong.MaxValue);
        Span<char> closeTiny = stackalloc char[23];
        bool closeResult = closeFail.TryFormat(closeTiny, out int closeWritten, default, CultureInfo.InvariantCulture);
        await Assert.That(closeResult).IsFalse();
        await Assert.That(closeWritten).IsEqualTo(0);
    }

    [Test]
    public async Task BytesBothNullReferencesAreEqual()
    {
        SettingValue left = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1]), "_ReferenceValue", null);
        SettingValue right = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([2]), "_ReferenceValue", null);
        await Assert.That(left == right).IsTrue();
    }

    [Test]
    public async Task BytesOneNullReferenceAreNotEqual()
    {
        SettingValue withNull = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1, 2]), "_ReferenceValue", null);
        SettingValue withData = SettingValue.Bytes([1, 2]);
        await Assert.That(withNull == withData).IsFalse();
        await Assert.That(withNull.GetHashCode()).IsNotEqualTo(withData.GetHashCode());
    }

    [Test]
    public async Task EqualsUnknownTypeReturnsFalse()
    {
        SettingValue unknown = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        await Assert.That(unknown.Equals(SettingValue.Bool(true))).IsFalse();
        await Assert.That(unknown.GetHashCode()).IsEqualTo(HashCode.Combine((SettingType)99));
    }

    [Test]
    public async Task EqualsUnknownTypeSameTypeUsesDefaultArm()
    {
        SettingValue left = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        SettingValue right = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)99);
        await Assert.That(left.Equals(right)).IsFalse();
    }

    [Test]
    public async Task ToStringLongEnumNameUsesHeapFallback()
    {
        string longName = new('Z', 300);
        SettingValue v = SettingValue.Enum(longName, 1);
        string text = v.ToString(null, CultureInfo.InvariantCulture);
        await Assert.That(text.StartsWith(longName, StringComparison.Ordinal)).IsTrue();
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture).StartsWith(longName, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ToStringVeryLongEnumNameFormatsFully()
    {
        string longName = new('Z', 5000);
        SettingValue v = SettingValue.Enum(longName, 1);
        string expected = longName + " (1)";
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo(expected);
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetHashCodeEqualValuesShareHashCodeAllNumericArms()
    {
        SettingValue u64A = SettingValue.U64(123);
        SettingValue u64B = SettingValue.U64(123);
        SettingValue i64A = SettingValue.I64(-55);
        SettingValue i64B = SettingValue.I64(-55);
        SettingValue enumA = SettingValue.Enum("Mid", 1);
        SettingValue enumB = SettingValue.Enum("Mid", 1);

        await Assert.That(u64A.GetHashCode()).IsEqualTo(u64B.GetHashCode());
        await Assert.That(i64A.GetHashCode()).IsEqualTo(i64B.GetHashCode());
        await Assert.That(enumA.GetHashCode()).IsEqualTo(enumB.GetHashCode());
    }

    [Test]
    public async Task EqualityDifferentU64AndI64AreNotEqual()
    {
        await Assert.That(SettingValue.U64(1) != SettingValue.U64(2)).IsTrue();
        await Assert.That(SettingValue.I64(1) != SettingValue.I64(2)).IsTrue();
        await Assert.That(SettingValue.Enum("A", 0) != SettingValue.Enum("B", 0)).IsTrue();
        await Assert.That(SettingValue.Enum("A", 0) != SettingValue.Enum("A", 1)).IsTrue();
    }

    [Test]
    public async Task TryFormatStringNullReferenceReturnsTrueWithZeroWritten()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.String("ignored"), "_ReferenceValue", null);
        Span<char> buffer = stackalloc char[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatEnumNullReferenceReturnsTrueWithZeroWritten()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Enum("High", 1), "_ReferenceValue", null);
        Span<char> buffer = stackalloc char[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUnknownTypeReturnsTrueWithZeroWritten()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)99);
        Span<char> buffer = stackalloc char[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatBytesLabelDigitBufferTooSmallReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes(new byte[5000]);
        Span<char> buffer = stackalloc char[1];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatBytesLabelSuffixBufferTooSmallReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes(new byte[12]);
        Span<char> buffer = stackalloc char[4];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatEnumLabelDigitBufferTooSmallReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("X", ulong.MaxValue);
        Span<char> buffer = stackalloc char[5];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8NonStringCharFallbackInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Enum(new string('E', 240), 1);
        Span<byte> buffer = stackalloc byte[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task EqualsMismatchedUnknownTypeReturnsFalse()
    {
        SettingValue left = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        SettingValue right = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)98);
        await Assert.That(left.Equals(right)).IsFalse();
    }

    [Test]
    public async Task TryFormatBytesNullReferenceUsesZeroByteLabel()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1]), "_ReferenceValue", null);
        char[] buffer = new char[16];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(new string(buffer, 0, written)).IsEqualTo("[0 bytes]");
    }

    [Test]
    public async Task ToStringWhenFormattedValueEmptyReturnsEmptyString()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.String("x"), "_Type", (SettingType)99);
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("");
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("");
    }

    [Test]
    public async Task TryFormatUtf8LongEnumNameHeapFallbackSucceeds()
    {
        string longName = new('Z', 300);
        SettingValue v = SettingValue.Enum(longName, 1);
        byte[] buffer = new byte[1024];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsGreaterThan(300);
    }

    [Test]
    public async Task TryFormatUtf8WhenCharTryFormatFailsReturnsFalse()
    {
        SettingValue v = SettingValue.Enum(new string('Z', 5000), 1);
        Span<byte> buffer = stackalloc byte[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8NonAsciiEnumDestinationEqualToCharCountReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("Größe", 1UL);
        Span<char> chars = stackalloc char[64];
        bool charOk = v.TryFormat(chars, out int charCount, default, CultureInfo.InvariantCulture);
        await Assert.That(charOk).IsTrue();

        Span<byte> utf8 = stackalloc byte[charCount];
        bool utf8Ok = v.TryFormat(utf8, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(utf8Ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8NonAsciiStringDestinationEqualToCharCountReturnsFalse()
    {
        SettingValue v = SettingValue.String("Größe");
        Span<char> chars = stackalloc char[16];
        bool charOk = v.TryFormat(chars, out int charCount, default, CultureInfo.InvariantCulture);
        await Assert.That(charOk).IsTrue();

        Span<byte> utf8 = stackalloc byte[charCount];
        bool utf8Ok = v.TryFormat(utf8, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(utf8Ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatEnumLabelNameBufferTooSmallReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("TooLongName", 1);
        char[] buffer = new char[4];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === TryGetStringSize / convenience formatting ===

    [Test]
    public async Task TryGetStringSizeBoolTrueReturnsFour()
    {
        bool ok = SettingValue.Bool(true).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(4);
    }

    [Test]
    public async Task TryGetStringSizeBoolFalseReturnsFive()
    {
        bool ok = SettingValue.Bool(false).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(5);
    }

    [Test]
    public async Task TryGetStringSizeStringReturnsExactLength()
    {
        bool ok = SettingValue.String("hello").TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(5);
    }

    [Test]
    public async Task TryGetStringSizeBytesReturnsExactLabelLength()
    {
        bool ok = SettingValue.Bytes([1, 2, 3]).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo("[3 bytes]".Length);
    }

    [Test]
    public async Task TryGetStringSizeU64ArrayReturnsExactLabelLength()
    {
        bool ok = SettingValue.U64Array([1UL, 2UL]).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo("[2 u64]".Length);
    }

    [Test]
    public async Task TryGetStringSizeEnumReturnsExactLabelLength()
    {
        bool ok = SettingValue.Enum("High", 2).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo("High (2)".Length);
    }

    [Test]
    public async Task TryGetStringSizeU64ReturnsExactDigitCount()
    {
        bool ok = SettingValue.U64(ulong.MaxValue).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(20);
        await Assert.That(size).IsEqualTo(SettingValue.U64(ulong.MaxValue).ToString(null, CultureInfo.InvariantCulture).Length);
    }

    [Test]
    public async Task TryGetStringSizeI64MinValueReturnsTwenty()
    {
        bool ok = SettingValue.I64(long.MinValue).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(20);
        await Assert.That(size).IsEqualTo(SettingValue.I64(long.MinValue).ToString(null, CultureInfo.InvariantCulture).Length);
    }

    [Test]
    public async Task TryGetStringSizeF64ReturnsFalse()
    {
        bool ok = SettingValue.F64(3.14).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetStringSizeU64WithFormatReturnsFalse()
    {
        bool ok = SettingValue.U64(42).TryGetStringSize("X", CultureInfo.InvariantCulture, out int size);
        await Assert.That(ok).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetStringSizeI64WithFormatReturnsFalse()
    {
        bool ok = SettingValue.I64(-7).TryGetStringSize("D", CultureInfo.InvariantCulture, out int size);
        await Assert.That(ok).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetStringSizeNullReferencesReturnZeroOrEmptyLabel()
    {
        SettingValue nullString = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.String("ignored"), "_ReferenceValue", null);
        bool stringOk = nullString.TryGetStringSize(default, null, out int stringSize);
        await Assert.That(stringOk).IsTrue();
        await Assert.That(stringSize).IsEqualTo(0);

        SettingValue nullEnum = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Enum("High", 1), "_ReferenceValue", null);
        bool enumOk = nullEnum.TryGetStringSize(default, null, out int enumSize);
        await Assert.That(enumOk).IsTrue();
        await Assert.That(enumSize).IsEqualTo(0);

        SettingValue nullBytes = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1]), "_ReferenceValue", null);
        bool bytesOk = nullBytes.TryGetStringSize(default, null, out int bytesSize);
        await Assert.That(bytesOk).IsTrue();
        await Assert.That(bytesSize).IsEqualTo("[0 bytes]".Length);
    }

    [Test]
    public async Task TryGetStringSizeUnknownTypeReturnsZero()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        bool ok = v.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task FormatIntoWritesExpectedText()
    {
        char[] buffer = new char[16];
        int written = SettingValue.U64(42).FormatInto(buffer);
        await Assert.That(written).IsEqualTo(2);
        await Assert.That(new string(buffer, 0, written)).IsEqualTo("42");
    }

    [Test]
    public async Task FormatIntoInsufficientBufferReturnsZero()
    {
        char[] tiny = new char[1];
        int written = SettingValue.Bool(true).FormatInto(tiny);
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatStringReturnsStoredInstance()
    {
        string stored = "plain";
        SettingValue v = SettingValue.String(stored);
        await Assert.That(ReferenceEquals(v.Format(), stored)).IsTrue();
    }

    [Test]
    public async Task FormatNumberReturnsInvariantText()
    {
        string numberText = SettingValue.I64(-12345).Format();

        await Assert.That(numberText).IsEqualTo("-12345");
    }

    [Test]
    public async Task FormatEmptyStringReturnsEmpty()
    {
        string text = SettingValue.String("").Format();

        await Assert.That(text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FormatEnumLabelBeyondTheStackLimitMatchesTryFormat()
    {
        // Array and bytes labels carry only a count and stay short; the pooled path (above 256 characters) is reached by an
        // enum label whose name is longer than the limit.
        string longName = new('E', 400);
        SettingValue enumValue = SettingValue.Enum(longName, 7);
        enumValue.TryGetStringSize(default, CultureInfo.InvariantCulture, out int size);
        char[] large = new char[size + 8];

        bool ok = enumValue.TryFormat(large, out int written, default, CultureInfo.InvariantCulture);
        string formatted = enumValue.Format();

        await Assert.That(size).IsGreaterThan(256);
        await Assert.That(ok).IsTrue();
        await Assert.That(formatted).IsEqualTo(new string(large, 0, written));
        await Assert.That(formatted.Length).IsEqualTo(size);
    }

    [Test]
    public async Task FormatF64UsesTheFixedStackBuffer()
    {
        // F64 has no exact size hint, so Format() formats it into a fixed stack buffer sized for the longest double text.
        SettingValue f64 = SettingValue.F64(-1.7976931348623157E+308);

        string text = f64.Format();

        await Assert.That(text).IsEqualTo((-1.7976931348623157E+308).ToString("R", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task TryFormatUtf8NullReferencesWriteEmptyOrZeroBytesLabel()
    {
        SettingValue nullString = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.String("ignored"), "_ReferenceValue", null);
        byte[] buffer = new byte[16];
        bool stringOk = nullString.TryFormat(buffer.AsSpan(), out int stringWritten, default, CultureInfo.InvariantCulture);
        await Assert.That(stringOk).IsTrue();
        await Assert.That(stringWritten).IsEqualTo(0);

        SettingValue nullEnum = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Enum("High", 1), "_ReferenceValue", null);
        bool enumOk = nullEnum.TryFormat(buffer.AsSpan(), out int enumWritten, default, CultureInfo.InvariantCulture);
        await Assert.That(enumOk).IsTrue();
        await Assert.That(enumWritten).IsEqualTo(0);

        SettingValue unknown = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)99);
        bool unknownOk = unknown.TryFormat(buffer.AsSpan(), out int unknownWritten, default, CultureInfo.InvariantCulture);
        await Assert.That(unknownOk).IsTrue();
        await Assert.That(unknownWritten).IsEqualTo(0);

        SettingValue nullBytes = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1]), "_ReferenceValue", null);
        bool bytesOk = nullBytes.TryFormat(buffer.AsSpan(), out int bytesWritten, default, CultureInfo.InvariantCulture);
        string bytesLabel = Encoding.UTF8.GetString(buffer.AsSpan(0, bytesWritten));
        await Assert.That(bytesOk).IsTrue();
        await Assert.That(bytesLabel).IsEqualTo("[0 bytes]");
    }

    [Test]
    public async Task FormatMatchesToStringAllTypes()
    {
        SettingValue[] values =
        [
            SettingValue.Bool(true),
            SettingValue.Bool(false),
            SettingValue.F64(3.5),
            SettingValue.U64(42),
            SettingValue.I64(-9),
            SettingValue.String("hello"),
            SettingValue.Bytes([1, 2, 3]),
            SettingValue.Enum("High", 2),
        ];

        foreach (SettingValue v in values)
        {
            await Assert.That(v.Format()).IsEqualTo(v.ToString(null, CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public async Task FormatUnknownTypeReturnsEmpty()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);

        await Assert.That(v.Format()).IsEqualTo("");
    }

    [Test]
    public async Task ToStringNullOrEmptyFormatUsesDefaultFormat()
    {
        SettingValue v = SettingValue.U64(42);
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("42");
        await Assert.That(v.ToString("", CultureInfo.InvariantCulture)).IsEqualTo("42");
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("42");
    }

    [Test]
    public async Task ToStringUnknownTypeWithFormatReturnsEmpty()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("");
    }

    [Test]
    public async Task ToStringF64CustomFormatUsesHeapWhenLongerThanStack()
    {
        SettingValue v = SettingValue.F64(1.0);
        string text = v.ToString("F300", CultureInfo.InvariantCulture);
        await Assert.That(text.StartsWith("1.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Length).IsEqualTo(302);
    }

    [Test]
    public async Task ToStringLongCustomFormatIgnoresTheProviderLikeTheShortOne()
    {
        // TryFormat always formats with the invariant culture; the text must not change only because it outgrew the stack buffer.
        NumberFormatInfo commaDecimal = new() { NumberDecimalSeparator = ",", NegativeSign = "~" };
        SettingValue negative = SettingValue.F64(-1.5);

#pragma warning disable CSC009 // Id: CSC009. Why: non-invariant provider on purpose; the test proves it is ignored. Scope: 3 lines. User approved.
        string shortText = negative.ToString("F1", commaDecimal);
        string longText = negative.ToString("F300", commaDecimal);
        string longIntegerText = SettingValue.I64(-1).ToString("D300", commaDecimal);
#pragma warning restore CSC009

        await Assert.That(shortText).IsEqualTo("-1.5");
        await Assert.That(longText.StartsWith("-1.5", StringComparison.Ordinal)).IsTrue();
        await Assert.That(longIntegerText.StartsWith('-')).IsTrue();
    }

    [Test]
    public async Task ToStringIntegerCustomFormatUsesBclWhenLongerThanStack()
    {
        string u64Text = SettingValue.U64(1).ToString("D300", CultureInfo.InvariantCulture);
        await Assert.That(u64Text.Length).IsEqualTo(300);
        await Assert.That(u64Text.EndsWith('1')).IsTrue();

        string i64Text = SettingValue.I64(-1).ToString("D300", CultureInfo.InvariantCulture);
        await Assert.That(i64Text.Length).IsEqualTo(301);
        await Assert.That(i64Text.StartsWith('-')).IsTrue();
    }

    [Test]
    public async Task ToStringEnumWithFormatUsesKnownSizePath()
    {
        SettingValue v = SettingValue.Enum("High", 2);
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("High (2)");
    }

    [Test]
    public async Task TryFormatUtf8NonAsciiEnumSucceedsWhenDestIsLargeEnough()
    {
        SettingValue v = SettingValue.Enum("Größe", 1UL);
        byte[] expected = Encoding.UTF8.GetBytes("Größe (1)");
        byte[] buffer = new byte[32];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        string actual = Encoding.UTF8.GetString(buffer.AsSpan(0, written));
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(actual).IsEqualTo("Größe (1)");
    }

    [Test]
    public async Task TryFormatUtf8BoolInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Bool(true);
        byte[] tiny = new byte[3];
        bool ok = v.TryFormat(tiny.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8BytesInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes([]);
        byte[] tiny = new byte[1];
        bool ok = v.TryFormat(tiny.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8AsciiEnumInsufficientBufferReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("High", 2);
        byte[] tiny = new byte[3];
        bool ok = v.TryFormat(tiny.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormatUtf8NonAsciiStringSucceedsWhenDestIsLargeEnough()
    {
        SettingValue v = SettingValue.String("Größe");
        byte[] expected = Encoding.UTF8.GetBytes("Größe");
        byte[] buffer = new byte[16];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        string actual = Encoding.UTF8.GetString(buffer.AsSpan(0, written));
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(actual).IsEqualTo("Größe");
    }

    [Test]
    public async Task TryGetStringSizeI64NegativeIncludesSign()
    {
        bool negativeOk = SettingValue.I64(-42).TryGetStringSize(default, null, out int negativeSize);
        await Assert.That(negativeOk).IsTrue();
        await Assert.That(negativeSize).IsEqualTo(3);

        bool zeroOk = SettingValue.I64(0).TryGetStringSize(default, null, out int zeroSize);
        await Assert.That(zeroOk).IsTrue();
        await Assert.That(zeroSize).IsEqualTo(1);
    }

    [Test]
    public async Task TryFormatUtf8NumericAndBoolRoundtrip()
    {
        await _AssertUtf8TryFormat(SettingValue.Bool(false), "False"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.I64(-9), "-9"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.F64(3.5), "3.5"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.Enum("High", 2), "High (2)"u8.ToArray());
    }

    private static async Task _AssertTryFormat(SettingValue v, string expected)
    {
        char[] buffer = new char[64];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        string formatted = new(buffer, 0, written);
        await Assert.That(ok).IsTrue();
        await Assert.That(formatted).IsEqualTo(expected);
    }

    private static async Task _AssertUtf8TryFormat(SettingValue v, byte[] expectedUtf8)
    {
        byte[] buffer = new byte[64];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        byte[] actual = buffer.AsSpan(0, written).ToArray();
        await Assert.That(ok).IsTrue();
        await Assert.That(actual.Length).IsEqualTo(expectedUtf8.Length);
        for (int i = 0; i < expectedUtf8.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expectedUtf8[i]);
        }
    }
}
