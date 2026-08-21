// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingValue"/> tagged union struct — all 7 types,
/// equality, implicit conversions, and edge cases.
/// </summary>
internal sealed class SettingValueTests
{
    // === Bool ===

    [Test]
    public async Task Bool_True_Roundtrip()
    {
        SettingValue v = SettingValue.Bool(true);
        await Assert.That(v.Type).IsEqualTo(SettingType.Bool);
        bool success = v.TryGetAsBool(out bool result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Bool_False_Roundtrip()
    {
        SettingValue v = SettingValue.Bool(false);
        bool success = v.TryGetAsBool(out bool result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Bool_WrongAccessor_ReturnsFalse()
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
    public async Task String_Roundtrip()
    {
        SettingValue v = SettingValue.String("hello");
        await Assert.That(v.Type).IsEqualTo(SettingType.String);
        bool success = v.TryGetAsString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task String_Empty_Roundtrip()
    {
        SettingValue v = SettingValue.String("");
        bool success = v.TryGetAsString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task String_WrongAccessor_ReturnsFalse()
    {
        SettingValue v = SettingValue.String("test");
        await Assert.That(v.TryGetAsBool(out _)).IsFalse();
        await Assert.That(v.TryGetAsF64(out _)).IsFalse();
    }

    // === F64 ===

    [Test]
    public async Task F64_Roundtrip()
    {
        SettingValue v = SettingValue.F64(3.14);
        await Assert.That(v.Type).IsEqualTo(SettingType.F64);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(3.14);
    }

    [Test]
    public async Task F64_Negative_Roundtrip()
    {
        SettingValue v = SettingValue.F64(-1.5);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(-1.5);
    }

    [Test]
    public async Task F64_Zero_Roundtrip()
    {
        SettingValue v = SettingValue.F64(0.0);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(0.0);
    }

    [Test]
    public async Task F64_NaN_Roundtrip()
    {
        SettingValue v = SettingValue.F64(double.NaN);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(double.NaN);
    }

    [Test]
    public async Task F64_Infinity_Roundtrip()
    {
        SettingValue v = SettingValue.F64(double.PositiveInfinity);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(double.PositiveInfinity);
    }

    // === U64 ===

    [Test]
    public async Task U64_Roundtrip()
    {
        SettingValue v = SettingValue.U64(42UL);
        await Assert.That(v.Type).IsEqualTo(SettingType.U64);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(42UL);
    }

    [Test]
    public async Task U64_MaxValue_Roundtrip()
    {
        SettingValue v = SettingValue.U64(ulong.MaxValue);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task U64_Zero_Roundtrip()
    {
        SettingValue v = SettingValue.U64(0UL);
        bool success = v.TryGetAsU64(out ulong result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(0UL);
    }

    // === I64 ===

    [Test]
    public async Task I64_Roundtrip()
    {
        SettingValue v = SettingValue.I64(-42L);
        await Assert.That(v.Type).IsEqualTo(SettingType.I64);
        bool success = v.TryGetAsI64(out long result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(-42L);
    }

    [Test]
    public async Task I64_MaxValue_Roundtrip()
    {
        SettingValue v = SettingValue.I64(long.MaxValue);
        bool success = v.TryGetAsI64(out long result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task I64_MinValue_Roundtrip()
    {
        SettingValue v = SettingValue.I64(long.MinValue);
        bool success = v.TryGetAsI64(out long result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(long.MinValue);
    }

    // === Bytes ===

    [Test]
    public async Task Bytes_Roundtrip()
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
    public async Task Bytes_Empty_Roundtrip()
    {
        byte[] data = [];
        SettingValue v = SettingValue.Bytes(data);
        bool success = v.TryGetAsBytes(out byte[] result);
        await Assert.That(success).IsTrue();
        await Assert.That(result.Length).IsEqualTo(0);
    }

    // === Enum ===

    [Test]
    public async Task Enum_Roundtrip()
    {
        SettingValue v = SettingValue.Enum("High", 2);
        await Assert.That(v.Type).IsEqualTo(SettingType.Enum);
        bool success = v.TryGetAsEnum(out (string Name, ulong Value) e);
        await Assert.That(success).IsTrue();
        await Assert.That(e.Name).IsEqualTo("High");
        await Assert.That(e.Value).IsEqualTo(2UL);
    }

    // === Equality ===

    [Test]
    public async Task Equality_SameBool_AreEqual()
    {
        SettingValue a = SettingValue.Bool(true);
        SettingValue b = SettingValue.Bool(true);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equality_DifferentBool_AreNotEqual()
    {
        SettingValue a = SettingValue.Bool(true);
        SettingValue b = SettingValue.Bool(false);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task Equality_SameF64_AreEqual()
    {
        SettingValue a = SettingValue.F64(3.14);
        SettingValue b = SettingValue.F64(3.14);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task Equality_NaN_F64_AreEqual()
    {
        // NaN-safe: NaN == NaN should be true via bit comparison
        SettingValue a = SettingValue.F64(double.NaN);
        SettingValue b = SettingValue.F64(double.NaN);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task Equality_DifferentType_AreNotEqual()
    {
        SettingValue a = SettingValue.Bool(true);
        SettingValue b = SettingValue.U64(1);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task Equality_SameString_AreEqual()
    {
        SettingValue a = SettingValue.String("test");
        SettingValue b = SettingValue.String("test");
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task Equality_DifferentString_AreNotEqual()
    {
        SettingValue a = SettingValue.String("foo");
        SettingValue b = SettingValue.String("bar");
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task Equality_SameBytes_AreEqual()
    {
        SettingValue a = SettingValue.Bytes([1, 2, 3]);
        SettingValue b = SettingValue.Bytes([1, 2, 3]);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task Equality_DifferentBytes_AreNotEqual()
    {
        SettingValue a = SettingValue.Bytes([1, 2, 3]);
        SettingValue b = SettingValue.Bytes([1, 2, 4]);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task Equality_SameEnum_AreEqual()
    {
        SettingValue a = SettingValue.Enum("High", 2);
        SettingValue b = SettingValue.Enum("High", 2);
        await Assert.That(a == b).IsTrue();
    }

    // === Implicit Conversions ===

    [Test]
    public async Task ImplicitConversion_Bool()
    {
        SettingValue v = true;
        await Assert.That(v.Type).IsEqualTo(SettingType.Bool);
        bool success = v.TryGetAsBool(out bool result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ImplicitConversion_String()
    {
        SettingValue v = "hello";
        await Assert.That(v.Type).IsEqualTo(SettingType.String);
        bool success = v.TryGetAsString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task ImplicitConversion_Double()
    {
        SettingValue v = 2.5;
        await Assert.That(v.Type).IsEqualTo(SettingType.F64);
        bool success = v.TryGetAsF64(out double result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(2.5);
    }

    [Test]
    public async Task ImplicitConversion_UInt64()
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
    public async Task Bytes_MutatingSourceArray_DoesNotAffectStoredValue()
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
    public async Task Bytes_MutatingOutputArray_DoesNotAffectStoredValue()
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
    public async Task Bytes_TwoCallsToTryGetAsBytes_ReturnDistinctArrayInstances()
    {
        // Each TryGetAsBytes() call must return a fresh copy, not the same reference.
        SettingValue v = SettingValue.Bytes([1, 2, 3]);
        v.TryGetAsBytes(out byte[] a);
        v.TryGetAsBytes(out byte[] b);
        await Assert.That(ReferenceEquals(a, b)).IsFalse();
    }

    // === Default struct ===

    [Test]
    public async Task Default_HasBoolType()
    {
        // Default struct: all zeros → Type = Bool(0), Bits = 0 (false)
        SettingValue v = default;
        await Assert.That(v.Type).IsEqualTo(SettingType.Bool);
    }

    // === GetHashCode ===

    [Test]
    public async Task GetHashCode_AllTypes_AreStable()
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
    public async Task Equals_Object_Boxed_Works()
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
    public async Task TryFormat_AllTypes_WriteExpectedText()
    {
        await _AssertTryFormat(SettingValue.Bool(true), "True");
        await _AssertTryFormat(SettingValue.Bool(false), "False");
        await _AssertTryFormat(SettingValue.F64(3.5), "3.5");
        await _AssertTryFormat(SettingValue.U64(42), "42");
        await _AssertTryFormat(SettingValue.I64(-9), "-9");
        await _AssertTryFormat(SettingValue.String("hello"), "hello");
        await _AssertTryFormat(SettingValue.Bytes([1, 2, 3]), "[3 bytes]");
        await _AssertTryFormat(SettingValue.Enum("High", 2), "High (2)");
    }

    [Test]
    public async Task TryFormat_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.String("toolong");
        Span<char> tiny = stackalloc char[2];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task ToString_WithFormat_UsesTryFormat()
    {
        SettingValue v = SettingValue.F64(2.0);
        await Assert.That(v.ToString("F1", CultureInfo.InvariantCulture)).IsEqualTo("2.0");
        await Assert.That(v.ToString()).IsEqualTo("2");
    }

    // === TryFormat (UTF-8) ===

    [Test]
    public async Task TryFormat_Utf8_AllTypes()
    {
        await _AssertUtf8TryFormat(SettingValue.Bool(true), "True"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.String("hello"), "hello"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.U64(7), "7"u8.ToArray());
        await _AssertUtf8TryFormat(SettingValue.Bytes([0]), "[1 bytes]"u8.ToArray());
    }

    [Test]
    public async Task TryFormat_Utf8_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.String("abcdef");
        Span<byte> tiny = stackalloc byte[2];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === Bytes(ReadOnlyMemory) factory ===

    [Test]
    public async Task Bytes_ReadOnlyMemory_Roundtrip()
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
    public async Task Equality_SameI64_AreEqual()
    {
        SettingValue a = SettingValue.I64(100);
        SettingValue b = SettingValue.I64(100);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task TryFormat_Bool_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Bool(true);
        Span<char> tiny = stackalloc char[3];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Bytes_EmptyBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes([]);
        Span<char> empty = [];
        bool ok = v.TryFormat(empty, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_CharFallback_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("VeryLongEnumName", 999999);
        Span<byte> tiny = stackalloc byte[4];
        bool ok = v.TryFormat(tiny, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task String_ToString_ReturnsRawString_NotFormatted()
    {
        SettingValue v = SettingValue.String("plain");
        await Assert.That(v.ToString()).IsEqualTo("plain");
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("plain");
    }

    [Test]
    public async Task EnumLabel_PartialBufferFailures_ReturnFalse()
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
    public async Task BytesBothNullReferences_AreEqual()
    {
        SettingValue left = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1]), "_ReferenceValue", null);
        SettingValue right = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([2]), "_ReferenceValue", null);
        await Assert.That(left == right).IsTrue();
    }

    [Test]
    public async Task BytesOneNullReference_AreNotEqual()
    {
        SettingValue withNull = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1, 2]), "_ReferenceValue", null);
        SettingValue withData = SettingValue.Bytes([1, 2]);
        await Assert.That(withNull == withData).IsFalse();
        await Assert.That(withNull.GetHashCode()).IsNotEqualTo(withData.GetHashCode());
    }

    [Test]
    public async Task Equals_UnknownType_ReturnsFalse()
    {
        SettingValue unknown = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        await Assert.That(unknown.Equals(SettingValue.Bool(true))).IsFalse();
        await Assert.That(unknown.GetHashCode()).IsEqualTo(HashCode.Combine((SettingType)99));
    }

    [Test]
    public async Task Equals_UnknownTypeSameType_UsesDefaultArm()
    {
        SettingValue left = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        SettingValue right = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)99);
        await Assert.That(left.Equals(right)).IsFalse();
    }

    [Test]
    public async Task ToString_LongEnumName_UsesHeapFallback()
    {
        string longName = new('Z', 300);
        SettingValue v = SettingValue.Enum(longName, 1);
        string text = v.ToString();
        await Assert.That(text.StartsWith(longName, StringComparison.Ordinal)).IsTrue();
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture).StartsWith(longName, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ToString_VeryLongEnumName_FormatsFully()
    {
        string longName = new('Z', 5000);
        SettingValue v = SettingValue.Enum(longName, 1);
        string expected = longName + " (1)";
        await Assert.That(v.ToString()).IsEqualTo(expected);
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo(expected);
    }

    [Test]
    public async Task GetHashCode_EqualValues_ShareHashCode_AllNumericArms()
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
    public async Task Equality_DifferentU64AndI64_AreNotEqual()
    {
        await Assert.That(SettingValue.U64(1) != SettingValue.U64(2)).IsTrue();
        await Assert.That(SettingValue.I64(1) != SettingValue.I64(2)).IsTrue();
        await Assert.That(SettingValue.Enum("A", 0) != SettingValue.Enum("B", 0)).IsTrue();
        await Assert.That(SettingValue.Enum("A", 0) != SettingValue.Enum("A", 1)).IsTrue();
    }

    [Test]
    public async Task TryFormat_StringNullReference_ReturnsTrueWithZeroWritten()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.String("ignored"), "_ReferenceValue", null);
        Span<char> buffer = stackalloc char[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_EnumNullReference_ReturnsTrueWithZeroWritten()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Enum("High", 1), "_ReferenceValue", null);
        Span<char> buffer = stackalloc char[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_UnknownType_ReturnsTrueWithZeroWritten()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)99);
        Span<char> buffer = stackalloc char[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_BytesLabel_DigitBufferTooSmall_ReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes(new byte[5000]);
        Span<char> buffer = stackalloc char[1];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_BytesLabel_SuffixBufferTooSmall_ReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes(new byte[12]);
        Span<char> buffer = stackalloc char[4];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_EnumLabel_DigitBufferTooSmall_ReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("X", ulong.MaxValue);
        Span<char> buffer = stackalloc char[5];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_NonStringCharFallbackInsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Enum(new string('E', 240), 1);
        Span<byte> buffer = stackalloc byte[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task Equals_MismatchedUnknownType_ReturnsFalse()
    {
        SettingValue left = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        SettingValue right = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(false), "_Type", (SettingType)98);
        await Assert.That(left.Equals(right)).IsFalse();
    }

    [Test]
    public async Task TryFormat_BytesNullReference_UsesZeroByteLabel()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField<object?>(
            SettingValue.Bytes([1]), "_ReferenceValue", null);
        char[] buffer = new char[16];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(new string(buffer, 0, written)).IsEqualTo("[0 bytes]");
    }

    [Test]
    public async Task ToString_WhenFormattedValueEmpty_ReturnsEmptyString()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.String("x"), "_Type", (SettingType)99);
        await Assert.That(v.ToString()).IsEqualTo("");
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("");
    }

    [Test]
    public async Task TryFormat_Utf8_LongEnumName_HeapFallbackSucceeds()
    {
        string longName = new('Z', 300);
        SettingValue v = SettingValue.Enum(longName, 1);
        byte[] buffer = new byte[1024];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsGreaterThan(300);
    }

    [Test]
    public async Task TryFormat_Utf8_WhenCharTryFormatFails_ReturnsFalse()
    {
        SettingValue v = SettingValue.Enum(new string('Z', 5000), 1);
        Span<byte> buffer = stackalloc byte[8];
        bool ok = v.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_NonAsciiEnum_DestinationEqualToCharCount_ReturnsFalse()
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
    public async Task TryFormat_Utf8_NonAsciiString_DestinationEqualToCharCount_ReturnsFalse()
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
    public async Task TryFormatEnumLabel_NameBufferTooSmall_ReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("TooLongName", 1);
        char[] buffer = new char[4];
        bool ok = v.TryFormat(buffer.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === IStringSize / ZeroAlloc convenience ===

    [Test]
    public async Task IStringSize_Bool_True_ReturnsFour()
    {
        bool ok = SettingValue.Bool(true).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(4);
    }

    [Test]
    public async Task IStringSize_Bool_False_ReturnsFive()
    {
        bool ok = SettingValue.Bool(false).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(5);
    }

    [Test]
    public async Task IStringSize_String_ReturnsExactLength()
    {
        bool ok = SettingValue.String("hello").TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(5);
    }

    [Test]
    public async Task IStringSize_Bytes_ReturnsExactLabelLength()
    {
        bool ok = SettingValue.Bytes([1, 2, 3]).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo("[3 bytes]".Length);
    }

    [Test]
    public async Task IStringSize_Enum_ReturnsExactLabelLength()
    {
        bool ok = SettingValue.Enum("High", 2).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo("High (2)".Length);
    }

    [Test]
    public async Task IStringSize_U64_ReturnsExactDigitCount()
    {
        bool ok = SettingValue.U64(ulong.MaxValue).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(20);
        await Assert.That(size).IsEqualTo(SettingValue.U64(ulong.MaxValue).ToString().Length);
    }

    [Test]
    public async Task IStringSize_I64_MinValue_ReturnsTwenty()
    {
        bool ok = SettingValue.I64(long.MinValue).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(20);
        await Assert.That(size).IsEqualTo(SettingValue.I64(long.MinValue).ToString().Length);
    }

    [Test]
    public async Task IStringSize_F64_ReturnsFalse()
    {
        bool ok = SettingValue.F64(3.14).TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task IStringSize_U64_WithFormat_ReturnsFalse()
    {
        bool ok = SettingValue.U64(42).TryGetStringSize("X", CultureInfo.InvariantCulture, out int size);
        await Assert.That(ok).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task IStringSize_I64_WithFormat_ReturnsFalse()
    {
        bool ok = SettingValue.I64(-7).TryGetStringSize("D", CultureInfo.InvariantCulture, out int size);
        await Assert.That(ok).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task IStringSize_NullReferences_ReturnZeroOrEmptyLabel()
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
    public async Task IStringSize_UnknownType_ReturnsZero()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        bool ok = v.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task FormatInto_WritesExpectedText()
    {
        char[] buffer = new char[16];
        int written = SettingValue.U64(42).FormatInto(buffer);
        await Assert.That(written).IsEqualTo(2);
        await Assert.That(new string(buffer, 0, written)).IsEqualTo("42");
    }

    [Test]
    public async Task FormatInto_InsufficientBuffer_ReturnsZero()
    {
        char[] tiny = new char[1];
        int written = SettingValue.Bool(true).FormatInto(tiny);
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task Format_String_ReturnsStoredInstance()
    {
        string stored = "plain";
        SettingValue v = SettingValue.String(stored);
        await Assert.That(ReferenceEquals(v.Format(), stored)).IsTrue();
        string fromTemp;
        using (TempString temp = v.FormatTemp())
        {
            fromTemp = temp.AsSpan().ToString();
        }
        await Assert.That(fromTemp).IsEqualTo(stored);
    }

    [Test]
    public async Task FormatTemp_EmptyString_ReturnsEmpty()
    {
        int length;
        using (TempString temp = SettingValue.String("").FormatTemp())
        {
            length = temp.AsSpan().Length;
        }
        await Assert.That(length).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_NullReferences_WriteEmptyOrZeroBytesLabel()
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
    public async Task FormatTemp_MatchesToString_AllTypes()
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
            string fromTemp;
            using (TempString temp = v.FormatTemp())
            {
                fromTemp = temp.AsSpan().ToString();
            }
            await Assert.That(fromTemp).IsEqualTo(v.ToString());
        }
    }

    [Test]
    public async Task FormatTemp_UnknownType_ReturnsEmpty()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        int length;
        using (TempString temp = v.FormatTemp())
        {
            length = temp.AsSpan().Length;
        }
        await Assert.That(length).IsEqualTo(0);
        await Assert.That(v.Format()).IsEqualTo("");
    }

    [Test]
    public async Task ToString_NullOrEmptyFormat_UsesDefaultFormat()
    {
        SettingValue v = SettingValue.U64(42);
        await Assert.That(v.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("42");
        await Assert.That(v.ToString("", CultureInfo.InvariantCulture)).IsEqualTo("42");
        await Assert.That(v.ToString("G", null)).IsEqualTo("42");
    }

    [Test]
    public async Task ToString_UnknownType_WithFormat_ReturnsEmpty()
    {
        SettingValue v = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("");
    }

    [Test]
    public async Task ToString_F64_CustomFormat_UsesHeapWhenLongerThanStack()
    {
        SettingValue v = SettingValue.F64(1.0);
        string text = v.ToString("F300", CultureInfo.InvariantCulture);
        await Assert.That(text.StartsWith("1.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Length).IsEqualTo(302);
    }

    [Test]
    public async Task ToString_Integer_CustomFormat_UsesBclWhenLongerThanStack()
    {
        string u64Text = SettingValue.U64(1).ToString("D300", CultureInfo.InvariantCulture);
        await Assert.That(u64Text.Length).IsEqualTo(300);
        await Assert.That(u64Text.EndsWith('1')).IsTrue();

        string i64Text = SettingValue.I64(-1).ToString("D300", CultureInfo.InvariantCulture);
        await Assert.That(i64Text.Length).IsEqualTo(301);
        await Assert.That(i64Text.StartsWith('-')).IsTrue();
    }

    [Test]
    public async Task ToString_Enum_WithFormat_UsesKnownSizePath()
    {
        SettingValue v = SettingValue.Enum("High", 2);
        await Assert.That(v.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo("High (2)");
    }

    [Test]
    public async Task TryFormat_Utf8_NonAsciiEnum_SucceedsWhenDestIsLargeEnough()
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
    public async Task TryFormat_Utf8_Bool_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Bool(true);
        byte[] tiny = new byte[3];
        bool ok = v.TryFormat(tiny.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_Bytes_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Bytes([]);
        byte[] tiny = new byte[1];
        bool ok = v.TryFormat(tiny.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_AsciiEnum_InsufficientBuffer_ReturnsFalse()
    {
        SettingValue v = SettingValue.Enum("High", 2);
        byte[] tiny = new byte[3];
        bool ok = v.TryFormat(tiny.AsSpan(), out int written, default, CultureInfo.InvariantCulture);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Utf8_NonAsciiString_SucceedsWhenDestIsLargeEnough()
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
    public async Task IStringSize_I64_Negative_IncludesSign()
    {
        bool negativeOk = SettingValue.I64(-42).TryGetStringSize(default, null, out int negativeSize);
        await Assert.That(negativeOk).IsTrue();
        await Assert.That(negativeSize).IsEqualTo(3);

        bool zeroOk = SettingValue.I64(0).TryGetStringSize(default, null, out int zeroSize);
        await Assert.That(zeroOk).IsTrue();
        await Assert.That(zeroSize).IsEqualTo(1);
    }

    [Test]
    public async Task TryFormat_Utf8_NumericAndBool_Roundtrip()
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
