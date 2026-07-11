// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// Tests for <see cref="IPv6Address"/>: construction, formatting, parsing (with :: compression),
/// binary serialization, equality, comparison, and RFC 5952 edge cases.
/// </summary>
internal sealed class IPv6AddressTests
{
    // === Construction ===

    [Test]
    public async Task Constructor_StoresHighLow()
    {
        IPv6Address addr = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        await Assert.That(addr.High).IsEqualTo(0x0102030405060708UL);
        await Assert.That(addr.Low).IsEqualTo(0x090A0B0C0D0E0F10UL);
    }

    [Test]
    public async Task Default_IsAllZeros()
    {
        IPv6Address addr = default;
        await Assert.That(addr.High).IsEqualTo(0UL);
        await Assert.That(addr.Low).IsEqualTo(0UL);
    }

    // === Parsing ===

    [Test]
    [Arguments("2001:db8::1", "2001:DB8::1")]
    [Arguments("::1", "::1")]
    [Arguments("::", "::")]
    [Arguments("fe80::1", "FE80::1")]
    [Arguments("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:DB8::1")]
    [Arguments("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "FFFF:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF")]
    public async Task TryParse_ValidAddresses_RoundTrip(string input, string expectedFormatted)
    {
        await Assert.That(IPv6Address.TryParse(input, out IPv6Address addr)).IsTrue();
        await Assert.That(addr.Format()).IsEqualTo(expectedFormatted);
    }

    [Test]
    [Arguments("")]
    [Arguments("not valid")]
    [Arguments("2001:db8::1::2")]             // double ::
    [Arguments("2001:db8:0:0:0:0:0:0:1")]    // too many groups
    [Arguments("gggg::1")]                     // invalid hex
    public async Task TryParse_InvalidAddresses_ReturnsFalse(string input)
    {
        bool ok = IPv6Address.TryParse(input, out IPv6Address addr);
        await Assert.That(ok).IsFalse();
        await Assert.That(addr).IsEqualTo(default(IPv6Address));
    }

    [Test]
    public async Task TryParse_Loopback()
    {
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address addr)).IsTrue();
        await Assert.That(addr.High).IsEqualTo(0UL);
        await Assert.That(addr.Low).IsEqualTo(1UL);
    }

    [Test]
    public async Task TryParse_AllZeros() =>
        await Assert.That(IPv6Address.TryParse("::", out IPv6Address addr) && addr == default).IsTrue();

    // === FromBytes ===

    [Test]
    public async Task FromBytes_ReadsCorrectly()
    {
        byte[] bytes =
        [
            0x20, 0x01, 0x0d, 0xb8, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
        ];
        IPv6Address addr = IPv6Address.FromBytes(bytes);
        await Assert.That(IPv6Address.TryParse("2001:db8::1", out IPv6Address expected)).IsTrue();
        await Assert.That(addr).IsEqualTo(expected);
    }

    // === GetGroups ===

    [Test]
    public async Task GetGroups_WritesAll8Groups()
    {
        await Assert.That(IPv6Address.TryParse("2001:db8::1", out IPv6Address addr)).IsTrue();
        ushort[] groups = new ushort[8];
        addr.GetGroups(groups);
        await Assert.That(groups[0]).IsEqualTo((ushort)0x2001);
        await Assert.That(groups[1]).IsEqualTo((ushort)0x0db8);
        await Assert.That(groups[7]).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task TryGetGroups_Succeeds()
    {
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address addr)).IsTrue();
        ushort[] buf = new ushort[8];
        bool ok = addr.TryGetGroups(buf);
        await Assert.That(ok).IsTrue();
        await Assert.That(buf[7]).IsEqualTo((ushort)1);
        for (int i = 0; i < 7; i++)
        {
            await Assert.That(buf[i]).IsEqualTo((ushort)0);
        }
    }

    [Test]
    public async Task TryGetGroups_TooSmallBuffer_ReturnsFalse()
    {
        IPv6Address addr = default;
        ushort[] buf = new ushort[7];
        bool ok = addr.TryGetGroups(buf);
        await Assert.That(ok).IsFalse();
    }

    // === Formatting ===

    [Test]
    public async Task Format_Loopback()
    {
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address addr)).IsTrue();
        await Assert.That(addr.Format()).IsEqualTo("::1");
    }

    [Test]
    public async Task Format_AllZeros() =>
        await Assert.That(default(IPv6Address).Format()).IsEqualTo("::");

    [Test]
    public async Task TryFormat_BufferTooSmall_ReturnsFalse()
    {
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address addr)).IsTrue();
        char[] buf = new char[2];
        bool ok = addr.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatTemp_ProducesCorrectString()
    {
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address addr)).IsTrue();
        string formatted;
        using (TempString temp = addr.FormatTemp())
        {
            formatted = temp.ToString();
        }
        await Assert.That(formatted).IsEqualTo("::1");
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality_SameValue_AreEqual()
    {
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address a)).IsTrue();
        await Assert.That(IPv6Address.TryParse("::1", out IPv6Address b)).IsTrue();
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task CompareTo_Ordering_ByHighFirst()
    {
        IPv6Address lo = new(0UL, 1UL);
        IPv6Address hi = new(1UL, 0UL);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
        await Assert.That(hi.CompareTo(lo)).IsGreaterThan(0);
        await Assert.That(lo < hi).IsTrue();
    }

    [Test]
    public async Task CompareTo_SameHigh_OrdersByLow()
    {
        IPv6Address lo = new(1UL, 0UL);
        IPv6Address hi = new(1UL, 1UL);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
    }

    [Test]
    public async Task IComparable_CompareTo_Null_Returns1()
    {
        IComparable addr = new IPv6Address(0UL, 1UL);
        await Assert.That(addr.CompareTo(null)).IsEqualTo(1);
    }

    [Test]
    public async Task IComparable_CompareTo_WrongType_Throws()
    {
        IComparable addr = new IPv6Address(0UL, 1UL);
        await Assert.That(() => addr.CompareTo(42)).Throws<ArgumentException>();
    }

    // === Binary Serialization ===

    [Test]
    public async Task TryGetWrittenSize_Is16()
    {
        IPv6Address addr = default;
        bool ok = addr.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(16);
    }

    [Test]
    public async Task ToBytes_FromBytes_RoundTrip()
    {
        await Assert.That(IPv6Address.TryParse("2001:db8::1", out IPv6Address original)).IsTrue();
        byte[] buf = new byte[16];
        original.ToBytes(buf);
        IPv6Address restored = IPv6Address.FromBytes(buf);
        await Assert.That(restored).IsEqualTo(original);
    }
}
