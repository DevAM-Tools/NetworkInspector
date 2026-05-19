// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="IPv6Address"/>: construction, formatting, binary serialization,
/// classification properties, equality, comparison, and edge cases.
/// </summary>
internal sealed class IPv6AddressTests
{
    // === Construction ===

    [Test]
    public async Task Construction_HighLow()
    {
        IPv6Address ip = new(0x2001_0DB8_0000_0001, 0x0000_0000_0000_0001);
        await Assert.That(ip.High).IsEqualTo(0x2001_0DB8_0000_0001UL);
        await Assert.That(ip.Low).IsEqualTo(0x0000_0000_0000_0001UL);
    }

    [Test]
    public async Task Default_IsAllZeros()
    {
        IPv6Address ip = default;
        await Assert.That(ip.High).IsEqualTo(0UL);
        await Assert.That(ip.Low).IsEqualTo(0UL);
    }

    // === Factory ===

    [Test]
    public async Task FromBytes_16Bytes()
    {
        byte[] bytes = [0x20, 0x01, 0x0D, 0xB8, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01];
        IPv6Address ip = IPv6Address.FromBytes(bytes);
        await Assert.That(ip.High).IsEqualTo(0x20010DB800000000UL);
        await Assert.That(ip.Low).IsEqualTo(1UL);
    }

    [Test]
    public async Task FromBytes_TooShort_ReturnsDefault()
    {
        byte[] bytes = [0x20, 0x01, 0x0D];
        IPv6Address ip = IPv6Address.FromBytes(bytes);
        await Assert.That(ip.High).IsEqualTo(0UL);
        await Assert.That(ip.Low).IsEqualTo(0UL);
    }

    // === Binary roundtrip ===

    [Test]
    public async Task ToBytesRoundtrip()
    {
        byte[] original = [0xFE, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                           0x02, 0x1A, 0x2B, 0xFF, 0xFE, 0x3C, 0x4D, 0x5E];
        IPv6Address ip = IPv6Address.FromBytes(original);
        byte[] rt = ip.ToBytesArray();
        await Assert.That(rt.Length).IsEqualTo(16);
        for (int i = 0; i < 16; i++)
        {
            await Assert.That(rt[i]).IsEqualTo(original[i]);
        }
    }

    [Test]
    public async Task ToBytes_TooShortDestination_ReturnsZero()
    {
        IPv6Address ip = new(1, 2);
        Span<byte> buf = stackalloc byte[10];
        int written = ip.ToBytes(buf);
        await Assert.That(written).IsEqualTo(0);
    }

    // === IBinarySerializable ===

    [Test]
    public async Task TryGetSerializedSize()
    {
        IPv6Address ip = new(1, 2);
        bool ok = ip.TryGetSerializedSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(16);
    }

    [Test]
    public async Task TryWrite_Success()
    {
        IPv6Address ip = new(0x0102030405060708, 0x090A0B0C0D0E0F10);
        byte[] buf = new byte[16];
        bool ok = ip.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(16);
        await Assert.That(buf[0]).IsEqualTo((byte)0x01);
        await Assert.That(buf[15]).IsEqualTo((byte)0x10);
    }

    [Test]
    public async Task TryWrite_TooShort()
    {
        IPv6Address ip = new(1, 2);
        byte[] buf = new byte[5];
        bool ok = ip.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === GetGroups ===

    [Test]
    public async Task GetGroups()
    {
        IPv6Address ip = new(0x2001_0DB8_85A3_0000, 0x0000_8A2E_0370_7334);
        ushort[] groups = new ushort[8];
        ip.GetGroups(groups);
        await Assert.That(groups[0]).IsEqualTo((ushort)0x2001);
        await Assert.That(groups[1]).IsEqualTo((ushort)0x0DB8);
        await Assert.That(groups[2]).IsEqualTo((ushort)0x85A3);
        await Assert.That(groups[3]).IsEqualTo((ushort)0x0000);
        await Assert.That(groups[4]).IsEqualTo((ushort)0x0000);
        await Assert.That(groups[5]).IsEqualTo((ushort)0x8A2E);
        await Assert.That(groups[6]).IsEqualTo((ushort)0x0370);
        await Assert.That(groups[7]).IsEqualTo((ushort)0x7334);
    }

    [Test]
    public async Task GetGroups_TooSmall_Throws()
    {
        IPv6Address ip = new(1, 2);
        ushort[] small = new ushort[3];
        await Assert.That(() => ip.GetGroups(small)).Throws<ArgumentException>();
    }

    // === Classification ===

    [Test]
    public async Task IsLoopback()
    {
        IPv6Address ip = new(0, 1);
        await Assert.That(ip.IsLoopback).IsTrue();
    }

    [Test]
    public async Task IsNotLoopback()
    {
        IPv6Address ip = new(0, 2);
        await Assert.That(ip.IsLoopback).IsFalse();
    }

    [Test]
    public async Task IsMulticast()
    {
        // ff02::1
        IPv6Address ip = new(0xFF02_0000_0000_0000, 1);
        await Assert.That(ip.IsMulticast).IsTrue();
    }

    [Test]
    public async Task IsNotMulticast()
    {
        IPv6Address ip = new(0x2001_0DB8_0000_0000, 0);
        await Assert.That(ip.IsMulticast).IsFalse();
    }

    [Test]
    public async Task IsLinkLocal()
    {
        // fe80::1
        IPv6Address ip = new(0xFE80_0000_0000_0000, 1);
        await Assert.That(ip.IsLinkLocal).IsTrue();
    }

    [Test]
    public async Task IsNotLinkLocal()
    {
        IPv6Address ip = new(0x2001_0DB8_0000_0000, 0);
        await Assert.That(ip.IsLinkLocal).IsFalse();
    }

    [Test]
    public async Task IsUniqueLocal()
    {
        // fd00::1
        IPv6Address ip = new(0xFD00_0000_0000_0000, 1);
        await Assert.That(ip.IsUniqueLocal).IsTrue();
    }

    [Test]
    public async Task IsNotUniqueLocal()
    {
        IPv6Address ip = new(0x2001_0DB8_0000_0000, 0);
        await Assert.That(ip.IsUniqueLocal).IsFalse();
    }

    [Test]
    public async Task IsUnspecified()
    {
        IPv6Address ip = new(0, 0);
        await Assert.That(ip.IsUnspecified).IsTrue();
    }

    [Test]
    public async Task IsNotUnspecified()
    {
        IPv6Address ip = new(0, 1);
        await Assert.That(ip.IsUnspecified).IsFalse();
    }

    [Test]
    public async Task IsIPv4Mapped()
    {
        // ::ffff:192.0.2.1  → high=0, low=0x0000FFFFC0000201
        IPv6Address ip = new(0, 0x0000_FFFF_C000_0201);
        await Assert.That(ip.IsIPv4Mapped).IsTrue();
    }

    [Test]
    public async Task IsNotIPv4Mapped()
    {
        IPv6Address ip = new(0, 1);
        await Assert.That(ip.IsIPv4Mapped).IsFalse();
    }

    // === Formatting ===

    [Test]
    public async Task Format_Loopback()
    {
        IPv6Address ip = new(0, 1);
        await Assert.That(ip.Format()).IsEqualTo("::1");
    }

    [Test]
    public async Task Format_Unspecified()
    {
        IPv6Address ip = new(0, 0);
        await Assert.That(ip.Format()).IsEqualTo("::");
    }

    [Test]
    public async Task Format_FullAddress()
    {
        IPv6Address ip = new(0x2001_0DB8_85A3_0000, 0x0000_8A2E_0370_7334);
        string formatted = ip.Format();
        await Assert.That(formatted).IsEqualTo("2001:DB8:85A3::8A2E:370:7334");
    }

    [Test]
    public async Task Format_NoCompression()
    {
        // All groups non-zero — no "::" compression needed
        IPv6Address ip = new(0x0001_0002_0003_0004, 0x0005_0006_0007_0008);
        string formatted = ip.Format();
        await Assert.That(formatted).IsEqualTo("1:2:3:4:5:6:7:8");
    }

    [Test]
    public async Task FormatInto()
    {
        IPv6Address ip = new(0, 1);
        char[] buffer = new char[IPv6Address.MaxFormattedLength];
        int written = ip.FormatInto(buffer);
        string formatted = new(buffer, 0, written);
        await Assert.That(formatted).IsEqualTo("::1");
    }

    [Test]
    public async Task FormatTemp()
    {
        IPv6Address ip = new(0, 1);
        TempString temp = ip.FormatTemp();
        await Assert.That(temp.ToString()).IsEqualTo("::1");
    }

    [Test]
    public async Task ToString_MatchesFormat()
    {
        IPv6Address ip = new(0, 1);
        await Assert.That(ip.ToString()).IsEqualTo(ip.Format());
    }

    [Test]
    public async Task ToString_FormatProvider()
    {
        IPv6Address ip = new(0, 1);
        await Assert.That(ip.ToString(null, null)).IsEqualTo(ip.Format());
    }

    // === TryFormat (char) ===

    [Test]
    public async Task TryFormat_TooShort()
    {
        IPv6Address ip = new(0, 1);
        char[] small = new char[3];
        bool ok = ip.TryFormat(small, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === TryFormat (UTF-8) ===

    [Test]
    public async Task TryFormatUtf8()
    {
        IPv6Address ip = new(0, 1);
        byte[] buf = new byte[IPv6Address.MaxFormattedLength];
        bool ok = ip.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        string str = System.Text.Encoding.ASCII.GetString(buf, 0, written);
        await Assert.That(str).IsEqualTo("::1");
    }

    [Test]
    public async Task TryFormatUtf8_TooShort()
    {
        IPv6Address ip = new(0, 1);
        byte[] small = new byte[2];
        bool ok = ip.TryFormat(small, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === IStringSize ===

    [Test]
    public async Task TryGetStringSize()
    {
        IPv6Address ip = new(0, 1);
        bool ok = ip.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        // Size should match actual formatted length
        string formatted = ip.Format();
        await Assert.That(size).IsEqualTo(formatted.Length);
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality()
    {
        IPv6Address a = new(1, 2);
        IPv6Address b = new(1, 2);
        IPv6Address c = new(3, 4);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task Equality_ObjectBoxing()
    {
        IPv6Address a = new(1, 2);
        object b = new IPv6Address(1, 2);
        object other = "not an ipv6";
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(other)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_EqualObjectsSameHash()
    {
        IPv6Address a = new(1, 2);
        IPv6Address b = new(1, 2);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task CompareTo()
    {
        IPv6Address low = new(0, 1);
        IPv6Address high = new(1, 0);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
    }

    [Test]
    public async Task ComparisonOperators()
    {
        IPv6Address a = new(0, 1);
        IPv6Address b = new(1, 0);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
        IPv6Address aCopy = a;
        await Assert.That(a <= aCopy).IsTrue();
        await Assert.That(a >= aCopy).IsTrue();
    }
}