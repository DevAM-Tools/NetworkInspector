// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for FieldValueData (tagged union) and FieldValue (with optional custom representation).
/// </summary>
internal sealed class FieldValueTests
{
    // === FieldValueData factory and roundtrip ===

    /// <summary>The none value of the tagged union reports the type <c>None</c>.</summary>
    [Test]
    public async Task FieldValueDataNoneHasNoneType()
    {
        FieldValueData none = FieldValueData.None;
        await Assert.That(none.Type).IsEqualTo(FieldType.None);
    }

    /// <summary>A bool stored in the tagged union reads back unchanged for true and false and reports the type <c>Bool</c>.</summary>
    [Test]
    public async Task FieldValueDataBoolRoundtrip()
    {
        FieldValueData val = FieldValueData.NewBool(true);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bool);
        val.TryGetAsBool(out bool boolVal);
        await Assert.That(boolVal).IsTrue();

        FieldValueData valFalse = FieldValueData.NewBool(false);
        valFalse.TryGetAsBool(out bool boolVal2);
        await Assert.That(boolVal2).IsFalse();
    }

    /// <summary>A negative 64-bit integer reads back unchanged and the union reports the type <c>I64</c>.</summary>
    [Test]
    public async Task FieldValueDataI64Roundtrip()
    {
        FieldValueData val = FieldValueData.NewI64(-42);
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-42L);
    }

    /// <summary>An unsigned 64-bit integer reads back unchanged and the union reports the type <c>U64</c>.</summary>
    [Test]
    public async Task FieldValueDataU64Roundtrip()
    {
        FieldValueData val = FieldValueData.NewU64(12345678UL);
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(12345678UL);
    }

    /// <summary>A double reads back unchanged and the union reports the type <c>F64</c>.</summary>
    [Test]
    public async Task FieldValueDataF64Roundtrip()
    {
        FieldValueData val = FieldValueData.NewF64(3.14);
        await Assert.That(val.Type).IsEqualTo(FieldType.F64);
        val.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(3.14);
    }

    /// <summary>A string reads back unchanged and the union reports the type <c>String</c>.</summary>
    [Test]
    public async Task FieldValueDataStringRoundtrip()
    {
        FieldValueData val = FieldValueData.NewString("hello");
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        val.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("hello");
    }

    /// <summary>Three bytes read back with the same length and order and the union reports the type <c>Bytes</c>.</summary>
    [Test]
    public async Task FieldValueDataBytesRoundtrip()
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

    /// <summary>Creating a bytes value from a null array is a caller bug and throws <see cref="ArgumentNullException"/>.</summary>
    [Test]
    public async Task FieldValueDataNewBytesNullArrayThrowsArgumentNullException()
    {
        await Assert.That(() => FieldValueData.NewBytes((byte[])null!)).Throws<ArgumentNullException>();
    }

    /// <summary>A MAC address reads back with the same raw value and the union reports the type <c>MacAddress</c>.</summary>
    [Test]
    public async Task FieldValueDataMacAddressRoundtrip()
    {
        MacAddress mac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        FieldValueData val = FieldValueData.NewMacAddress(mac);
        await Assert.That(val.Type).IsEqualTo(FieldType.MacAddress);
        val.TryGetAsMacAddress(out MacAddress macVal);
        await Assert.That(macVal.RawValue).IsEqualTo(mac.RawValue);
    }

    /// <summary>An IPv4 address reads back with the same raw value and the union reports the type <c>IPv4Address</c>.</summary>
    [Test]
    public async Task FieldValueDataIPv4Roundtrip()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        FieldValueData val = FieldValueData.NewIPv4(ip);
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv4Address);
        val.TryGetAsIPv4(out IPv4Address ipv4Val);
        await Assert.That(ipv4Val.RawValue).IsEqualTo(ip.RawValue);
    }

    /// <summary>A timestamp reads back with the same nanosecond count and the union reports the type <c>Timestamp</c>.</summary>
    [Test]
    public async Task FieldValueDataTimestampRoundtrip()
    {
        Timestamp ts = Timestamp.FromSecs(1000);
        FieldValueData val = FieldValueData.NewTimestamp(ts);
        await Assert.That(val.Type).IsEqualTo(FieldType.Timestamp);
        val.TryGetAsTimestamp(out Timestamp tsVal);
        await Assert.That(tsVal.AsNanos).IsEqualTo(ts.AsNanos);
    }

    // === FieldValueData Equality ===

    /// <summary>Two U64 values with the same number are equal through Equals and the equality operator.</summary>
    [Test]
    public async Task FieldValueDataEqualitySameValues()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewU64(100);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a == b).IsTrue();
    }

    /// <summary>Two U64 values with different numbers are unequal through Equals and the inequality operator.</summary>
    [Test]
    public async Task FieldValueDataInequalityDifferentValues()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewU64(200);
        await Assert.That(a.Equals(b)).IsFalse();
        await Assert.That(a != b).IsTrue();
    }

    /// <summary>A U64 and an I64 with the same number are unequal: the type is part of the value.</summary>
    [Test]
    public async Task FieldValueDataInequalityDifferentTypes()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewI64(100);
        await Assert.That(a.Equals(b)).IsFalse();
    }

    /// <summary>Two strings with equal text are equal.</summary>
    [Test]
    public async Task FieldValueDataEqualityStrings()
    {
        FieldValueData a = FieldValueData.NewString("test");
        FieldValueData b = FieldValueData.NewString("test");
        await Assert.That(a.Equals(b)).IsTrue();
    }

    /// <summary>Two strings with different text are unequal.</summary>
    [Test]
    public async Task FieldValueDataInequalityStrings()
    {
        FieldValueData a = FieldValueData.NewString("hello");
        FieldValueData b = FieldValueData.NewString("world");
        await Assert.That(a.Equals(b)).IsFalse();
    }

    /// <summary>Two different byte arrays with equal content are equal.</summary>
    [Test]
    public async Task FieldValueDataEqualityBytes()
    {
        byte[] d1 = [1, 2, 3];
        byte[] d2 = [1, 2, 3];
        FieldValueData a = FieldValueData.NewBytes(d1);
        FieldValueData b = FieldValueData.NewBytes(d2);
        await Assert.That(a.Equals(b)).IsTrue();
    }

    /// <summary>Two slices of one array that hold equal bytes at different offsets are equal, hash alike, and compare as 0.</summary>
    [Test]
    public async Task FieldValueDataEqualityBytesSlicesOfSameArrayWithEqualContent()
    {
        byte[] data = [1, 2, 3, 1, 2, 3];
        FieldValueData first = FieldValueData.NewBytes(new ReadOnlyMemory<byte>(data, 0, 3));
        FieldValueData second = FieldValueData.NewBytes(new ReadOnlyMemory<byte>(data, 3, 3));

        await Assert.That(first.Equals(second)).IsTrue();
        await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
        await Assert.That(first.CompareTo(second)).IsEqualTo(0);
    }

    /// <summary>Slices of one array with different content, or a slice and the whole array, are unequal; the whole array equals itself.</summary>
    [Test]
    public async Task FieldValueDataInequalityBytesSlicesOfSameArrayWithDifferentContent()
    {
        byte[] data = [1, 2, 3, 4];
        FieldValueData first = FieldValueData.NewBytes(new ReadOnlyMemory<byte>(data, 0, 3));
        FieldValueData second = FieldValueData.NewBytes(new ReadOnlyMemory<byte>(data, 1, 3));
        FieldValueData whole = FieldValueData.NewBytes(data);

        await Assert.That(first.Equals(second)).IsFalse();
        await Assert.That(first.Equals(whole)).IsFalse();
        await Assert.That(whole.Equals(FieldValueData.NewBytes(data))).IsTrue();
    }

    /// <summary>Two byte arrays with different content are unequal.</summary>
    [Test]
    public async Task FieldValueDataInequalityBytes()
    {
        byte[] d1 = [1, 2, 3];
        byte[] d2 = [4, 5, 6];
        FieldValueData a = FieldValueData.NewBytes(d1);
        FieldValueData b = FieldValueData.NewBytes(d2);
        await Assert.That(a.Equals(b)).IsFalse();
    }

    /// <summary>Two none values are equal.</summary>
    [Test]
    public async Task FieldValueDataNoneEquality()
    {
        FieldValueData a = FieldValueData.None;
        FieldValueData b = FieldValueData.None;
        await Assert.That(a.Equals(b)).IsTrue();
    }

    // === FieldValueData CompareTo ===

    /// <summary>U64 values order by number: smaller before larger, equal to itself.</summary>
    [Test]
    public async Task FieldValueDataCompareToU64()
    {
        FieldValueData a = FieldValueData.NewU64(10);
        FieldValueData b = FieldValueData.NewU64(20);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
        await Assert.That(a.CompareTo(a)).IsEqualTo(0);
    }

    /// <summary>A negative I64 orders before a positive one.</summary>
    [Test]
    public async Task FieldValueDataCompareToI64Negative()
    {
        FieldValueData a = FieldValueData.NewI64(-10);
        FieldValueData b = FieldValueData.NewI64(10);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    // === FieldValue ===

    /// <summary>The none value of FieldValue reports the type None.</summary>
    [Test]
    public async Task FieldValueNoneHasNoneType()
    {
        FieldValue none = FieldValue.None;
        await Assert.That(none.Type).IsEqualTo(FieldType.None);
    }

    /// <summary>NewBool creates a value of type Bool whose data reads back as the given bool.</summary>
    [Test]
    public async Task FieldValueNewBool()
    {
        FieldValue val = FieldValue.NewBool(true);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bool);
        val.Data.TryGetAsBool(out bool boolVal);
        await Assert.That(boolVal).IsTrue();
    }

    /// <summary>NewU64 creates a value of type U64 whose data reads back as the given number.</summary>
    [Test]
    public async Task FieldValueNewU64()
    {
        FieldValue val = FieldValue.NewU64(42);
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.Data.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(42UL);
    }

    /// <summary>NewI64 creates a value of type I64 whose data reads back as the given number.</summary>
    [Test]
    public async Task FieldValueNewI64()
    {
        FieldValue val = FieldValue.NewI64(-100);
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.Data.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-100L);
    }

    /// <summary>NewF64 creates a value of type F64 whose data reads back as the given number.</summary>
    [Test]
    public async Task FieldValueNewF64()
    {
        FieldValue val = FieldValue.NewF64(2.718);
        await Assert.That(val.Type).IsEqualTo(FieldType.F64);
        val.Data.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(2.718);
    }

    /// <summary>NewString creates a value of type String whose data reads back as the given text.</summary>
    [Test]
    public async Task FieldValueNewString()
    {
        FieldValue val = FieldValue.NewString("test");
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        val.Data.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("test");
    }

    /// <summary>NewBytes creates a value of type Bytes whose data reads back as the given bytes.</summary>
    [Test]
    public async Task FieldValueNewBytes()
    {
        byte[] data = [0xDE, 0xAD];
        FieldValue val = FieldValue.NewBytes(data);
        await Assert.That(val.Type).IsEqualTo(FieldType.Bytes);
        val.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
        await Assert.That(bytesVal.Length).IsEqualTo(2);
    }

    /// <summary>NewMacAddress creates a value of type MacAddress that reads back as the given address.</summary>
    [Test]
    public async Task FieldValueNewMacAddress()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        FieldValue val = FieldValue.NewMacAddress(mac);
        await Assert.That(val.Type).IsEqualTo(FieldType.MacAddress);
        val.Data.TryGetAsMacAddress(out MacAddress macVal);
        await Assert.That(macVal.Format()).IsEqualTo("00:11:22:33:44:55");
    }

    /// <summary>NewIPv4 creates a value of type IPv4Address that reads back as the given address.</summary>
    [Test]
    public async Task FieldValueNewIPv4()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        FieldValue val = FieldValue.NewIPv4(ip);
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv4Address);
        val.Data.TryGetAsIPv4(out IPv4Address ipv4Val);
        await Assert.That(ipv4Val.Format()).IsEqualTo("10.0.0.1");
    }

    /// <summary>NewIPv6 creates a value of type IPv6Address that reads back as the given address.</summary>
    [Test]
    public async Task FieldValueNewIPv6()
    {
        IPv6Address ip = IPv6Address.FromBytes(new byte[16]);
        FieldValue val = FieldValue.NewIPv6(ip, "custom-v6");
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv6Address);
        await Assert.That(val.CustomRepresentation.AsString).IsEqualTo("custom-v6");
        val.Data.TryGetAsIPv6(out IPv6Address ipv6Val);
        await Assert.That(ipv6Val.Format()).IsEqualTo("::");
    }

    /// <summary>NewEui64 creates a value of type Eui64 that reads back as the given identifier.</summary>
    [Test]
    public async Task FieldValueNewEui64()
    {
        Eui64 eui = Eui64.FromBytes(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 });
        FieldValue val = FieldValue.NewEui64(eui, "eui-custom");
        await Assert.That(val.Type).IsEqualTo(FieldType.Eui64);
        await Assert.That(val.CustomRepresentation.AsString).IsEqualTo("eui-custom");
        val.Data.TryGetAsEui64(out Eui64 euiVal);
        await Assert.That(euiVal.Format()).IsEqualTo("00:11:22:33:44:55:66:77");
    }

    /// <summary>NewUuid creates a value of type Uuid that reads back as the given identifier.</summary>
    [Test]
    public async Task FieldValueNewUuid()
    {
        Uuid uuid = Uuid.FromBytes(new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        });
        FieldValue val = FieldValue.NewUuid(uuid, "uuid-custom");
        await Assert.That(val.Type).IsEqualTo(FieldType.Uuid);
        await Assert.That(val.CustomRepresentation.AsString).IsEqualTo("uuid-custom");
        val.Data.TryGetAsUuid(out Uuid uuidVal);
        await Assert.That(uuidVal.ToString()).IsEqualTo(uuid.Format());
    }

    /// <summary>NewTimestamp creates a value of type Timestamp that reads back as the given instant.</summary>
    [Test]
    public async Task FieldValueNewTimestamp()
    {
        Timestamp ts = Timestamp.FromMillis(5000);
        FieldValue val = FieldValue.NewTimestamp(ts);
        await Assert.That(val.Type).IsEqualTo(FieldType.Timestamp);
        val.Data.TryGetAsTimestamp(out Timestamp tsVal);
        await Assert.That(tsVal.AsMillis).IsEqualTo(5000L);
    }

    /// <summary>A custom representation is kept next to the data and is what ToString returns.</summary>
    [Test]
    public async Task FieldValueWithCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        await Assert.That(val.CustomRepresentation.IsNull).IsFalse();
        await Assert.That(val.CustomRepresentation.AsString).IsEqualTo("HTTP (80)");
        await Assert.That(val.ToString()).IsEqualTo("HTTP (80)");
    }

    /// <summary>A value created without a custom representation has a null one.</summary>
    [Test]
    public async Task FieldValueWithoutCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80);
        await Assert.That(val.CustomRepresentation.IsNull).IsTrue();
    }

    /// <summary>Two values with equal data are equal whatever their custom representations are.</summary>
    [Test]
    public async Task FieldValueEqualityIgnoresCustomRepresentation()
    {
        FieldValue a = FieldValue.NewU64(42, "answer");
        FieldValue b = FieldValue.NewU64(42);
        // Equality ignores custom representation per the implementation
        await Assert.That(a == b).IsTrue();
    }

    /// <summary>Two values with different data are unequal.</summary>
    [Test]
    public async Task FieldValueInequalityDifferentData()
    {
        FieldValue a = FieldValue.NewU64(1);
        FieldValue b = FieldValue.NewU64(2);
        await Assert.That(a != b).IsTrue();
    }

    // === FieldValueData.ToString() ===

    /// <summary>The none value formats as the empty string.</summary>
    [Test]
    public async Task FieldValueDataToStringNone() => await Assert.That(FieldValueData.None.ToString()).IsEqualTo(string.Empty);

    /// <summary>A true bool formats as True.</summary>
    [Test]
    public async Task FieldValueDataToStringBoolTrue() => await Assert.That(FieldValueData.NewBool(true).ToString()).IsEqualTo("True");

    /// <summary>A false bool formats as False.</summary>
    [Test]
    public async Task FieldValueDataToStringBoolFalse() => await Assert.That(FieldValueData.NewBool(false).ToString()).IsEqualTo("False");

    /// <summary>A negative I64 formats with its minus sign.</summary>
    [Test]
    public async Task FieldValueDataToStringI64() => await Assert.That(FieldValueData.NewI64(-42).ToString()).IsEqualTo("-42");

    /// <summary>A U64 formats as its decimal digits.</summary>
    [Test]
    public async Task FieldValueDataToStringU64() => await Assert.That(FieldValueData.NewU64(12345).ToString()).IsEqualTo("12345");

    /// <summary>An F64 formats with the invariant culture.</summary>
    [Test]
    public async Task FieldValueDataToStringF64() =>
        // InvariantCulture uses '.' as decimal separator
        await Assert.That(FieldValueData.NewF64(3.14).ToString()).IsEqualTo("3.14");

    /// <summary>A string formats as itself.</summary>
    [Test]
    public async Task FieldValueDataToStringString() => await Assert.That(FieldValueData.NewString("hello").ToString()).IsEqualTo("hello");

    /// <summary>Empty bytes format as the empty string.</summary>
    [Test]
    public async Task FieldValueDataToStringBytesEmpty()
        => await Assert.That(FieldValueData.NewBytes(ReadOnlyMemory<byte>.Empty).ToString()).IsEqualTo(string.Empty);

    /// <summary>One byte formats as two upper-case hex digits.</summary>
    [Test]
    public async Task FieldValueDataToStringBytesSingleByte() => await Assert.That(FieldValueData.NewBytes([0xAB]).ToString()).IsEqualTo("AB");

    /// <summary>Several bytes format as upper-case hex pairs separated by blanks.</summary>
    [Test]
    public async Task FieldValueDataToStringBytesMultipleBytes()
    {
        await Assert.That(FieldValueData.NewBytes([0xDE, 0xAD, 0xBE, 0xEF]).ToString())
            .IsEqualTo("DE AD BE EF");
    }

    /// <summary>A MAC address formats as colon-separated hex pairs.</summary>
    [Test]
    public async Task FieldValueDataToStringMacAddress()
    {
        MacAddress mac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        await Assert.That(FieldValueData.NewMacAddress(mac).ToString()).IsEqualTo("AA:BB:CC:DD:EE:FF");
    }

    /// <summary>An IPv4 address formats in dotted decimal.</summary>
    [Test]
    public async Task FieldValueDataToStringIPv4()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        await Assert.That(FieldValueData.NewIPv4(ip).ToString()).IsEqualTo("192.168.1.1");
    }

    // === FieldValueData.TryFormat(Span<char>) ===

    /// <summary>Formatting the none value into characters succeeds and writes nothing.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharNone()
    {
        char[] buf = new char[16];
        bool result = FieldValueData.None.TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>A bool formats into characters as True or False.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharBool()
    {
        char[] buf = new char[16];
        bool result = FieldValueData.NewBool(true).TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("True");
    }

    /// <summary>A destination too small for a bool makes the character format report false with nothing written.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharBoolInsufficientSpace()
    {
        char[] buf = new char[3]; // "True" needs 4
        bool result = FieldValueData.NewBool(true).TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>A U64 formats into characters as its decimal digits.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharU64()
    {
        char[] buf = new char[32];
        bool result = FieldValueData.NewU64(65535).TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("65535");
    }

    /// <summary>Bytes format into characters as upper-case hex pairs separated by blanks.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharBytes()
    {
        char[] buf = new char[32];
        bool result = FieldValueData.NewBytes([0x0A, 0xFF]).TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("0A FF");
    }

    /// <summary>A destination too small for the hex text of bytes makes the character format report false.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharBytesInsufficientSpace()
    {
        char[] buf = new char[4]; // "0A FF" needs 5
        bool result = FieldValueData.NewBytes([0x0A, 0xFF]).TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>A string formats into characters as itself.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharString()
    {
        char[] buf = new char[32];
        bool result = FieldValueData.NewString("test").TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("test");
    }

    // === FieldValueData.TryFormat(Span<byte>) UTF-8 ===

    /// <summary>A bool formats into UTF-8 as True or False.</summary>
    [Test]
    public async Task FieldValueDataTryFormatUtf8Bool()
    {
        byte[] buf = new byte[16];
        bool result = FieldValueData.NewBool(false).TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("False");
    }

    /// <summary>A U64 formats into UTF-8 as its decimal digits.</summary>
    [Test]
    public async Task FieldValueDataTryFormatUtf8U64()
    {
        byte[] buf = new byte[32];
        bool result = FieldValueData.NewU64(999).TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("999");
    }

    /// <summary>Bytes format into UTF-8 as upper-case hex pairs separated by blanks.</summary>
    [Test]
    public async Task FieldValueDataTryFormatUtf8Bytes()
    {
        byte[] buf = new byte[32];
        bool result = FieldValueData.NewBytes([0xCA, 0xFE]).TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("CA FE");
    }

    /// <summary>A string formats into UTF-8 as its encoded text.</summary>
    [Test]
    public async Task FieldValueDataTryFormatUtf8String()
    {
        byte[] buf = new byte[32];
        bool result = FieldValueData.NewString("hello").TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("hello");
    }

    // === FieldValue.ToString() ===

    /// <summary>ToString of a value with a custom representation returns that representation.</summary>
    [Test]
    public async Task FieldValueToStringWithCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        await Assert.That(val.ToString()).IsEqualTo("HTTP (80)");
    }

    /// <summary>ToString of a value without a custom representation returns the default text of the data.</summary>
    [Test]
    public async Task FieldValueToStringWithoutCustomRepresentationFallsBackToData()
    {
        FieldValue val = FieldValue.NewU64(80);
        await Assert.That(val.ToString()).IsEqualTo("80");
    }

    /// <summary>ToString of the none value is the empty string.</summary>
    [Test]
    public async Task FieldValueToStringNone() => await Assert.That(FieldValue.None.ToString()).IsEqualTo(string.Empty);

    // === FieldValue.TryFormat ===

    /// <summary>The character format of a value with a custom representation writes that representation.</summary>
    [Test]
    public async Task FieldValueTryFormatCharUsesCustomRep()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP");
        char[] buf = new char[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("HTTP");
    }

    /// <summary>The character format of a value without a custom representation writes the text of the data.</summary>
    [Test]
    public async Task FieldValueTryFormatCharFallsBackToData()
    {
        FieldValue val = FieldValue.NewI64(-7);
        char[] buf = new char[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("-7");
    }

    /// <summary>The UTF-8 format of a value with a custom representation writes that representation.</summary>
    [Test]
    public async Task FieldValueTryFormatUtf8UsesCustomRep()
    {
        FieldValue val = FieldValue.NewU64(443, "HTTPS");
        byte[] buf = new byte[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("HTTPS");
    }

    /// <summary>The UTF-8 format of a value without a custom representation writes the text of the data.</summary>
    [Test]
    public async Task FieldValueTryFormatUtf8FallsBackToData()
    {
        FieldValue val = FieldValue.NewU64(443);
        byte[] buf = new byte[32];
        bool result = val.TryFormat(buf, out int written, default, null);
        string text = System.Text.Encoding.UTF8.GetString(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("443");
    }

    // === FieldValue.Data: the default text of the data ===

    /// <summary>Formatting the Data of a value that has a custom representation writes the data text, not the representation.</summary>
    [Test]
    public async Task DataTryFormatIgnoresTheCustomRepresentation()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP (80)");
        char[] buf = new char[32];
        bool result = val.Data.TryFormat(buf, out int written, default, null);
        string text = new(buf, 0, written);
        await Assert.That(result).IsTrue();
        await Assert.That(text).IsEqualTo("80");
    }

    // === Implicit operators ===

    /// <summary>A bool converts implicitly to a value of type Bool.</summary>
    [Test]
    public async Task FieldValueImplicitBool()
    {
        FieldValue val = true;
        await Assert.That(val.Type).IsEqualTo(FieldType.Bool);
        val.Data.TryGetAsBool(out bool boolVal);
        await Assert.That(boolVal).IsTrue();
    }

    /// <summary>A long converts implicitly to a value of type I64 with the same number.</summary>
    [Test]
    public async Task FieldValueImplicitLong()
    {
        FieldValue val = -100L;
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.Data.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-100L);
    }

    /// <summary>A ulong converts implicitly to a value of type U64 with the same number.</summary>
    [Test]
    public async Task FieldValueImplicitUlong()
    {
        FieldValue val = 42UL;
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.Data.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(42UL);
    }

    /// <summary>An int converts implicitly to a value of type I64 with the same number.</summary>
    [Test]
    public async Task FieldValueImplicitInt()
    {
        FieldValue val = -5;
        await Assert.That(val.Type).IsEqualTo(FieldType.I64);
        val.Data.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(-5L);
    }

    /// <summary>A uint converts implicitly to a value of type U64 with the same number.</summary>
    [Test]
    public async Task FieldValueImplicitUint()
    {
        FieldValue val = 100U;
        await Assert.That(val.Type).IsEqualTo(FieldType.U64);
        val.Data.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(100UL);
    }

    /// <summary>A double converts implicitly to a value of type F64 with the same number.</summary>
    [Test]
    public async Task FieldValueImplicitDouble()
    {
        FieldValue val = 2.5;
        await Assert.That(val.Type).IsEqualTo(FieldType.F64);
        val.Data.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(2.5);
    }

    /// <summary>A string converts implicitly to a value of type String with the same text.</summary>
    [Test]
    public async Task FieldValueImplicitString()
    {
        FieldValue val = "hello";
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        val.Data.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("hello");
    }

    /// <summary>A null string converts implicitly to the none value instead of failing.</summary>
    [Test]
    public async Task FieldValueImplicitStringNull()
    {
        FieldValue val = (string?)null;
        await Assert.That(val.Type).IsEqualTo(FieldType.None);
    }

    /// <summary>A byte array converts implicitly to a value of type Bytes with the same content.</summary>
    [Test]
    public async Task FieldValueImplicitByteArray()
    {
        FieldValue val = new byte[] { 0xAA, 0xBB };
        await Assert.That(val.Type).IsEqualTo(FieldType.Bytes);
        val.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal);
        await Assert.That(bytesVal.Length).IsEqualTo(2);
    }

    /// <summary>A null byte array converts implicitly to the none value instead of failing.</summary>
    [Test]
    public async Task FieldValueImplicitByteArrayNull()
    {
        FieldValue val = (byte[]?)null;
        await Assert.That(val.Type).IsEqualTo(FieldType.None);
    }

    /// <summary>A MAC address converts implicitly to a value of type MacAddress.</summary>
    [Test]
    public async Task FieldValueImplicitMacAddress()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        FieldValue val = mac;
        await Assert.That(val.Type).IsEqualTo(FieldType.MacAddress);
    }

    /// <summary>An IPv4 address converts implicitly to a value of type IPv4Address.</summary>
    [Test]
    public async Task FieldValueImplicitIPv4()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        FieldValue val = ip;
        await Assert.That(val.Type).IsEqualTo(FieldType.IPv4Address);
    }

    /// <summary>A timestamp converts implicitly to a value of type Timestamp.</summary>
    [Test]
    public async Task FieldValueImplicitTimestamp()
    {
        Timestamp ts = Timestamp.FromSecs(1);
        FieldValue val = ts;
        await Assert.That(val.Type).IsEqualTo(FieldType.Timestamp);
    }

    // === Cross-type CompareTo ===

    /// <summary>An I64 and a U64 compare by number across the two types: 10 is less than 20.</summary>
    [Test]
    public async Task CompareToI64VsU64Positive()
    {
        // I64(10) vs U64(20) → negative
        FieldValueData a = FieldValueData.NewI64(10);
        FieldValueData b = FieldValueData.NewU64(20);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
    }

    /// <summary>A negative I64 is always less than any U64, also 0.</summary>
    [Test]
    public async Task CompareToI64NegativeVsU64()
    {
        // I64(-1) vs U64(0) → negative (signed < 0 always less than unsigned)
        FieldValueData a = FieldValueData.NewI64(-1);
        FieldValueData b = FieldValueData.NewU64(0);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
    }

    /// <summary>An I64 and an F64 compare by number: 10 is less than 10.5.</summary>
    [Test]
    public async Task CompareToI64VsF64()
    {
        FieldValueData a = FieldValueData.NewI64(10);
        FieldValueData b = FieldValueData.NewF64(10.5);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    /// <summary>A U64 and an F64 compare by number: 100 is greater than 99.5.</summary>
    [Test]
    public async Task CompareToU64VsF64()
    {
        FieldValueData a = FieldValueData.NewU64(100);
        FieldValueData b = FieldValueData.NewF64(99.5);
        await Assert.That(a.CompareTo(b)).IsGreaterThan(0);
    }

    /// <summary>An IPv4 address is always less than an IPv6 address, also 255.255.255.255 against ::1.</summary>
    [Test]
    public async Task CompareToIPv4VsIPv6AlwaysLess()
    {
        FieldValueData a = FieldValueData.NewIPv4(IPv4Address.FromBytes([255, 255, 255, 255]));
        FieldValueData b = FieldValueData.NewIPv6(IPv6Address.FromBytes([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]));
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
    }

    /// <summary>Comparing two values looks at the data only, not at the custom representations.</summary>
    [Test]
    public async Task CompareToFieldValueIgnoresCustomRepresentation()
    {
        FieldValue a = FieldValue.NewU64(10, "ten");
        FieldValue b = FieldValue.NewU64(20, "twenty");
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    /// <summary>Two values of type I64 and U64 compare by number through the value type as well.</summary>
    [Test]
    public async Task CompareToFieldValueCrossTypeI64U64()
    {
        FieldValue a = FieldValue.NewI64(-5);
        FieldValue b = FieldValue.NewU64(10);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
    }

    // === TryGetStringSize ===

    /// <summary>The none value has the text size 0 and the size query succeeds.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeNoneReturnsZero()
    {
        FieldValueData val = FieldValueData.None;
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(0);
    }

    /// <summary>A true bool has the text size 4 (True).</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeBoolTrueReturnsFour()
    {
        FieldValueData val = FieldValueData.NewBool(true);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(4); // "True"
    }

    /// <summary>A false bool has the text size 5 (False).</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeBoolFalseReturnsFive()
    {
        FieldValueData val = FieldValueData.NewBool(false);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(5); // "False"
    }

    /// <summary>The size of an I64 is an upper bound: not less than the text length, also for the smallest long.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeI64ReturnsBoundedSize()
    {
        FieldValueData val = FieldValueData.NewI64(long.MinValue);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsGreaterThanOrEqualTo(val.ToString().Length);
    }

    /// <summary>The size of a U64 is an upper bound: not less than the text length, also for the largest ulong.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeU64ReturnsBoundedSize()
    {
        FieldValueData val = FieldValueData.NewU64(ulong.MaxValue);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsGreaterThanOrEqualTo(val.ToString().Length);
    }

    /// <summary>An F64 has no text size known in advance: the size query reports false with size 0.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeF64ReturnsFalse()
    {
        FieldValueData val = FieldValueData.NewF64(3.14);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    /// <summary>A string has exactly the size of its length.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeStringReturnsExactLength()
    {
        string text = "Hello, World!";
        FieldValueData val = FieldValueData.NewString(text);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(text.Length);
    }

    /// <summary>Bytes have the exact size of their hex text with blanks between the pairs: four bytes are 11 characters.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeBytesReturnsExactLength()
    {
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF];
        FieldValueData val = FieldValueData.NewBytes(bytes);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        // "DE AD BE EF" = 4*3-1 = 11
        await Assert.That(size).IsEqualTo(11);
    }

    /// <summary>A corrupt bytes length whose hex text would overflow an int makes the size query report false with size 0.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeBytesOverflowLengthReturnsFalse()
    {
        FieldValueData val = _WithBytesPackedLength(FieldValueData.NewBytes([0x01]), ((long)int.MaxValue + 1) / 3 + 1);

        bool result = val.TryGetStringSize(default, null, out int size);

        await Assert.That(result).IsFalse();
        await Assert.That(size).IsEqualTo(0);
    }

    /// <summary>The same corrupt bytes length makes the character and the UTF-8 format report false with nothing written.</summary>
    [Test]
    public async Task FieldValueDataTryFormatBytesOverflowLengthReturnsFalse()
    {
        FieldValueData val = _WithBytesPackedLength(FieldValueData.NewBytes([0x01]), ((long)int.MaxValue + 1) / 3 + 1);
        Span<char> charBuf = stackalloc char[16];
        byte[] utf8Buf = new byte[16];

        bool charOk = val.TryFormat(charBuf, out int charWritten, default, null);
        bool utf8Ok = val.TryFormat(utf8Buf, out int utf8Written, default, null);

        await Assert.That(charOk).IsFalse();
        await Assert.That(charWritten).IsEqualTo(0);
        await Assert.That(utf8Ok).IsFalse();
        await Assert.That(utf8Written).IsEqualTo(0);
    }

    /// <summary>A MAC address has the size of its text.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeMacAddressReturnsExactLength()
    {
        MacAddress mac = new(0x112233445566UL);
        FieldValueData val = FieldValueData.NewMacAddress(mac);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(mac.ToString().Length);
    }

    /// <summary>The size of an IPv4 address is not less than the length of its text.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeIPv4ReturnsExactLength()
    {
        IPv4Address ip = new(0xC0A80101u); // 192.168.1.1
        FieldValueData val = FieldValueData.NewIPv4(ip);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsGreaterThanOrEqualTo(ip.ToString().Length);
    }

    /// <summary>A value with a custom representation has the size of that representation.</summary>
    [Test]
    public async Task FieldValueTryGetStringSizeWithCustomRepresentationReturnsCustomLength()
    {
        string custom = "Custom Text";
        FieldValue val = FieldValue.NewU64(42, custom);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(custom.Length);
    }

    /// <summary>A value without a custom representation has the size of its data text.</summary>
    [Test]
    public async Task FieldValueTryGetStringSizeWithoutCustomRepresentationDelegatesToData()
    {
        FieldValue val = FieldValue.NewBool(true);
        bool result = val.TryGetStringSize(default, null, out int size);
        await Assert.That(result).IsTrue();
        await Assert.That(size).IsEqualTo(4); // "True"
    }

    // === ToString ===

    /// <summary>
    /// One value of every field type; the string, lazy, and bytes entries cover the non-scalar paths of
    /// <see cref="FieldValueData.ToString()"/>.
    /// </summary>
    private static FieldValueData[] _EveryFieldType() =>
    [
        FieldValueData.None,
        FieldValueData.NewBool(true),
        FieldValueData.NewBool(false),
        FieldValueData.NewI64(long.MinValue),
        FieldValueData.NewU64(ulong.MaxValue),
        FieldValueData.NewF64(-1.7976931348623157E+308),
        FieldValueData.NewString("test"),
        FieldValueData.NewBytes(Array.Empty<byte>()),
        FieldValueData.NewBytes([0xAB]),
        FieldValueData.NewBytes([0xAB, 0xCD]),
        FieldValueData.NewMacAddress(new MacAddress(0x001122334455UL)),
        FieldValueData.NewIPv4(new IPv4Address(0x0A000001u)),
        FieldValueData.NewIPv6(new IPv6Address(0x20010DB800000000UL, 0x0000000000000001UL)),
        FieldValueData.NewEui64(new Eui64(0x0011223344556677UL)),
        FieldValueData.NewUuid(new Uuid(0x550E8400E29B41D4UL, 0xA716446655440000UL)),
        FieldValueData.NewTimestamp(Timestamp.FromNanos(1_700_000_000_123_456_789L)),
        FieldValueData.NewLazyString(LazyString.Lazy(static () => "lazy")),
    ];

    /// <summary>For one value of every field type the string and the character format give the same text.</summary>
    [Test]
    public async Task FieldValueDataToStringEqualsTryFormatTextForEveryFieldType()
    {
        char[] large = new char[256];

        foreach (FieldValueData val in _EveryFieldType())
        {
            bool ok = val.TryFormat(large, out int written, default, CultureInfo.InvariantCulture);
            string viaTryFormat = new(large, 0, written);

            await Assert.That(ok).IsTrue();
            await Assert.That(val.ToString()).IsEqualTo(viaTryFormat);
        }
    }

    /// <summary>The default text of each common type is the documented one, for example 11:22:33:44:55:66 for a MAC address.</summary>
    [Test]
    public async Task FieldValueDataToStringKnownTexts()
    {
        await Assert.That(FieldValueData.None.ToString()).IsEqualTo(string.Empty);
        await Assert.That(FieldValueData.NewBool(true).ToString()).IsEqualTo("True");
        await Assert.That(FieldValueData.NewBool(false).ToString()).IsEqualTo("False");
        await Assert.That(FieldValueData.NewI64(-1234567890L).ToString()).IsEqualTo("-1234567890");
        await Assert.That(FieldValueData.NewU64(9876543210UL).ToString()).IsEqualTo("9876543210");
        await Assert.That(FieldValueData.NewString("Hello").ToString()).IsEqualTo("Hello");
        await Assert.That(FieldValueData.NewBytes(Array.Empty<byte>()).ToString()).IsEqualTo(string.Empty);
        await Assert.That(FieldValueData.NewBytes([0xDE, 0xAD, 0xBE, 0xEF]).ToString()).IsEqualTo("DE AD BE EF");
        await Assert.That(FieldValueData.NewMacAddress(new MacAddress(0x112233445566UL)).ToString()).IsEqualTo("11:22:33:44:55:66");
        await Assert.That(FieldValueData.NewIPv4(new IPv4Address(0xC0A80101u)).ToString()).IsEqualTo("192.168.1.1");
    }

    /// <summary>ToString of a string value returns the stored string instance and does not copy it.</summary>
    [Test]
    public async Task FieldValueDataToStringReturnsTheStoredStringInstance()
    {
        string text = new('x', 5);
        FieldValueData val = FieldValueData.NewString(text);

        string result = val.ToString();

        await Assert.That(ReferenceEquals(result, text)).IsTrue();
    }

    /// <summary>ToString returns the custom representation when there is one and the data text otherwise.</summary>
    [Test]
    public async Task FieldValueToStringPrefersTheCustomRepresentation()
    {
        FieldValue withCustom = FieldValue.NewU64(42, "Custom Display");
        FieldValue withoutCustom = FieldValue.NewI64(99L);

        await Assert.That(withCustom.ToString()).IsEqualTo("Custom Display");
        await Assert.That(withoutCustom.ToString()).IsEqualTo("99");
    }

    /// <summary>ToString of the Data of a value that has a custom representation returns the data text.</summary>
    [Test]
    public async Task DataToStringIgnoresTheCustomRepresentation()
    {
        FieldValue val = FieldValue.NewI64(42L, "custom");

        string result = val.Data.ToString();

        await Assert.That(result).IsEqualTo("42");
    }

    // === FieldValue equality, comparison, formatting gaps ===

    /// <summary>Equals with an object is true for an equal value and false for another type; equal values share the hash code.</summary>
    [Test]
    public async Task FieldValueEqualsObjectAndGetHashCode()
    {
        FieldValue a = FieldValue.NewU64(42);
        FieldValue b = FieldValue.NewU64(42);
        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.Equals((object)"not")).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    /// <summary>The four ordering operators follow the number order, also less-or-equal and greater-or-equal on equal values.</summary>
    [Test]
    public async Task FieldValueComparisonOperators()
    {
        FieldValue low = FieldValue.NewU64(1);
        FieldValue high = FieldValue.NewU64(9);
        FieldValue lowCopy = FieldValue.NewU64(1);
        await Assert.That(low < high).IsTrue();
        await Assert.That(high > low).IsTrue();
        await Assert.That(low <= high).IsTrue();
        await Assert.That(high >= low).IsTrue();
        await Assert.That(low <= lowCopy).IsTrue();
        await Assert.That(lowCopy >= low).IsTrue();
    }

    /// <summary>A destination too small for the custom representation makes the character format report false, nothing written.</summary>
    [Test]
    public async Task FieldValueTryFormatCustomRepInsufficientCharBuffer()
    {
        FieldValue val = FieldValue.NewU64(1, "ABCDEF");
        char[] buf = new char[3];
        bool result = val.TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>A destination too small for the custom representation makes the UTF-8 format report false, nothing written.</summary>
    [Test]
    public async Task FieldValueTryFormatCustomRepInsufficientUtf8Buffer()
    {
        FieldValue val = FieldValue.NewU64(1, "ABCDEF");
        byte[] buf = new byte[2];
        bool result = val.TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>The ToString overload with a format provider ignores the provider and returns the same text as the plain one.</summary>
    [Test]
    public async Task FieldValueToStringWithFormatProvider()
    {
        FieldValue val = FieldValue.NewU64(80, "HTTP");
        await Assert.That(val.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("HTTP");
        await Assert.That(FieldValue.NewI64(-5).ToString(null, null)).IsEqualTo("-5");
    }

    /// <summary>short and sbyte convert implicitly to I64, ushort and byte to U64, each with the same number.</summary>
    [Test]
    public async Task FieldValueImplicitNarrowNumericTypes()
    {
        FieldValue fromShort = (short)-3;
        FieldValue fromUshort = (ushort)5;
        FieldValue fromSbyte = (sbyte)-2;
        FieldValue fromByte = (byte)9;
        await Assert.That(fromShort.Data.TryGetAsI64(out long i64)).IsTrue();
        await Assert.That(i64).IsEqualTo(-3L);
        await Assert.That(fromUshort.Data.TryGetAsU64(out ulong u64)).IsTrue();
        await Assert.That(u64).IsEqualTo(5UL);
        await Assert.That(fromSbyte.Data.TryGetAsI64(out long i64b)).IsTrue();
        await Assert.That(i64b).IsEqualTo(-2L);
        await Assert.That(fromByte.Data.TryGetAsU64(out ulong u64b)).IsTrue();
        await Assert.That(u64b).IsEqualTo(9UL);
    }

    /// <summary>An IPv6 address, an EUI-64 identifier, and a UUID convert implicitly to values of their own types.</summary>
    [Test]
    public async Task FieldValueImplicitAddressTypes()
    {
        IPv6Address ipv6 = IPv6Address.FromBytes(new byte[16]);
        Eui64 eui = Eui64.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Uuid uuid = Uuid.FromBytes(new byte[16]);
        FieldValue v6 = ipv6;
        FieldValue euiVal = eui;
        FieldValue uuidVal = uuid;
        await Assert.That(v6.Type).IsEqualTo(FieldType.IPv6Address);
        await Assert.That(euiVal.Type).IsEqualTo(FieldType.Eui64);
        await Assert.That(uuidVal.Type).IsEqualTo(FieldType.Uuid);
    }

    /// <summary>A string created from a ReadOnlyMemory of characters reads back as that text.</summary>
    [Test]
    public async Task FieldValueDataNewStringFromReadOnlyMemory()
    {
        FieldValueData val = FieldValueData.NewString("memory".AsMemory());
        await Assert.That(val.TryGetAsString(out string text)).IsTrue();
        await Assert.That(text).IsEqualTo("memory");
    }

    /// <summary>Bytes created from memory that is not array-backed read back with the same content.</summary>
    [Test]
    public async Task FieldValueDataNewBytesFromNonArrayMemory()
    {
        using System.Buffers.IMemoryOwner<byte> owner = System.Buffers.MemoryPool<byte>.Shared.Rent(4);
        ReadOnlySpan<byte> source = [0x01, 0x02, 0x03];
        source.CopyTo(owner.Memory.Span[..3]);
        FieldValueData val = FieldValueData.NewBytes(owner.Memory[..3]);
        await Assert.That(val.TryGetAsBytes(out ReadOnlyMemory<byte> bytes)).IsTrue();
        await Assert.That(bytes.Length).IsEqualTo(3);
        await Assert.That(bytes.Span[0]).IsEqualTo((byte)0x01);
    }

    /// <summary>Every typed reader other than the one of the stored type reports false.</summary>
    [Test]
    public async Task FieldValueDataTryGetAsWrongTypeReturnsFalse()
    {
        FieldValueData u64 = FieldValueData.NewU64(1);
        await Assert.That(u64.TryGetAsBool(out _)).IsFalse();
        await Assert.That(u64.TryGetAsI64(out _)).IsFalse();
        await Assert.That(u64.TryGetAsF64(out _)).IsFalse();
        await Assert.That(u64.TryGetAsString(out _)).IsFalse();
        await Assert.That(u64.TryGetAsBytes(out _)).IsFalse();
        await Assert.That(u64.TryGetAsMacAddress(out _)).IsFalse();
        await Assert.That(u64.TryGetAsIPv4(out _)).IsFalse();
        await Assert.That(u64.TryGetAsIPv6(out _)).IsFalse();
        await Assert.That(u64.TryGetAsEui64(out _)).IsFalse();
        await Assert.That(u64.TryGetAsUuid(out _)).IsFalse();
        await Assert.That(u64.TryGetAsTimestamp(out _)).IsFalse();
    }

    /// <summary>A lazy string reports the type String and gives its built text when read.</summary>
    [Test]
    public async Task FieldValueDataLazyStringRoundtrip()
    {
        LazyString lazy = LazyString.Lazy(static () => "lazy-value");
        FieldValueData val = FieldValueData.NewLazyString(lazy);
        await Assert.That(val.Type).IsEqualTo(FieldType.String);
        await Assert.That(val.TryGetAsString(out string text)).IsTrue();
        await Assert.That(text).IsEqualTo("lazy-value");
    }

    /// <summary>Equals with an object is true for an equal union and false for another type; equal unions share the hash code.</summary>
    [Test]
    public async Task FieldValueDataEqualsObjectAndGetHashCode()
    {
        FieldValueData a = FieldValueData.NewString("x");
        FieldValueData b = FieldValueData.NewString("x");
        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.Equals((object)FieldValueData.NewU64(1))).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    /// <summary>The four ordering operators of the union follow the number order.</summary>
    [Test]
    public async Task FieldValueDataComparisonOperators()
    {
        FieldValueData low = FieldValueData.NewU64(1);
        FieldValueData high = FieldValueData.NewU64(9);
        await Assert.That(low < high).IsTrue();
        await Assert.That(high > low).IsTrue();
        await Assert.That(low <= high).IsTrue();
        await Assert.That(high >= low).IsTrue();
    }

    /// <summary>A MAC address orders before an EUI-64 identifier that starts with the same bytes, and the reverse holds.</summary>
    [Test]
    public async Task FieldValueDataCompareToMacAddressVsEui64()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        Eui64 eui = Eui64.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]);
        FieldValueData macVal = FieldValueData.NewMacAddress(mac);
        FieldValueData euiVal = FieldValueData.NewEui64(eui);
        await Assert.That(macVal.CompareTo(euiVal)).IsLessThan(0);
        await Assert.That(euiVal.CompareTo(macVal)).IsGreaterThan(0);
    }

    /// <summary>Values whose types have no common order (a string and a U64) are never ordered as equal: the type order decides.</summary>
    [Test]
    public async Task FieldValueDataCompareToIncompatibleTypesUsesTypeOrder()
    {
        FieldValueData str = FieldValueData.NewString("a");
        FieldValueData u64 = FieldValueData.NewU64(1);
        await Assert.That(str.CompareTo(u64)).IsNotEqualTo(0);
    }

    /// <summary>IPv6, EUI-64, UUID, and timestamp values format as the text of their own formatting methods.</summary>
    [Test]
    public async Task FieldValueDataToStringIPv6Eui64UuidTimestamp()
    {
        IPv6Address ipv6 = IPv6Address.FromBytes(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });
        Eui64 eui = Eui64.FromBytes(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        Uuid uuid = Uuid.FromBytes(new byte[16]);
        Timestamp ts = Timestamp.FromSecs(5);
        await Assert.That(FieldValueData.NewIPv6(ipv6).ToString()).IsEqualTo(ipv6.Format());
        await Assert.That(FieldValueData.NewEui64(eui).ToString()).IsEqualTo(eui.Format());
        await Assert.That(FieldValueData.NewUuid(uuid).ToString()).IsEqualTo(uuid.Format());
        await Assert.That(FieldValueData.NewTimestamp(ts).ToString()).IsEqualTo(ts.ToString());
    }

    /// <summary>Every scalar type formats into a 128-character destination and writes at least one character.</summary>
    [Test]
    public async Task FieldValueDataTryFormatCharAllScalarTypes()
    {
        FieldValueData[] values =
        [
            FieldValueData.NewI64(-7),
            FieldValueData.NewF64(1.5),
            FieldValueData.NewMacAddress(new MacAddress(0xAABBCCDDEEFFUL)),
            FieldValueData.NewIPv4(new IPv4Address(0x7F000001u)),
            FieldValueData.NewIPv6(IPv6Address.FromBytes(new byte[16])),
            FieldValueData.NewEui64(Eui64.FromBytes(new byte[8])),
            FieldValueData.NewUuid(Uuid.FromBytes(new byte[16])),
            FieldValueData.NewTimestamp(Timestamp.FromSecs(1)),
        ];

        foreach (FieldValueData val in values)
        {
            char[] buf = new char[128];
            bool result = val.TryFormat(buf, out int written, default, null);
            await Assert.That(result).IsTrue();
            await Assert.That(written).IsGreaterThan(0);
        }
    }

    /// <summary>Every scalar type formats into a 128-byte UTF-8 destination and writes at least one byte.</summary>
    [Test]
    public async Task FieldValueDataTryFormatUtf8AllScalarTypes()
    {
        FieldValueData[] values =
        [
            FieldValueData.NewI64(-7),
            FieldValueData.NewF64(1.5),
            FieldValueData.NewMacAddress(new MacAddress(0xAABBCCDDEEFFUL)),
            FieldValueData.NewIPv4(new IPv4Address(0x7F000001u)),
            FieldValueData.NewIPv6(IPv6Address.FromBytes(new byte[16])),
            FieldValueData.NewEui64(Eui64.FromBytes(new byte[8])),
            FieldValueData.NewUuid(Uuid.FromBytes(new byte[16])),
            FieldValueData.NewTimestamp(Timestamp.FromSecs(1)),
        ];

        foreach (FieldValueData val in values)
        {
            byte[] buf = new byte[128];
            bool result = val.TryFormat(buf, out int written, default, null);
            await Assert.That(result).IsTrue();
            await Assert.That(written).IsGreaterThan(0);
        }
    }

    /// <summary>A too small character or UTF-8 destination makes the string format report false with nothing written.</summary>
    [Test]
    public async Task FieldValueDataTryFormatStringInsufficientBuffers()
    {
        FieldValueData val = FieldValueData.NewString("ABCDEF");
        char[] charBuf = new char[3];
        byte[] byteBuf = new byte[2];
        await Assert.That(val.TryFormat(charBuf, out int charWritten, default, null)).IsFalse();
        await Assert.That(charWritten).IsEqualTo(0);
        await Assert.That(val.TryFormat(byteBuf, out int byteWritten, default, null)).IsFalse();
        await Assert.That(byteWritten).IsEqualTo(0);
    }

    /// <summary>A UTF-8 destination of 3 bytes is too small for True: the format reports false with nothing written.</summary>
    [Test]
    public async Task FieldValueDataTryFormatBoolUtf8InsufficientSpace()
    {
        byte[] buf = new byte[3];
        bool result = FieldValueData.NewBool(true).TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>A UTF-8 destination of 4 bytes is too small for the hex text of four bytes: the format reports false.</summary>
    [Test]
    public async Task FieldValueDataTryFormatBytesUtf8InsufficientSpace()
    {
        byte[] buf = new byte[4];
        bool result = FieldValueData.NewBytes([0xDE, 0xAD, 0xBE, 0xEF]).TryFormat(buf, out int written, default, null);
        await Assert.That(result).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>The size query succeeds for IPv6, EUI-64, UUID, and timestamp values and returns a positive size.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeIPv6Eui64UuidTimestamp()
    {
        await Assert.That(FieldValueData.NewIPv6(IPv6Address.FromBytes(new byte[16])).TryGetStringSize(default, null, out int ipv6Size)).IsTrue();
        await Assert.That(ipv6Size).IsGreaterThan(0);
        await Assert.That(FieldValueData.NewEui64(Eui64.FromBytes(new byte[8])).TryGetStringSize(default, null, out int euiSize)).IsTrue();
        await Assert.That(euiSize).IsGreaterThan(0);
        await Assert.That(FieldValueData.NewUuid(Uuid.FromBytes(new byte[16])).TryGetStringSize(default, null, out int uuidSize)).IsTrue();
        await Assert.That(uuidSize).IsGreaterThan(0);
        await Assert.That(FieldValueData.NewTimestamp(Timestamp.FromSecs(1)).TryGetStringSize(default, null, out int tsSize)).IsTrue();
        await Assert.That(tsSize).IsGreaterThan(0);
    }

    /// <summary>A lazy string reports the length of its built text as size.</summary>
    [Test]
    public async Task FieldValueDataTryGetStringSizeLazyStringReturnsLength()
    {
        FieldValueData val = FieldValueData.NewLazyString(LazyString.Lazy(static () => "lazy"));
        await Assert.That(val.TryGetStringSize(default, null, out int size)).IsTrue();
        await Assert.That(size).IsEqualTo(4);
    }

    /// <summary>Two string values that share one string instance are equal.</summary>
    [Test]
    public async Task FieldValueDataEqualitySameRefDifferentData()
    {
        string shared = "shared";
        FieldValueData a = FieldValueData.NewString(shared);
        FieldValueData b = FieldValueData.NewString(shared);
        await Assert.That(a.Equals(b)).IsTrue();
    }

    /// <summary>Bytes and UUID values give a non-zero hash code.</summary>
    [Test]
    public async Task FieldValueDataGetHashCodeBytesAndUuid()
    {
        byte[] bytes = [1, 2, 3];
        FieldValueData bytesVal = FieldValueData.NewBytes(bytes);
        FieldValueData uuidVal = FieldValueData.NewUuid(Uuid.FromBytes(new byte[16]));
        await Assert.That(bytesVal.GetHashCode()).IsNotEqualTo(0);
        await Assert.That(uuidVal.GetHashCode()).IsNotEqualTo(0);
    }

    /// <summary>The UTF-8 format, the ToString overload, and the size query of the Data of a value with a custom representation give the data text.</summary>
    [Test]
    public async Task DataUtf8TryFormatToStringOverloadAndStringSize()
    {
        FieldValueData dataText = FieldValue.NewU64(99, "custom").Data;

        byte[] utf8 = new byte[16];
        bool utf8Ok = dataText.TryFormat(utf8, out int byteWritten, default, null);
        string utf8Text = Encoding.UTF8.GetString(utf8, 0, byteWritten);

        bool sizeOk = dataText.TryGetStringSize(default, null, out int size);
        string overload = dataText.ToString("G", CultureInfo.InvariantCulture);

        await Assert.That(utf8Ok).IsTrue();
        await Assert.That(utf8Text).IsEqualTo("99");
        await Assert.That(sizeOk).IsTrue();
        await Assert.That(size).IsEqualTo(20);
        await Assert.That(overload).IsEqualTo("99");
    }

    /// <summary>CompareTo and Equals give the expected order for pairs of each type with its own comparison branch, and F64 against I64 and U64.</summary>
    [Test]
    public async Task FieldValueDataMatrixExercisesCompareToAndEqualsSwitchArms()
    {
        FieldValueData noneA = FieldValueData.None;
        FieldValueData noneB = FieldValueData.None;
        await Assert.That(noneA.Equals(noneB)).IsTrue();
        await Assert.That(noneA.CompareTo(noneB)).IsEqualTo(0);

        FieldValueData i64A = FieldValueData.NewI64(1);
        FieldValueData i64B = FieldValueData.NewI64(2);
        await Assert.That(i64A.CompareTo(i64B)).IsLessThan(0);

        FieldValueData f64A = FieldValueData.NewF64(1.0);
        FieldValueData f64B = FieldValueData.NewF64(2.0);
        await Assert.That(f64A.CompareTo(f64B)).IsLessThan(0);

        FieldValueData strA = FieldValueData.NewString("a");
        FieldValueData strB = FieldValueData.NewString("b");
        await Assert.That(strA.CompareTo(strB)).IsLessThan(0);
        await Assert.That(strA.Equals(strB)).IsFalse();

        FieldValueData bytesA = FieldValueData.NewBytes([1]);
        FieldValueData bytesB = FieldValueData.NewBytes([2]);
        await Assert.That(bytesA.CompareTo(bytesB)).IsLessThan(0);
        await Assert.That(bytesA.Equals(bytesB)).IsFalse();

        FieldValueData ipv6A = FieldValueData.NewIPv6(IPv6Address.FromBytes(new byte[16]));
        FieldValueData ipv6B = FieldValueData.NewIPv6(IPv6Address.FromBytes(
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
        ]));
        await Assert.That(ipv6A.CompareTo(ipv6B)).IsLessThan(0);

        FieldValueData uuidA = FieldValueData.NewUuid(Uuid.FromBytes(new byte[16]));
        FieldValueData uuidB = FieldValueData.NewUuid(Uuid.FromBytes(
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
        ]));
        await Assert.That(uuidA.CompareTo(uuidB)).IsLessThan(0);

        await Assert.That(FieldValueData.NewF64(2).CompareTo(FieldValueData.NewI64(1))).IsGreaterThan(0);
        await Assert.That(FieldValueData.NewF64(2).CompareTo(FieldValueData.NewU64(1))).IsGreaterThan(0);
    }

    /// <summary>A lazy string formats to its built text through both formats and both ToString forms; a too small destination reports false.</summary>
    [Test]
    public async Task FieldValueDataLazyStringFormattingPaths()
    {
        FieldValueData lazy = FieldValueData.NewLazyString(LazyString.Lazy(static () => "lazy-format"));

        char[] charBuf = new char[32];
        bool charOk = lazy.TryFormat(charBuf, out int charWritten, default, null);
        string charText = new(charBuf, 0, charWritten);

        byte[] utf8Buf = new byte[32];
        bool utf8Ok = lazy.TryFormat(utf8Buf, out int byteWritten, default, null);
        string utf8Text = Encoding.UTF8.GetString(utf8Buf, 0, byteWritten);

        string toString = lazy.ToString();
        string overload = lazy.ToString(null, CultureInfo.InvariantCulture);

        bool smallChar = lazy.TryFormat(new char[3], out int smallCharWritten, default, null);
        bool smallUtf8 = lazy.TryFormat(new byte[3], out int smallUtf8Written, default, null);

        await Assert.That(charOk).IsTrue();
        await Assert.That(charText).IsEqualTo("lazy-format");
        await Assert.That(utf8Ok).IsTrue();
        await Assert.That(utf8Text).IsEqualTo("lazy-format");
        await Assert.That(toString).IsEqualTo("lazy-format");
        await Assert.That(overload).IsEqualTo("lazy-format");
        await Assert.That(smallChar).IsFalse();
        await Assert.That(smallCharWritten).IsEqualTo(0);
        await Assert.That(smallUtf8).IsFalse();
        await Assert.That(smallUtf8Written).IsEqualTo(0);
    }

    /// <summary>Bytes created from non-array memory are copied, so changing the source afterwards does not change the value.</summary>
    [Test]
    public async Task FieldValueDataNewBytesCopyPathReturnsIndependentBuffer()
    {
        using NonArrayBytesMemory owner = new([0xAA, 0xBB]);
        FieldValueData val = FieldValueData.NewBytes(owner.Memory);
        val.TryGetAsBytes(out ReadOnlyMemory<byte> bytes);
        owner.Memory.Span[0] = 0xFF;
        await Assert.That(bytes.Span[0]).IsEqualTo((byte)0xAA);
        await Assert.That(bytes.Span[1]).IsEqualTo((byte)0xBB);
    }

    /// <summary>Memory not array-backed so <see cref="FieldValueData.NewBytes"/> copies.</summary>
    private sealed class NonArrayBytesMemory : MemoryManager<byte>
    {
        private readonly byte[] _data;

        public NonArrayBytesMemory(byte[] data) => _data = data;

        public override Span<byte> GetSpan() => _data;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    /// <summary>Reading a bool as U64 reports false and gives 0.</summary>
    [Test]
    public async Task FieldValueDataTryGetAsU64OnNonU64ReturnsFalse()
    {
        FieldValueData val = FieldValueData.NewBool(true);
        bool ok = val.TryGetAsU64(out ulong value);
        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(0UL);
    }

    /// <summary>
    /// Returns a copy of <paramref name="value"/> whose private reference slot holds <paramref name="refValue"/>, to reach the
    /// guards against a corrupt union.
    /// </summary>
    /// <param name="value">The union to copy.</param>
    /// <param name="refValue">The object to put into the reference slot.</param>
    /// <returns>The modified copy.</returns>
    private static FieldValueData _WithRef(FieldValueData value, object? refValue)
    {
        object boxed = value;
        System.Reflection.FieldInfo field = typeof(FieldValueData).GetField(
            "_Ref",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(boxed, refValue);
        return (FieldValueData)boxed;
    }

    /// <summary>
    /// Returns a copy of <paramref name="value"/> whose packed bytes length is <paramref name="length"/>, to reach the overflow
    /// guards of the hex text size.
    /// </summary>
    /// <param name="value">A bytes union to copy.</param>
    /// <param name="length">The length to store in the high half of the data word.</param>
    /// <returns>The modified copy.</returns>
    private static FieldValueData _WithBytesPackedLength(FieldValueData value, long length)
    {
        object boxed = value;
        System.Reflection.FieldInfo dataField = typeof(FieldValueData).GetField(
            "_Data",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ulong packed = (ulong)(uint)length << 32;
        dataField.SetValue(boxed, packed);
        return (FieldValueData)boxed;
    }

    /// <summary>A union whose reference slot holds a foreign object reports the type None, formats nothing, and has size 0.</summary>
    [Test]
    public async Task FieldValueDataCorruptRefFallbackClassificationAndFormatting()
    {
        FieldValueData corrupt = _WithRef(FieldValueData.NewU64(1), new());
        await Assert.That(corrupt.Type).IsEqualTo(FieldType.None);

        Span<char> charBuf = stackalloc char[4];
        bool charOk = corrupt.TryFormat(charBuf, out int charWritten, default, null);
        byte[] utf8Buf = new byte[4];
        bool utf8Ok = corrupt.TryFormat(utf8Buf, out int utf8Written, default, null);
        bool sizeOk = corrupt.TryGetStringSize(default, null, out int size);

        await Assert.That(charOk).IsFalse();
        await Assert.That(charWritten).IsEqualTo(0);
        await Assert.That(utf8Ok).IsFalse();
        await Assert.That(utf8Written).IsEqualTo(0);
        await Assert.That(sizeOk).IsTrue();
        await Assert.That(size).IsEqualTo(0);
    }

    /// <summary>Strings with equal text in different instances, lazy strings, bytes, bools, and none values with a foreign reference are equal.</summary>
    [Test]
    public async Task FieldValueDataEqualsStringLazyAndScalarSwitchArms()
    {
        // Distinct string instances (avoid interning) so Equals skips the ReferenceEquals fast path.
        string strLeft = string.Concat("al", "pha");
        string strRight = new(['a', 'l', 'p', 'h', 'a']);
        FieldValueData strA = FieldValueData.NewString(strLeft);
        FieldValueData strB = FieldValueData.NewString(strRight);
        FieldValueData lazyA = FieldValueData.NewLazyString(LazyString.Lazy(static () => "lazy-eq"));
        FieldValueData lazyB = FieldValueData.NewLazyString(LazyString.Lazy(static () => "lazy-eq"));
        byte[] bytesLeft = [1, 2];
        byte[] bytesRight = [1, 2];
        FieldValueData bytesA = FieldValueData.NewBytes(bytesLeft);
        FieldValueData bytesB = FieldValueData.NewBytes(bytesRight);
        FieldValueData boolA = FieldValueData.NewBool(true);
        FieldValueData boolB = FieldValueData.NewBool(true);
        FieldValueData noneA = _WithRef(FieldValueData.None, new());
        FieldValueData noneB = _WithRef(FieldValueData.None, new());

        await Assert.That(ReferenceEquals(strLeft, strRight)).IsFalse();
        await Assert.That(strA.Equals(strB)).IsTrue();
        await Assert.That(lazyA.Equals(lazyB)).IsTrue();
        await Assert.That(bytesA.Equals(bytesB)).IsTrue();
        await Assert.That(boolA.Equals(boolB)).IsTrue();
        await Assert.That(noneA.Equals(noneB)).IsTrue();
    }

    /// <summary>Strings and bytes with equal content in different instances are equal, different content is unequal, also through Equals(object).</summary>
    [Test]
    public async Task FieldValueDataEqualsStringAndBytesSwitchArms()
    {
        string unique = Guid.NewGuid().ToString("N");
        string left = unique;
        string right = new string(unique.ToCharArray());
        FieldValueData strLeft = FieldValueData.NewString(left);
        FieldValueData strRight = FieldValueData.NewString(right);
        FieldValueData strOther = FieldValueData.NewString(unique + "x");

        byte[] bytesLeft = [0x10, 0x20, 0x30];
        byte[] bytesRight = (byte[])bytesLeft.Clone();
        FieldValueData bytesLeftVal = FieldValueData.NewBytes(bytesLeft);
        FieldValueData bytesRightVal = FieldValueData.NewBytes(bytesRight);
        FieldValueData bytesOtherVal = FieldValueData.NewBytes([0x10, 0x20, 0x31]);

        await Assert.That(ReferenceEquals(left, right)).IsFalse();
        await Assert.That(ReferenceEquals(bytesLeft, bytesRight)).IsFalse();
        await Assert.That(strLeft.Equals(strRight)).IsTrue();
        await Assert.That(strLeft.Equals(strOther)).IsFalse();
        await Assert.That(bytesLeftVal.Equals(bytesRightVal)).IsTrue();
        await Assert.That(bytesLeftVal.Equals(bytesOtherVal)).IsFalse();
        await Assert.That(strLeft.Equals((object)strRight)).IsTrue();
        await Assert.That(bytesLeftVal.Equals((object)bytesRightVal)).IsTrue();
    }

    /// <summary>The private string extraction gives the empty string for a foreign object and the built text for a lazy string.</summary>
    [Test]
    public async Task FieldValueDataExtractStringPrivatePaths()
    {
        MethodInfo extract = typeof(FieldValueData).GetMethod(
            "_ExtractString",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        string fromObject = (string)extract.Invoke(null, [new()])!;
        FieldValueData lazyVal = FieldValueData.NewLazyString(LazyString.Lazy(static () => "extract-lazy"));
        object boxed = lazyVal;
        object? lazyRef = typeof(FieldValueData).GetField(
            "_Ref",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxed);
        string fromLazy = (string)extract.Invoke(null, [lazyRef])!;

        await Assert.That(fromObject).IsEqualTo(string.Empty);
        await Assert.That(fromLazy).IsEqualTo("extract-lazy");
    }

    /// <summary>UUIDs order bytewise: the all-zero UUID is less than one that ends in 1.</summary>
    [Test]
    public async Task FieldValueDataCompareToUuidArm()
    {
        FieldValueData left = FieldValueData.NewUuid(Uuid.FromBytes(new byte[16]));
        FieldValueData right = FieldValueData.NewUuid(Uuid.FromBytes(
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
        ]));
        int cmp = left.CompareTo(right);
        await Assert.That(cmp).IsLessThan(0);
    }

    /// <summary>Equal IPv6, UUID, U64, and bool values are equal.</summary>
    [Test]
    public async Task FieldValueDataEqualsMarkerTypesUseSwitchArms()
    {
        FieldValueData ipv6A = FieldValueData.NewIPv6(IPv6Address.FromBytes(new byte[16]));
        FieldValueData ipv6B = FieldValueData.NewIPv6(IPv6Address.FromBytes(new byte[16]));
        FieldValueData uuidA = FieldValueData.NewUuid(Uuid.FromBytes(new byte[16]));
        FieldValueData uuidB = FieldValueData.NewUuid(Uuid.FromBytes(new byte[16]));
        FieldValueData u64A = FieldValueData.NewU64(42);
        FieldValueData u64B = FieldValueData.NewU64(42);
        FieldValueData boolA = FieldValueData.NewBool(true);
        FieldValueData boolB = FieldValueData.NewBool(true);

        await Assert.That(ipv6A.Equals(ipv6B)).IsTrue();
        await Assert.That(uuidA.Equals(uuidB)).IsTrue();
        await Assert.That(u64A.Equals(u64B)).IsTrue();
        await Assert.That(boolA.Equals(boolB)).IsTrue();
    }

    /// <summary>Timestamps order by instant: the earlier is less than the later.</summary>
    [Test]
    public async Task FieldValueDataCompareToTimestampArm()
    {
        FieldValueData early = FieldValueData.NewTimestamp(Timestamp.FromSecs(1));
        FieldValueData late = FieldValueData.NewTimestamp(Timestamp.FromSecs(9));
        await Assert.That(early.CompareTo(late)).IsLessThan(0);
        await Assert.That(late.CompareTo(early)).IsGreaterThan(0);
    }

    /// <summary>Formatting the none value into UTF-8 succeeds and writes nothing.</summary>
    [Test]
    public async Task FieldValueDataUtf8TryFormatNoneReturnsEmpty()
    {
        FieldValueData none = FieldValueData.None;
        byte[] buf = new byte[8];
        bool ok = none.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }
}
