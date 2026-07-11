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
