// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for MacAddress and IPv4Address value types.
/// </summary>
internal sealed class ValueTypeTests
{
    // === MacAddress ===

    [Test]
    public async Task MacAddress_ConstructionAndRawValue()
    {
        MacAddress mac = new(0x001122334455);
        await Assert.That(mac.RawValue).IsEqualTo(0x001122334455UL);
    }

    [Test]
    public async Task MacAddress_ConstructionMasks48Bits()
    {
        // Upper bits beyond 48 should be masked off
        MacAddress mac = new(0xFFFF_001122334455);
        await Assert.That(mac.RawValue).IsEqualTo(0x001122334455UL);
    }

    [Test]
    public async Task MacAddress_FromBytes()
    {
        byte[] bytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.RawValue).IsEqualTo(0x001122334455UL);
    }

    [Test]
    public async Task MacAddress_FromBytesRoundtrip()
    {
        byte[] original = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        MacAddress mac = MacAddress.FromBytes(original);
        byte[] result = mac.ToBytesArray();
        await Assert.That(result.Length).IsEqualTo(original.Length);
        for (int i = 0; i < original.Length; i++)
        {
            await Assert.That(result[i]).IsEqualTo(original[i]);
        }
    }

    [Test]
    public async Task MacAddress_FromBytesTooShort_Throws()
    {
        byte[] bytes = [0x01, 0x02];
        await Assert.That(() =>
        {
            MacAddress _ = MacAddress.FromBytes(bytes);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
        await Assert.That(MacAddress.TryFromBytes(bytes, out MacAddress mac)).IsFalse();
        await Assert.That(mac).IsEqualTo(default(MacAddress));
    }

    [Test]
    public async Task MacAddress_Format()
    {
        byte[] bytes = [0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E];
        MacAddress mac = MacAddress.FromBytes(bytes);
        string formatted = mac.Format();
        await Assert.That(formatted).IsEqualTo("00:1A:2B:3C:4D:5E");
    }

    [Test]
    public async Task MacAddress_FormatBroadcast()
    {
        byte[] bytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.Format()).IsEqualTo("FF:FF:FF:FF:FF:FF");
    }

    [Test]
    public async Task MacAddress_IsBroadcast()
    {
        byte[] bytes = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsBroadcast).IsTrue();
    }

    [Test]
    public async Task MacAddress_IsNotBroadcast()
    {
        byte[] bytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsBroadcast).IsFalse();
    }

    [Test]
    public async Task MacAddress_IsMulticast()
    {
        // Multicast bit is bit 0 of the first octet (bit 40 in the ulong)
        byte[] bytes = [0x01, 0x00, 0x5E, 0x00, 0x00, 0x01];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsMulticast).IsTrue();
        await Assert.That(mac.IsUnicast).IsFalse();
    }

    [Test]
    public async Task MacAddress_IsUnicast()
    {
        byte[] bytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsUnicast).IsTrue();
        await Assert.That(mac.IsMulticast).IsFalse();
    }

    [Test]
    public async Task MacAddress_IsZero()
    {
        byte[] bytes = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsZero).IsTrue();
    }

    [Test]
    public async Task MacAddress_IsLocal()
    {
        // Local bit is bit 1 of the first octet (bit 41 in the ulong)
        byte[] bytes = [0x02, 0x00, 0x00, 0x00, 0x00, 0x01];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsLocal).IsTrue();
        await Assert.That(mac.IsGlobal).IsFalse();
    }

    [Test]
    public async Task MacAddress_IsGlobal()
    {
        byte[] bytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        MacAddress mac = MacAddress.FromBytes(bytes);
        await Assert.That(mac.IsGlobal).IsTrue();
        await Assert.That(mac.IsLocal).IsFalse();
    }

    [Test]
    public async Task MacAddress_TryParse_Valid()
    {
        bool ok = MacAddress.TryParse("aa:bb:cc:dd:ee:ff", out MacAddress mac);
        await Assert.That(ok).IsTrue();
        await Assert.That(mac.Format()).IsEqualTo("AA:BB:CC:DD:EE:FF");
    }

    [Test]
    public async Task MacAddress_TryParse_Invalid()
    {
        bool ok = MacAddress.TryParse("not-a-mac", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task MacAddress_Equality()
    {
        MacAddress a = new(0x001122334455);
        MacAddress b = new(0x001122334455);
        MacAddress c = new(0xAABBCCDDEEFF);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
    }

    [Test]
    public async Task MacAddress_FormatInto()
    {
        byte[] bytes = [0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45];
        MacAddress mac = MacAddress.FromBytes(bytes);
        char[] buffer = new char[MacAddress.FormattedLength];
        int written = mac.FormatInto(buffer);
        string formatted = new(buffer);
        await Assert.That(written).IsEqualTo(17);
        await Assert.That(formatted).IsEqualTo("AB:CD:EF:01:23:45");
    }

    // === IPv4Address ===

    [Test]
    public async Task IPv4Address_ConstructionAndRawValue()
    {
        IPv4Address ip = new(0xC0A80001); // 192.168.0.1
        await Assert.That(ip.RawValue).IsEqualTo(0xC0A80001u);
    }

    [Test]
    public async Task IPv4Address_FromBytes()
    {
        byte[] bytes = [192, 168, 1, 100];
        IPv4Address ip = IPv4Address.FromBytes(bytes);
        await Assert.That(ip.Format()).IsEqualTo("192.168.1.100");
    }

    [Test]
    public async Task IPv4Address_FromBytesRoundtrip()
    {
        byte[] original = [10, 0, 0, 1];
        IPv4Address ip = IPv4Address.FromBytes(original);
        byte[] result = ip.ToBytesArray();
        await Assert.That(result.Length).IsEqualTo(original.Length);
        for (int i = 0; i < original.Length; i++)
        {
            await Assert.That(result[i]).IsEqualTo(original[i]);
        }
    }

    [Test]
    public async Task IPv4Address_FromBytesTooShort_Throws()
    {
        byte[] bytes = [10, 20];
        await Assert.That(() =>
        {
            IPv4Address _ = IPv4Address.FromBytes(bytes);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
        await Assert.That(IPv4Address.TryFromBytes(bytes, out IPv4Address ip)).IsFalse();
        await Assert.That(ip).IsEqualTo(default(IPv4Address));
    }

    [Test]
    public async Task IPv4Address_Format()
    {
        byte[] bytes = [10, 0, 0, 1];
        IPv4Address ip = IPv4Address.FromBytes(bytes);
        await Assert.That(ip.Format()).IsEqualTo("10.0.0.1");
    }

    [Test]
    public async Task IPv4Address_FormatBroadcast()
    {
        byte[] bytes = [255, 255, 255, 255];
        IPv4Address ip = IPv4Address.FromBytes(bytes);
        await Assert.That(ip.Format()).IsEqualTo("255.255.255.255");
    }

    [Test]
    public async Task IPv4Address_IsPrivate_10Network()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        await Assert.That(ip.IsPrivate).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsPrivate_172_16Network()
    {
        IPv4Address ip = IPv4Address.FromBytes([172, 16, 0, 1]);
        await Assert.That(ip.IsPrivate).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsPrivate_172_31Network()
    {
        IPv4Address ip = IPv4Address.FromBytes([172, 31, 255, 255]);
        await Assert.That(ip.IsPrivate).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsNotPrivate_172_32Network()
    {
        IPv4Address ip = IPv4Address.FromBytes([172, 32, 0, 1]);
        await Assert.That(ip.IsPrivate).IsFalse();
    }

    [Test]
    public async Task IPv4Address_IsPrivate_192_168Network()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        await Assert.That(ip.IsPrivate).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsNotPrivate_PublicAddress()
    {
        IPv4Address ip = IPv4Address.FromBytes([8, 8, 8, 8]);
        await Assert.That(ip.IsPrivate).IsFalse();
    }

    [Test]
    public async Task IPv4Address_IsLoopback()
    {
        IPv4Address ip = IPv4Address.FromBytes([127, 0, 0, 1]);
        await Assert.That(ip.IsLoopback).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsLoopback_AnyInRange()
    {
        IPv4Address ip = IPv4Address.FromBytes([127, 255, 255, 255]);
        await Assert.That(ip.IsLoopback).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsNotLoopback()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        await Assert.That(ip.IsLoopback).IsFalse();
    }

    [Test]
    public async Task IPv4Address_IsMulticast()
    {
        IPv4Address ip = IPv4Address.FromBytes([224, 0, 0, 1]);
        await Assert.That(ip.IsMulticast).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsMulticast_UpperRange()
    {
        IPv4Address ip = IPv4Address.FromBytes([239, 255, 255, 255]);
        await Assert.That(ip.IsMulticast).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsNotMulticast()
    {
        IPv4Address ip = IPv4Address.FromBytes([240, 0, 0, 1]);
        await Assert.That(ip.IsMulticast).IsFalse();
    }

    [Test]
    public async Task IPv4Address_IsBroadcast()
    {
        IPv4Address ip = IPv4Address.FromBytes([255, 255, 255, 255]);
        await Assert.That(ip.IsBroadcast).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsNotBroadcast()
    {
        IPv4Address ip = IPv4Address.FromBytes([255, 255, 255, 254]);
        await Assert.That(ip.IsBroadcast).IsFalse();
    }

    [Test]
    public async Task IPv4Address_IsZero()
    {
        IPv4Address ip = IPv4Address.FromBytes([0, 0, 0, 0]);
        await Assert.That(ip.IsZero).IsTrue();
    }

    [Test]
    public async Task IPv4Address_IsLinkLocal()
    {
        IPv4Address ip = IPv4Address.FromBytes([169, 254, 1, 1]);
        await Assert.That(ip.IsLinkLocal).IsTrue();
    }

    [Test]
    public async Task IPv4Address_TryParse_Valid()
    {
        bool ok = IPv4Address.TryParse("192.168.1.1", out IPv4Address ip);
        await Assert.That(ok).IsTrue();
        await Assert.That(ip.Format()).IsEqualTo("192.168.1.1");
    }

    [Test]
    public async Task IPv4Address_TryParse_Invalid()
    {
        bool ok = IPv4Address.TryParse("999.999.999.999", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task IPv4Address_TryParse_NotAnAddress()
    {
        bool ok = IPv4Address.TryParse("not-an-ip", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task IPv4Address_Equality()
    {
        IPv4Address a = new(0xC0A80001);
        IPv4Address b = new(0xC0A80001);
        IPv4Address c = new(0x08080808);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task IPv4Address_CompareToOrdering()
    {
        IPv4Address low = new(0x0A000001); // 10.0.0.1
        IPv4Address high = new(0xC0A80001); // 192.168.0.1
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
    }

    [Test]
    public async Task IPv4Address_ComparisonOperators()
    {
        IPv4Address a = new(0x0A000001);
        IPv4Address b = new(0xC0A80001);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
    }

    [Test]
    public async Task IPv4Address_FormatInto()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        char[] buffer = new char[IPv4Address.MaxFormattedLength];
        int written = ip.FormatInto(buffer);
        string formatted = new(buffer, 0, written);
        await Assert.That(formatted).IsEqualTo("192.168.1.1");
    }

    // === Timestamp ===

    [Test]
    public async Task Timestamp_Format_UnixEpoch()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        await Assert.That(ts.Format()).IsEqualTo("1970-01-01T00:00:00.000000000Z");
    }

    [Test]
    public async Task Timestamp_Format_WithNanos()
    {
        // 2024-03-26T16:00:00.123456789Z
        Timestamp ts = Timestamp.FromSecsAndNanos(1711468800, 123456789);
        await Assert.That(ts.Format()).IsEqualTo("2024-03-26T16:00:00.123456789Z");
    }

    [Test]
    public async Task Timestamp_Format_BeforeEpoch()
    {
        // 1969-12-31T23:59:59.000000000Z
        Timestamp ts = Timestamp.FromSecs(-1);
        await Assert.That(ts.Format()).IsEqualTo("1969-12-31T23:59:59.000000000Z");
    }

    [Test]
    public async Task Timestamp_Format_BeforeEpochWithNanos()
    {
        // -0.5s = 1969-12-31T23:59:59.500000000Z
        Timestamp ts = Timestamp.FromNanos(-500_000_000);
        await Assert.That(ts.Format()).IsEqualTo("1969-12-31T23:59:59.500000000Z");
    }

    [Test]
    public async Task Timestamp_FormatInto()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        char[] buffer = new char[Timestamp.MaxFormattedLength];
        int written = ts.FormatInto(buffer);
        string formatted = new(buffer, 0, written);
        await Assert.That(formatted).IsEqualTo("1970-01-01T00:00:00.000000000Z");
        await Assert.That(written).IsEqualTo(30);
    }

    [Test]
    public async Task Timestamp_TryGetStringSize_IsAlways30()
    {
        Timestamp ts = Timestamp.FromSecs(1_000_000);
        bool success = ts.TryGetStringSize(default, null, out int size);
        await Assert.That(success).IsTrue();
        await Assert.That(size).IsEqualTo(30);
    }

    // === MacAddress extended coverage ===

    [Test]
    public async Task MacAddress_TryGetWrittenSize()
    {
        MacAddress mac = new(0x001122334455);
        bool ok = mac.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(6);
    }

    [Test]
    public async Task MacAddress_TryWrite_Success()
    {
        MacAddress mac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        byte[] buf = new byte[6];
        bool ok = mac.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(6);
        await Assert.That(buf[0]).IsEqualTo((byte)0xAA);
        await Assert.That(buf[5]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task MacAddress_TryWrite_TooShort()
    {
        MacAddress mac = new(1);
        byte[] buf = new byte[3];
        bool ok = mac.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task MacAddress_TryFormatUtf8()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E]);
        byte[] buf = new byte[MacAddress.FormattedLength];
        bool ok = mac.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        string str = System.Text.Encoding.ASCII.GetString(buf, 0, written);
        await Assert.That(str).IsEqualTo("00:1A:2B:3C:4D:5E");
    }

    [Test]
    public async Task MacAddress_TryFormatUtf8_TooShort()
    {
        MacAddress mac = new(1);
        byte[] buf = new byte[5];
        bool ok = mac.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task MacAddress_TryFormat_TooShort()
    {
        MacAddress mac = new(1);
        char[] buf = new char[5];
        bool ok = mac.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task MacAddress_FormatTemp()
    {
        MacAddress mac = MacAddress.FromBytes([0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45]);
        TempString temp = mac.FormatTemp();
        await Assert.That(temp.ToString()).IsEqualTo("AB:CD:EF:01:23:45");
    }

    [Test]
    public async Task MacAddress_TryGetStringSize()
    {
        MacAddress mac = new(1);
        bool ok = mac.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(MacAddress.FormattedLength);
    }

    [Test]
    public async Task MacAddress_ToString_FormatProvider()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        await Assert.That(mac.ToString(null, null)).IsEqualTo(mac.Format());
    }

    [Test]
    public async Task MacAddress_ToString_MatchesFormat()
    {
        MacAddress mac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        await Assert.That(mac.ToString()).IsEqualTo(mac.Format());
    }

    [Test]
    public async Task MacAddress_ComparisonOperators()
    {
        MacAddress a = new(1);
        MacAddress b = new(100);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
    }

    [Test]
    public async Task MacAddress_Equality_Operators()
    {
        MacAddress a = new(0x001122334455);
        MacAddress b = new(0x001122334455);
        MacAddress c = new(0xAABBCCDDEEFF);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task MacAddress_GetHashCode_EqualObjectsSameHash()
    {
        MacAddress a = new(0x001122334455);
        MacAddress b = new(0x001122334455);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task MacAddress_CompareTo()
    {
        MacAddress a = new(1);
        MacAddress b = new(100);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
        await Assert.That(a.CompareTo(a)).IsEqualTo(0);
    }

    [Test]
    public async Task MacAddress_Equals_ObjectBoxing()
    {
        MacAddress a = new(0x001122334455);
        object b = new MacAddress(0x001122334455);
        object other = "not a mac";
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(other)).IsFalse();
    }

    [Test]
    public async Task MacAddress_ToBytes_TooShort_ReturnsZero()
    {
        MacAddress mac = new(1);
        byte[] buf = new byte[3];
        int written = mac.ToBytes(buf);
        await Assert.That(written).IsEqualTo(0);
    }

    // === IPv4Address extended coverage ===

    [Test]
    public async Task IPv4Address_TryGetWrittenSize()
    {
        IPv4Address ip = new(0xC0A80001);
        bool ok = ip.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(4);
    }

    [Test]
    public async Task IPv4Address_TryWrite_Success()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        byte[] buf = new byte[4];
        bool ok = ip.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(4);
        await Assert.That(buf[0]).IsEqualTo((byte)192);
        await Assert.That(buf[3]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task IPv4Address_TryWrite_TooShort()
    {
        IPv4Address ip = new(1);
        byte[] buf = new byte[2];
        bool ok = ip.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task IPv4Address_TryFormatUtf8()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        byte[] buf = new byte[IPv4Address.MaxFormattedLength];
        bool ok = ip.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        string str = System.Text.Encoding.ASCII.GetString(buf, 0, written);
        await Assert.That(str).IsEqualTo("192.168.1.1");
    }

    [Test]
    public async Task IPv4Address_TryFormatUtf8_TooShort()
    {
        IPv4Address ip = new(0xC0A80101);
        byte[] buf = new byte[3];
        bool ok = ip.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task IPv4Address_TryFormat_TooShort()
    {
        IPv4Address ip = new(0xC0A80101);
        char[] buf = new char[5];
        bool ok = ip.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task IPv4Address_FormatTemp()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        TempString temp = ip.FormatTemp();
        await Assert.That(temp.ToString()).IsEqualTo("10.0.0.1");
    }

    [Test]
    public async Task IPv4Address_TryGetStringSize()
    {
        IPv4Address ip = IPv4Address.FromBytes([192, 168, 1, 1]);
        bool ok = ip.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        // Size should match actual formatted length
        await Assert.That(size).IsEqualTo(ip.Format().Length);
    }

    [Test]
    public async Task IPv4Address_ToString_MatchesFormat()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        await Assert.That(ip.ToString()).IsEqualTo(ip.Format());
    }

    [Test]
    public async Task IPv4Address_ToString_FormatProvider()
    {
        IPv4Address ip = IPv4Address.FromBytes([10, 0, 0, 1]);
        await Assert.That(ip.ToString(null, null)).IsEqualTo(ip.Format());
    }

    [Test]
    public async Task IPv4Address_Equality_Operators()
    {
        IPv4Address a = new(0xC0A80001);
        IPv4Address b = new(0xC0A80001);
        IPv4Address c = new(0x08080808);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task IPv4Address_Equals_ObjectBoxing()
    {
        IPv4Address a = new(0xC0A80001);
        object b = new IPv4Address(0xC0A80001);
        object other = "not an ip";
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(other)).IsFalse();
    }

    [Test]
    public async Task IPv4Address_GetHashCode_EqualObjectsSameHash()
    {
        IPv4Address a = new(0xC0A80001);
        IPv4Address b = new(0xC0A80001);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task IPv4Address_ToBytes_TooShort_ReturnsZero()
    {
        IPv4Address ip = new(0xC0A80001);
        byte[] buf = new byte[2];
        int written = ip.ToBytes(buf);
        await Assert.That(written).IsEqualTo(0);
    }

    // === Timestamp extended coverage ===

    [Test]
    public async Task Timestamp_Now_IsReasonable()
    {
        Timestamp now = Timestamp.Now;
        // Should be after 2020-01-01
        Timestamp baseline = Timestamp.FromSecs(1577836800);
        await Assert.That(now.AsNanos).IsGreaterThan(baseline.AsNanos);
    }

    [Test]
    public async Task Timestamp_AsMicros()
    {
        Timestamp ts = Timestamp.FromMicros(12345);
        await Assert.That(ts.AsMicros).IsEqualTo(12345L);
    }

    [Test]
    public async Task Timestamp_AsMillis()
    {
        Timestamp ts = Timestamp.FromMillis(999);
        await Assert.That(ts.AsMillis).IsEqualTo(999L);
    }

    [Test]
    public async Task Timestamp_Secs()
    {
        Timestamp ts = Timestamp.FromSecsAndNanos(42, 500_000_000);
        await Assert.That(ts.Secs).IsEqualTo(42L);
    }

    [Test]
    public async Task Timestamp_SubsecNanos()
    {
        Timestamp ts = Timestamp.FromSecsAndNanos(42, 123456789);
        await Assert.That(ts.SubsecNanos).IsEqualTo(123456789);
    }

    [Test]
    public async Task Timestamp_TryGetWrittenSize()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        bool ok = ts.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(8);
    }

    [Test]
    public async Task Timestamp_TryWrite_Success()
    {
        Timestamp ts = Timestamp.FromSecs(1);
        byte[] buf = new byte[8];
        bool ok = ts.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(8);
        // nanos = 1_000_000_000 in big-endian
        long readBack = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(buf);
        await Assert.That(readBack).IsEqualTo(1_000_000_000L);
    }

    [Test]
    public async Task Timestamp_TryWrite_TooShort()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        byte[] buf = new byte[4];
        bool ok = ts.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task Timestamp_TryFormatUtf8()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        byte[] buf = new byte[Timestamp.MaxFormattedLength];
        bool ok = ts.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        string str = System.Text.Encoding.ASCII.GetString(buf, 0, written);
        await Assert.That(str).IsEqualTo("1970-01-01T00:00:00.000000000Z");
    }

    [Test]
    public async Task Timestamp_TryFormatUtf8_TooShort()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        byte[] buf = new byte[10];
        bool ok = ts.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task Timestamp_TryFormat_TooShort()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        char[] buf = new char[10];
        bool ok = ts.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task Timestamp_FormatTemp()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        TempString temp = ts.FormatTemp();
        await Assert.That(temp.ToString()).IsEqualTo("1970-01-01T00:00:00.000000000Z");
    }

    [Test]
    public async Task Timestamp_ToString_MatchesFormat()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        await Assert.That(ts.ToString()).IsEqualTo(ts.Format());
    }

    [Test]
    public async Task Timestamp_ToString_FormatProvider()
    {
        Timestamp ts = Timestamp.FromSecs(0);
        await Assert.That(ts.ToString(null, null)).IsEqualTo(ts.Format());
    }

    [Test]
    public async Task Timestamp_Equality()
    {
        Timestamp a = Timestamp.FromSecs(100);
        Timestamp b = Timestamp.FromSecs(100);
        Timestamp c = Timestamp.FromSecs(200);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task Timestamp_Equality_ObjectBoxing()
    {
        Timestamp a = Timestamp.FromSecs(100);
        object b = Timestamp.FromSecs(100);
        object other = "not a timestamp";
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(other)).IsFalse();
    }

    [Test]
    public async Task Timestamp_GetHashCode_EqualObjectsSameHash()
    {
        Timestamp a = Timestamp.FromSecs(100);
        Timestamp b = Timestamp.FromSecs(100);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Timestamp_CompareTo()
    {
        Timestamp a = Timestamp.FromSecs(1);
        Timestamp b = Timestamp.FromSecs(2);
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
        await Assert.That(a.CompareTo(a)).IsEqualTo(0);
    }

    [Test]
    public async Task Timestamp_ComparisonOperators()
    {
        Timestamp a = Timestamp.FromSecs(1);
        Timestamp b = Timestamp.FromSecs(2);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
        Timestamp aCopy = a;
        await Assert.That(a <= aCopy).IsTrue();
        await Assert.That(a >= aCopy).IsTrue();
    }

    [Test]
    public async Task Timestamp_Arithmetic_AddTimeSpan()
    {
        Timestamp ts = Timestamp.FromSecs(100);
        Timestamp result = ts + TimeSpan.FromSeconds(50);
        await Assert.That(result.Secs).IsEqualTo(150L);
    }

    [Test]
    public async Task Timestamp_Arithmetic_SubtractTimeSpan()
    {
        Timestamp ts = Timestamp.FromSecs(100);
        Timestamp result = ts - TimeSpan.FromSeconds(50);
        await Assert.That(result.Secs).IsEqualTo(50L);
    }

    [Test]
    public async Task Timestamp_Arithmetic_DifferenceBetweenTimestamps()
    {
        Timestamp a = Timestamp.FromSecs(200);
        Timestamp b = Timestamp.FromSecs(100);
        TimeSpan diff = a - b;
        await Assert.That(diff.TotalSeconds).IsEqualTo(100.0);
    }

    [Test]
    public async Task Timestamp_FromFactories_Roundtrip()
    {
        Timestamp fromNanos = Timestamp.FromNanos(5_000_000_000);
        Timestamp fromMicros = Timestamp.FromMicros(5_000_000);
        Timestamp fromMillis = Timestamp.FromMillis(5_000);
        Timestamp fromSecs = Timestamp.FromSecs(5);

        await Assert.That(fromNanos.Secs).IsEqualTo(5L);
        await Assert.That(fromMicros.Secs).IsEqualTo(5L);
        await Assert.That(fromMillis.Secs).IsEqualTo(5L);
        await Assert.That(fromSecs.Secs).IsEqualTo(5L);
    }
}
