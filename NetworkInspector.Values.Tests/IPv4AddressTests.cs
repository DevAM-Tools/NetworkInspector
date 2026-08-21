// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// Tests for <see cref="IPv4Address"/>: construction, formatting, parsing, binary serialization,
/// classification, equality, comparison, and edge cases.
/// </summary>
internal sealed class IPv4AddressTests
{
    // === Construction ===

    [Test]
    public async Task Constructor_StoresRawValue()
    {
        IPv4Address addr = new(0xC0A80101u); // 192.168.1.1
        await Assert.That(addr.RawValue).IsEqualTo(0xC0A80101u);
    }

    [Test]
    public async Task Default_IsAllZeros()
    {
        IPv4Address addr = default;
        await Assert.That(addr.RawValue).IsEqualTo(0u);
    }

    // === Factory Methods ===

    [Test]
    [Arguments("1.2.3.4", 0x01020304u)]
    [Arguments("0.0.0.0", 0u)]
    [Arguments("255.255.255.255", 0xFFFFFFFFu)]
    [Arguments("192.168.1.1", 0xC0A80101u)]
    public async Task TryParse_ValidAddresses(string input, uint expectedValue)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.RawValue).IsEqualTo(expectedValue);
    }

    [Test]
    [Arguments("")]
    [Arguments("256.0.0.1")]
    [Arguments("1.2.3")]
    [Arguments("1.2.3.4.5")]
    [Arguments("not an ip")]
    [Arguments("1.2.3.300")]
    [Arguments("-1.0.0.0")]
    public async Task TryParse_InvalidAddresses_ReturnsFalse(string input)
    {
        bool result = IPv4Address.TryParse(input, out IPv4Address addr);
        await Assert.That(result).IsFalse();
        await Assert.That(addr).IsEqualTo(default(IPv4Address));
    }

    [Test]
    public async Task FromBytes_BigEndian()
    {
        byte[] bytes = [192, 168, 1, 1];
        IPv4Address addr = IPv4Address.FromBytes(bytes);
        await Assert.That(addr.RawValue).IsEqualTo(0xC0A80101u);
    }

    [Test]
    public async Task FromBytes_ExtraBytes_Ignored()
    {
        byte[] bytes = [1, 2, 3, 4, 99]; // extra bytes ignored
        IPv4Address addr = IPv4Address.FromBytes(bytes);
        await Assert.That(addr.RawValue).IsEqualTo(0x01020304u);
    }

    [Test]
    public async Task TryFromBytes_ShortSpan_ReturnsFalse()
    {
        await Assert.That(IPv4Address.TryFromBytes(ReadOnlySpan<byte>.Empty, out IPv4Address address)).IsFalse();
        await Assert.That(address).IsEqualTo(default(IPv4Address));
    }

    [Test]
    public async Task FromBytes_ShortSpan_Throws()
    {
        await Assert.That(() =>
        {
            IPv4Address _ = IPv4Address.FromBytes(ReadOnlySpan<byte>.Empty);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
    }

    [Test]
    public async Task TryFormat_MinimalBuffer_FitsZeroAddress()
    {
        IPv4Address addr = default;
        char[] buffer = new char[7];
        bool ok = addr.TryFormat(buffer, out int written, default, null);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(7);
        await Assert.That(new string(buffer, 0, written)).IsEqualTo("0.0.0.0");
    }

    // === Classification ===

    [Test]
    [Arguments("10.0.0.1", true)]
    [Arguments("172.16.0.1", true)]
    [Arguments("172.31.255.255", true)]
    [Arguments("192.168.1.1", true)]
    [Arguments("8.8.8.8", false)]
    public async Task IsPrivate(string input, bool expected)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.IsPrivate).IsEqualTo(expected);
    }

    [Test]
    [Arguments("127.0.0.1", true)]
    [Arguments("127.255.255.255", true)]
    [Arguments("192.168.1.1", false)]
    public async Task IsLoopback(string input, bool expected)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.IsLoopback).IsEqualTo(expected);
    }

    [Test]
    [Arguments("224.0.0.1", true)]
    [Arguments("239.255.255.255", true)]
    [Arguments("192.168.1.1", false)]
    public async Task IsMulticast(string input, bool expected)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.IsMulticast).IsEqualTo(expected);
    }

    [Test]
    [Arguments("169.254.1.1", true)]
    [Arguments("169.254.0.0", true)]
    [Arguments("192.168.1.1", false)]
    public async Task IsLinkLocal(string input, bool expected)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.IsLinkLocal).IsEqualTo(expected);
    }

    [Test]
    [Arguments("255.255.255.255", true)]
    [Arguments("192.168.1.1", false)]
    public async Task IsBroadcast(string input, bool expected)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.IsBroadcast).IsEqualTo(expected);
    }

    // === Formatting ===

    [Test]
    [Arguments(0x01020304u, "1.2.3.4")]
    [Arguments(0u, "0.0.0.0")]
    [Arguments(0xFFFFFFFFu, "255.255.255.255")]
    [Arguments(0xC0A80101u, "192.168.1.1")]
    public async Task Format_ProducesCorrectString(uint value, string expected)
    {
        IPv4Address addr = new(value);
        await Assert.That(addr.Format()).IsEqualTo(expected);
        await Assert.That(addr.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task TryFormat_WritesCorrectly()
    {
        IPv4Address addr = new(0xC0A80101u);
        char[] buf = new char[IPv4Address.MaxFormattedLength];
        bool ok = addr.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        await Assert.That(new string(buf, 0, written)).IsEqualTo("192.168.1.1");
    }

    [Test]
    public async Task TryFormat_BufferTooSmall_ReturnsFalse()
    {
        IPv4Address addr = new(0xC0A80101u);
        char[] buf = new char[4];
        bool ok = addr.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatTemp_ProducesCorrectString()
    {
        IPv4Address addr = new(0xC0A80101u);
        string formatted;
        using (TempString temp = addr.FormatTemp())
        {
            formatted = temp.ToString();
        }
        await Assert.That(formatted).IsEqualTo("192.168.1.1");
    }

    // === Round-trip ===

    [Test]
    [Arguments("1.2.3.4")]
    [Arguments("0.0.0.0")]
    [Arguments("255.255.255.255")]
    [Arguments("192.168.1.1")]
    [Arguments("10.0.0.1")]
    public async Task ParseFormat_RoundTrip(string input)
    {
        await Assert.That(IPv4Address.TryParse(input, out IPv4Address addr)).IsTrue();
        await Assert.That(addr.Format()).IsEqualTo(input);
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality_SameValue_AreEqual()
    {
        IPv4Address a = new(0xC0A80101u);
        IPv4Address b = new(0xC0A80101u);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != b).IsFalse();
    }

    [Test]
    public async Task Equality_DifferentValue_AreNotEqual()
    {
        IPv4Address a = new(0xC0A80101u);
        IPv4Address b = new(0xC0A80102u);
        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task CompareTo_Ordering()
    {
        IPv4Address lo = new(1u);
        IPv4Address hi = new(2u);
        IPv4Address lo2 = new(1u);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
        await Assert.That(hi.CompareTo(lo)).IsGreaterThan(0);
        await Assert.That(lo.CompareTo(lo2)).IsEqualTo(0);
        await Assert.That(lo < hi).IsTrue();
        await Assert.That(hi > lo).IsTrue();
        await Assert.That(lo <= lo2).IsTrue();
        await Assert.That(hi >= lo).IsTrue();
    }

    [Test]
    public async Task IComparable_CompareTo_Null_Returns1()
    {
        IComparable addr = new IPv4Address(1u);
        await Assert.That(addr.CompareTo(null)).IsEqualTo(1);
    }

    [Test]
    public async Task IComparable_CompareTo_WrongType_Throws()
    {
        IComparable addr = new IPv4Address(1u);
        await Assert.That(() => addr.CompareTo("wrong")).Throws<ArgumentException>();
    }

    // === Binary Serialization ===

    [Test]
    public async Task TryGetWrittenSize_Is4()
    {
        IPv4Address addr = new(0xC0A80101u);
        bool ok = addr.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(4);
    }

    [Test]
    public async Task ToBytes_BigEndian()
    {
        IPv4Address addr = new(0x01020304u);
        byte[] buf = new byte[4];
        int written = addr.ToBytes(buf);
        await Assert.That(written).IsEqualTo(4);
        await Assert.That(buf[0]).IsEqualTo((byte)1);
        await Assert.That(buf[1]).IsEqualTo((byte)2);
        await Assert.That(buf[2]).IsEqualTo((byte)3);
        await Assert.That(buf[3]).IsEqualTo((byte)4);
    }

    [Test]
    public async Task FromBytes_ToBytes_RoundTrip()
    {
        IPv4Address original = new(0xC0A80101u);
        byte[] buf = new byte[4];
        original.ToBytes(buf);
        IPv4Address restored = IPv4Address.FromBytes(buf);
        await Assert.That(restored).IsEqualTo(original);
    }

    // === GetHashCode ===

    [Test]
    public async Task GetHashCode_SameValue_SameHash()
    {
        IPv4Address a = new(0xC0A80101u);
        IPv4Address b = new(0xC0A80101u);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
