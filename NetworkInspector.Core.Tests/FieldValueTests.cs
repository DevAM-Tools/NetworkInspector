// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for FieldValueData (tagged union) and FieldValue (with optional custom representation).
/// </summary>
internal sealed class FieldValueTests
{
    // === FieldValueData factory and roundtrip ===

    [Test]
    public async Task FieldValueData_None_HasNoneType()
    {
        FieldValueData none = FieldValueData.None;
        await Assert.That(none.Type).IsEqualTo(FieldType.None);
    }

    [Test]
    public async Task FieldValueData_Bool_Roundtrip()
    {
        FieldValueData val = FieldValueData.NewBool(true);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bool);
        val.TryGetAsBool(out bool boolVal);
        await Assert.That(boolVal).IsTrue();

        FieldValueData valFalse = FieldValueData.NewBool(false);
        valFalse.TryGetAsBool(out bool boolVal2);
        await Assert.That(boolVal2).IsFalse();
    }

    [Test]
    public async Task FieldValueData_I64_Roundtrip()
    {
        FieldValueData val = FieldValueData.NewI64(-42);
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-42L);
    }

    [Test]
    public async Task FieldValueData_U64_Roundtrip()
    {
        FieldValueData val = FieldValueData.NewU64(12345678UL);
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(12345678UL);
    }

    [Test]
    public async Task FieldValueData_F64_Roundtrip()
    {
        FieldValueData val = FieldValueData.NewF64(3.14);
        await Assert.That(val.Type).IsEqualTo(FieldType.F64);
        val.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(3.14);
    }

    [Test]
    public async Task FieldValueData_String_Roundtrip()
    {
        FieldValueData val = FieldValueData.NewString("hello");
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        val.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("hello");
    }

    [Test]
    public async Task FieldValueData_Bytes_Roundtrip()
    {
        byte[] data = [0x01, 0x02, 0x03];
        FieldValueData val = FieldValueData.NewBytes(data);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bytes);
        val.TryGetAsBytes(out ReadOnlyMemory<byte> result);
        await Assert.That(result.Length).IsEqualTo(3);
        await Assert.That(result.Span[0]).IsEqualTo((byte)0x01);
        await Assert.That(result.Span[1]).IsEqualTo((byte)0x02);
        await Assert.That(result.Span[2]).IsEqualTo((byte)0x03);
    }

    [Test]
    public async Task FieldValueData_MacAddress_Roundtrip()
    {
        MacAddress mac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        FieldValueData val = FieldValueData.NewMacAddress(mac);
        await Assert.That(val.Type).IsEqualTo(FieldType.MacAddress);
        val.TryGetAsMacAddress(out MacAddress macVal);
        await Assert.That(macVal.RawValue).IsEqualTo(mac.RawValue);
    }

    [Test]
    public async Task FieldValueData_IPv4_Roundtrip()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        FieldValueData val = FieldValueData.NewIPv4(ip);
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv4Address);
        val.TryGetAsIPv4(out IPv4Address ipv4Val);
        await Assert.That(ipv4Val.RawValue).IsEqualTo(ip.RawValue);
    }

    [Test]
    public async Task FieldValueData_Timestamp_Roundtrip()
    {
        Timestamp ts = Timestamp.FromSecs(1000);
        FieldValueData val = FieldValueData.NewTimestamp(ts);
        await Assert.That(val.Type).IsEqualTo(FieldType.Timestamp);
        val.TryGetAsTimestamp(out Timestamp tsVal);
        await Assert.That(tsVal.AsNanos).IsEqualTo(ts.AsNanos);
    }

    // === FieldValueData Equality ===

    [Test]
    public async Task FieldValueData_Equality_SameValues()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewU64(100);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task FieldValueData_Inequality_DifferentValues()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewU64(200);
        await Assert.That(a.Equals(b)).IsFalse();
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task FieldValueData_Inequality_DifferentTypes()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewI64(100);
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task FieldValueData_Equality_Strings()
    {
        FieldValueData a = FieldValueData.NewString("test");
        FieldValueData b = FieldValueData.NewString("test");
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task FieldValueData_Inequality_Strings()
    {
        FieldValueData a = FieldValueData.NewString("hello");
        FieldValueData b = FieldValueData.NewString("world");
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task FieldValueData_Equality_Bytes()
    {
        byte[] d1 = [1, 2, 3];
        byte[] d2 = [1, 2, 3];
        FieldValueData a = FieldValueData.NewBytes(d1);
        FieldValueData b = FieldValueData.NewBytes(d2);
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task FieldValueData_Inequality_Bytes()
    {
        byte[] d1 = [1, 2, 3];
        byte[] d2 = [4, 5, 6];
        FieldValueData a = FieldValueData.NewBytes(d1);
        FieldValueData b = FieldValueData.NewBytes(d2);
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task FieldValueData_None_Equality()
    {
        FieldValueData a = FieldValueData.None;
        FieldValueData b = FieldValueData.None;
        await Assert.That(a.Equals(b)).IsTrue();
    }

    // === FieldValueData CompareTo ===

    [Test]
    public async Task FieldValueData_CompareTo_U64()
    {
        FieldValueData a = FieldValueData.NewU64(10);
        FieldValueData b = FieldValueData.NewU64(20);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
        await Assert.That(a.CompareTo(a)).IsEqualTo(0);
    }

    [Test]
    public async Task FieldValueData_CompareTo_I64_Negative()
    {
        FieldValueData a = FieldValueData.NewI64(-10);
        FieldValueData b = FieldValueData.NewI64(10);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    // === FieldValue ===

    [Test]
    public async Task FieldValue_None_HasNoneType()
    {
        FieldValue none = FieldValue.None;
        await Assert.That(none.Type).IsEqualTo(FieldType.None);
    }

    [Test]
    public async Task FieldValue_NewBool()
    {
        FieldValue val = FieldValue.NewBool(true);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bool);
        val.Data.TryGetAsBool(out bool boolVal);
        await Assert.That(boolVal).IsTrue();
    }

    [Test]
    public async Task FieldValue_NewU64()
    {
        FieldValue val = FieldValue.NewU64(42);
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.Data.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(42UL);
    }

    [Test]
    public async Task FieldValue_NewI64()
    {
        FieldValue val = FieldValue.NewI64(-100);
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.Data.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-100L);
    }

    [Test]
    public async Task FieldValue_NewF64()
    {
        FieldValue val = FieldValue.NewF64(2.718);
        await Assert.That(val.Type).IsEqualTo(FieldType.F64);
        val.Data.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(2.718);
    }

    [Test]
    public async Task FieldValue_NewString()
    {
        FieldValue val = FieldValue.NewString("test");
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        val.Data.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("test");
    }

    [Test]
    public async Task FieldValue_NewBytes()
    {
        byte[] data = [0xDE, 0xAD];
        FieldValue val = FieldValue.NewBytes(data);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bytes);
        val.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
        await Assert.That(bytesVal.Length).IsEqualTo(2);
    }

    [Test]
    public async Task FieldValue_NewMacAddress()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        FieldValue val = FieldValue.NewMacAddress(mac);
        await Assert.That(val.Type).IsEqualTo(FieldType.MacAddress);
        val.Data.TryGetAsMacAddress(out MacAddress macVal);
        await Assert.That(macVal.Format()).IsEqualTo("00:11:22:33:44:55");
    }

    [Test]
    public async Task FieldValue_NewIPv4()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        FieldValue val = FieldValue.NewIPv4(ip);
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv4Address);
        val.Data.TryGetAsIPv4(out IPv4Address ipv4Val);
        await Assert.That(ipv4Val.Format()).IsEqualTo("10.0.0.1");
    }

    [Test]
    public async Task FieldValue_NewTimestamp()
    {
        Timestamp ts = Timestamp.FromMillis(5000);
        FieldValue val = FieldValue.NewTimestamp(ts);
        await Assert.That(val.Type).IsEqualTo(FieldType.Timestamp);
        val.Data.TryGetAsTimestamp(out Timestamp tsVal);
        await Assert.That(tsVal.AsMillis).IsEqualTo(5000L);
    }

    [Test]
    public async Task FieldValue_WithCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        await Assert.That(val.CustomRepresentation.IsNull).IsFalse();
        await Assert.That(val.CustomRepresentation.AsString).IsEqualTo("HTTP (80)");
        await Assert.That(val.ToString()).IsEqualTo("HTTP (80)");
    }

    [Test]
    public async Task FieldValue_WithoutCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80);
        await Assert.That(val.CustomRepresentation.IsNull).IsTrue();
    }

    [Test]
    public async Task FieldValue_Equality_IgnoresCustomRepresentation()
    {
        FieldValue a = FieldValue.NewU64(42, "answer");
        FieldValue b = FieldValue.NewU64(42);
        // Equality ignores custom representation per the implementation
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task FieldValue_Inequality_DifferentData()
    {
        FieldValue a = FieldValue.NewU64(1);
        FieldValue b = FieldValue.NewU64(2);
        await Assert.That(a != b).IsTrue();
    }

    // === FieldValueData.ToString() ===

    [Test]
    public async Task FieldValueData_ToString_None() => await Assert.That(FieldValueData.None.ToString()).IsEqualTo(string.Empty);

    [Test]
    public async Task FieldValueData_ToString_Bool_True() => await Assert.That(FieldValueData.NewBool(true).ToString()).IsEqualTo("True");

    [Test]
    public async Task FieldValueData_ToString_Bool_False() => await Assert.That(FieldValueData.NewBool(false).ToString()).IsEqualTo("False");

    [Test]
    public async Task FieldValueData_ToString_I64() => await Assert.That(FieldValueData.NewI64(-42).ToString()).IsEqualTo("-42");

    [Test]
    public async Task FieldValueData_ToString_U64() => await Assert.That(FieldValueData.NewU64(12345).ToString()).IsEqualTo("12345");

    [Test]
    public async Task FieldValueData_ToString_F64() =>
        // InvariantCulture uses '.' as decimal separator
        await Assert.That(FieldValueData.NewF64(3.14).ToString()).IsEqualTo("3.14");

    [Test]
    public async Task FieldValueData_ToString_String() => await Assert.That(FieldValueData.NewString("hello").ToString()).IsEqualTo("hello");

    [Test]
    public async Task FieldValueData_ToString_Bytes_Empty()
        => await Assert.That(FieldValueData.NewBytes(ReadOnlyMemory<byte>.Empty).ToString()).IsEqualTo(string.Empty);

    [Test]
    public async Task FieldValueData_ToString_Bytes_SingleByte() => await Assert.That(FieldValueData.NewBytes(new byte[] { 0xAB }).ToString()).IsEqualTo("AB");

    [Test]
    public async Task FieldValueData_ToString_Bytes_MultipleBytes()
    {
        await Assert.That(FieldValueData.NewBytes(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }).ToString())
            .IsEqualTo("DE AD BE EF");
    }

    [Test]
    public async Task FieldValueData_ToString_MacAddress()
    {
        MacAddress mac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        await Assert.That(FieldValueData.NewMacAddress(mac).ToString()).IsEqualTo("AA:BB:CC:DD:EE:FF");
    }

    [Test]
    public async Task FieldValueData_ToString_IPv4()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        await Assert.That(FieldValueData.NewIPv4(ip).ToString()).IsEqualTo("192.168.1.1");
    }

    // === FieldValueData.TryFormat(Span<char>) ===

    [Test]
    public async Task FieldValueData_TryFormat_Char_None()
    {
        char[] buf = new char[16];
        bool result = FieldValueData.None.TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FieldValueData_TryFormat_Char_Bool()
    {
        char[] buf = new char[16];
        bool result = FieldValueData.NewBool(true).TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("True");
    }

    [Test]
    public async Task FieldValueData_TryFormat_Char_Bool_InsufficientSpace()
    {
        char[] buf = new char[3]; // "True" needs 4
        bool result = FieldValueData.NewBool(true).TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FieldValueData_TryFormat_Char_U64()
    {
        char[] buf = new char[32];
        bool result = FieldValueData.NewU64(65535).TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("65535");
    }

    [Test]
    public async Task FieldValueData_TryFormat_Char_Bytes()
    {
        char[] buf = new char[32];
        bool result = FieldValueData.NewBytes(new byte[] { 0x0A, 0xFF }).TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("0A FF");
    }

    [Test]
    public async Task FieldValueData_TryFormat_Char_Bytes_InsufficientSpace()
    {
        char[] buf = new char[4]; // "0A FF" needs 5
        bool result = FieldValueData.NewBytes(new byte[] { 0x0A, 0xFF }).TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FieldValueData_TryFormat_Char_String()
    {
        char[] buf = new char[32];
        bool result = FieldValueData.NewString("test").TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("test");
    }

    // === FieldValueData.TryFormat(Span<byte>) UTF-8 ===

    [Test]
    public async Task FieldValueData_TryFormat_Utf8_Bool()
    {
        byte[] buf = new byte[16];
        bool result = FieldValueData.NewBool(false).TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("False");
    }

    [Test]
    public async Task FieldValueData_TryFormat_Utf8_U64()
    {
        byte[] buf = new byte[32];
        bool result = FieldValueData.NewU64(999).TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("999");
    }

    [Test]
    public async Task FieldValueData_TryFormat_Utf8_Bytes()
    {
        byte[] buf = new byte[32];
        bool result = FieldValueData.NewBytes(new byte[] { 0xCA, 0xFE }).TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("CA FE");
    }

    [Test]
    public async Task FieldValueData_TryFormat_Utf8_String()
    {
        byte[] buf = new byte[32];
        bool result = FieldValueData.NewString("hello").TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("hello");
    }

    // === FieldValue.ToString() ===

    [Test]
    public async Task FieldValue_ToString_WithCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        await Assert.That(val.ToString()).IsEqualTo("HTTP (80)");
    }

    [Test]
    public async Task FieldValue_ToString_WithoutCustomRepresentation_FallsBackToData()
    {
        FieldValue val = FieldValue.NewU64(80);
        await Assert.That(val.ToString()).IsEqualTo("80");
    }

    [Test]
    public async Task FieldValue_ToString_None() => await Assert.That(FieldValue.None.ToString()).IsEqualTo(string.Empty);

    // === FieldValue.TryFormat ===

    [Test]
    public async Task FieldValue_TryFormat_Char_UsesCustomRep()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP");
        char[] buf = new char[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("HTTP");
    }

    [Test]
    public async Task FieldValue_TryFormat_Char_FallsBackToData()
    {
        FieldValue val = FieldValue.NewI64(-7);
        char[] buf = new char[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("-7");
    }

    [Test]
    public async Task FieldValue_TryFormat_Utf8_UsesCustomRep()
    {
        FieldValue val = FieldValue.NewU64(443, "HTTPS");
        byte[] buf = new byte[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("HTTPS");
    }

    [Test]
    public async Task FieldValue_TryFormat_Utf8_FallsBackToData()
    {
        FieldValue val = FieldValue.NewU64(443);
        byte[] buf = new byte[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("443");
    }

    // === FieldValue.DefaultText ===

    [Test]
    public async Task FieldValue_DataText_IgnoresCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        char[] buf = new char[32];
        bool result = val.DataText.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("80");
    }

    [Test]
    public async Task FieldValue_DataText_TryFormat()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        char[] buf = new char[32];
        bool result = val.DataText.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("80");
    }

    // === Implicit operators ===

    [Test]
    public async Task FieldValue_Implicit_Bool()
    {
        FieldValue val = true;
        await Assert.That(val.Type).IsEqualTo(FieldType.Bool);
        val.Data.TryGetAsBool(out bool boolVal);
        await Assert.That(boolVal).IsTrue();
    }

    [Test]
    public async Task FieldValue_Implicit_Long()
    {
        FieldValue val = -100L;
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.Data.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-100L);
    }

    [Test]
    public async Task FieldValue_Implicit_Ulong()
    {
        FieldValue val = 42UL;
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.Data.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(42UL);
    }

    [Test]
    public async Task FieldValue_Implicit_Int()
    {
        FieldValue val = -5;
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.Data.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-5L);
    }

    [Test]
    public async Task FieldValue_Implicit_Uint()
    {
        FieldValue val = 100U;
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.Data.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(100UL);
    }

    [Test]
    public async Task FieldValue_Implicit_Double()
    {
        FieldValue val = 2.5;
        await Assert.That(val.Type).IsEqualTo(FieldType.F64);
        val.Data.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(2.5);
    }

    [Test]
    public async Task FieldValue_Implicit_String()
    {
        FieldValue val = "hello";
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        val.Data.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("hello");
    }

    [Test]
    public async Task FieldValue_Implicit_String_Null()
    {
        FieldValue val = (string?)null;
        await Assert.That(val.Type).IsEqualTo(FieldType.None);
    }

    [Test]
    public async Task FieldValue_Implicit_ByteArray()
    {
        FieldValue val = new byte[] { 0xAA, 0xBB };
        await Assert.That(val.Type).IsEqualTo(FieldType.Bytes);
        val.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
        await Assert.That(bytesVal.Length).IsEqualTo(2);
    }

    [Test]
    public async Task FieldValue_Implicit_ByteArray_Null()
    {
        FieldValue val = (byte[]?)null;
        await Assert.That(val.Type).IsEqualTo(FieldType.None);
    }

    [Test]
    public async Task FieldValue_Implicit_MacAddress()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        FieldValue val = mac;
        await Assert.That(val.Type).IsEqualTo(FieldType.MacAddress);
    }

    [Test]
    public async Task FieldValue_Implicit_IPv4()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        FieldValue val = ip;
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv4Address);
    }

    [Test]
    public async Task FieldValue_Implicit_Timestamp()
    {
        Timestamp ts = Timestamp.FromSecs(1);
        FieldValue val = ts;
        await Assert.That(val.Type).IsEqualTo(FieldType.Timestamp);
    }

    // === Cross-type CompareTo ===

    [Test]
    public async Task CompareTo_I64_Vs_U64_Positive()
    {
        // I64(10) vs U64(20) → negative
        FieldValueData a = FieldValueData.NewI64(10);
        FieldValueData b = FieldValueData.NewU64(20);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
    }

    [Test]
    public async Task CompareTo_I64_Negative_Vs_U64()
    {
        // I64(-1) vs U64(0) → negative (signed < 0 always less than unsigned)
        FieldValueData a = FieldValueData.NewI64(-1);
        FieldValueData b = FieldValueData.NewU64(0);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
    }

    [Test]
    public async Task CompareTo_I64_Vs_F64()
    {
        FieldValueData a = FieldValueData.NewI64(10);
        FieldValueData b = FieldValueData.NewF64(10.5);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    [Test]
    public async Task CompareTo_U64_Vs_F64()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewF64(99.5);
        await Assert.That(a.CompareTo(b)).IsGreaterThan(0);
    }

    [Test]
    public async Task CompareTo_IPv4_Vs_IPv6_AlwaysLess()
    {
        FieldValueData a = FieldValueData.NewIPv4(IPv4Address.FromBytes([255, 255, 255, 255]));
        FieldValueData b = FieldValueData.NewIPv6(IPv6Address.FromBytes([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]));
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
    }

    [Test]
    public async Task CompareTo_FieldValue_IgnoresCustomRepresentation()
    {
        FieldValue a = FieldValue.NewU64(10, "ten");
        FieldValue b = FieldValue.NewU64(20, "twenty");
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    [Test]
    public async Task CompareTo_FieldValue_CrossType_I64_U64()
    {
        FieldValue a = FieldValue.NewI64(-5);
        FieldValue b = FieldValue.NewU64(10);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    // === IStringSize ===

    [Test]
    public async Task FieldValueData_IStringSize_None_ReturnsZero()
    {
        FieldValueData val = FieldValueData.None;
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task FieldValueData_IStringSize_Bool_True_ReturnsFour()
    {
        FieldValueData val = FieldValueData.NewBool(true);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(4); // "True"
    }

    [Test]
    public async Task FieldValueData_IStringSize_Bool_False_ReturnsFive()
    {
        FieldValueData val = FieldValueData.NewBool(false);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(5); // "False"
    }

    [Test]
    public async Task FieldValueData_IStringSize_I64_ReturnsBoundedSize()
    {
        FieldValueData val = FieldValueData.NewI64(long.MinValue);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsGreaterThanOrEqualTo(val.ToString().Length);
    }

    [Test]
    public async Task FieldValueData_IStringSize_U64_ReturnsBoundedSize()
    {
        FieldValueData val = FieldValueData.NewU64(ulong.MaxValue);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsGreaterThanOrEqualTo(val.ToString().Length);
    }

    [Test]
    public async Task FieldValueData_IStringSize_F64_ReturnsFalse()
    {
        FieldValueData val = FieldValueData.NewF64(3.14);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    [Test]
    public async Task FieldValueData_IStringSize_String_ReturnsExactLength()
    {
        string text = "Hello, World!";
        FieldValueData val = FieldValueData.NewString(text);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(text.Length);
    }

    [Test]
    public async Task FieldValueData_IStringSize_Bytes_ReturnsExactLength()
    {
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF];
        FieldValueData val = FieldValueData.NewBytes(bytes);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        // "DE AD BE EF" = 4*3-1 = 11
        await Assert.That(size).IsEqualTo(11);
    }

    [Test]
    public async Task FieldValueData_IStringSize_MacAddress_ReturnsExactLength()
    {
        MacAddress mac = new(0x112233445566UL);
        FieldValueData val = FieldValueData.NewMacAddress(mac);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(mac.ToString().Length);
    }

    [Test]
    public async Task FieldValueData_IStringSize_IPv4_ReturnsExactLength()
    {
        IPv4Address ip = new(0xC0A80101u); // 192.168.1.1
        FieldValueData val = FieldValueData.NewIPv4(ip);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsGreaterThanOrEqualTo(ip.ToString().Length);
    }

    [Test]
    public async Task FieldValue_IStringSize_WithCustomRepresentation_ReturnsCustomLength()
    {
        string custom = "Custom Text";
        FieldValue val = FieldValue.NewU64(42, custom);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(custom.Length);
    }

    [Test]
    public async Task FieldValue_IStringSize_WithoutCustomRepresentation_DelegatesToData()
    {
        FieldValue val = FieldValue.NewBool(true);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(4); // "True"
    }

    // === ToTempString ===

    [Test]
    public async Task FieldValueData_ToTempString_None_ReturnsEmpty()
    {
        string result;
        using (TempString temp = FieldValueData.None.ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FieldValueData_ToTempString_Bool_MatchesFormat()
    {
        string trueResult;
        string falseResult;
        using (TempString temp = FieldValueData.NewBool(true).ToTempString())
        {
            trueResult = temp.AsSpan().ToString();
        }
        using (TempString temp = FieldValueData.NewBool(false).ToTempString())
        {
            falseResult = temp.AsSpan().ToString();
        }
        await Assert.That(trueResult).IsEqualTo("True");
        await Assert.That(falseResult).IsEqualTo("False");
    }

    [Test]
    public async Task FieldValueData_ToTempString_I64_MatchesFormat()
    {
        long value = -1234567890L;
        string result;
        using (TempString temp = FieldValueData.NewI64(value).ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(value.ToString());
    }

    [Test]
    public async Task FieldValueData_ToTempString_U64_MatchesFormat()
    {
        ulong value = 9876543210UL;
        string result;
        using (TempString temp = FieldValueData.NewU64(value).ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(value.ToString());
    }

    [Test]
    public async Task FieldValueData_ToTempString_String_MatchesOriginal()
    {
        string text = "Hello, ZeroAlloc!";
        string result;
        using (TempString temp = FieldValueData.NewString(text).ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(text);
    }

    [Test]
    public async Task FieldValueData_ToTempString_Bytes_MatchesHexFormat()
    {
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF];
        string result;
        using (TempString temp = FieldValueData.NewBytes(bytes).ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo("DE AD BE EF");
    }

    [Test]
    public async Task FieldValueData_ToTempString_MacAddress_MatchesFormat()
    {
        MacAddress mac = new(0x112233445566UL);
        string result;
        using (TempString temp = FieldValueData.NewMacAddress(mac).ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(mac.ToString());
    }

    [Test]
    public async Task FieldValueData_ToTempString_IPv4_MatchesFormat()
    {
        IPv4Address ip = new(0xC0A80101u); // 192.168.1.1
        string result;
        using (TempString temp = FieldValueData.NewIPv4(ip).ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(ip.ToString());
    }

    [Test]
    public async Task FieldValue_ToTempString_WithCustomRepresentation_UsesCustom()
    {
        string custom = "Custom Display";
        FieldValue val = FieldValue.NewU64(42, custom);
        string result;
        using (TempString temp = val.ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo(custom);
    }

    [Test]
    public async Task FieldValue_ToTempString_WithoutCustomRepresentation_FormatsData()
    {
        FieldValue val = FieldValue.NewI64(99L);
        string result;
        using (TempString temp = val.ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo("99");
    }

    [Test]
    public async Task FieldValue_DefaultText_ToTempString_IgnoresCustomRepresentation()
    {
        // DefaultText should always format raw data, even when custom repr is set
        FieldValue val = FieldValue.NewI64(42L, "custom");
        string result;
        using (TempString temp = val.DataText.ToTempString())
        {
            result = temp.AsSpan().ToString();
        }
        await Assert.That(result).IsEqualTo("42");
    }

    [Test]
    public async Task FieldValueData_ToTempString_ResultMatchesToString()
    {
        // ToTempString and ToString should produce identical output for all types
        FieldValueData[] values =
        [
            FieldValueData.None,
            FieldValueData.NewBool(true),
            FieldValueData.NewBool(false),
            FieldValueData.NewI64(-42L),
            FieldValueData.NewU64(42UL),
            FieldValueData.NewString("test"),
            FieldValueData.NewBytes(new byte[] { 0xAB, 0xCD }),
            FieldValueData.NewMacAddress(new MacAddress(0x001122334455UL)),
            FieldValueData.NewIPv4(new IPv4Address(0x0A000001u)),
        ];

        foreach (FieldValueData val in values)
        {
            string fromTempString;
            using (TempString temp = val.ToTempString())
            {
                fromTempString = temp.AsSpan().ToString();
            }
            string fromToString = val.ToString();
            await Assert.That(fromTempString).IsEqualTo(fromToString);
        }
    }
}
