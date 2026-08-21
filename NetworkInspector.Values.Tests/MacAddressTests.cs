// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// Tests for <see cref="MacAddress"/>: construction, formatting, parsing, binary serialization,
/// equality, comparison, and edge cases including 48-bit masking.
/// </summary>
internal sealed class MacAddressTests
{
    // === Construction ===

    [Test]
    public async Task Constructor_MasksTo48Bits()
    {
        // Upper bits beyond bit 47 must be stripped
        ulong raw = 0xFFAA_BB00_1122_3344UL; // upper 16 bits should be masked out
        MacAddress addr = new(raw);
        await Assert.That(addr.RawValue).IsEqualTo(raw & 0x0000_FFFF_FFFF_FFFFUL);
    }

    [Test]
    public async Task Constructor_ValidValue_Stored()
    {
        MacAddress addr = new(0x001122334455UL);
        await Assert.That(addr.RawValue).IsEqualTo(0x001122334455UL);
    }

    [Test]
    public async Task Default_IsAllZeros()
    {
        MacAddress addr = default;
        await Assert.That(addr.RawValue).IsEqualTo(0UL);
    }

    // === Factory Methods ===

    [Test]
    [Arguments("00:11:22:33:44:55", 0x001122334455UL)]
    [Arguments("FF:FF:FF:FF:FF:FF", 0xFFFFFFFFFFFFUL)]
    [Arguments("00:00:00:00:00:00", 0UL)]
    [Arguments("aa:bb:cc:dd:ee:ff", 0xAABBCCDDEEFFUL)]
    public async Task TryParse_ValidAddresses(string input, ulong expected)
    {
        await Assert.That(MacAddress.TryParse(input, out MacAddress addr)).IsTrue();
        await Assert.That(addr.RawValue).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("00:11:22:33:44")]           // too short
    [Arguments("00:11:22:33:44:55:66")]     // too long
    [Arguments("ZZ:11:22:33:44:55")]        // invalid hex
    [Arguments("0011:22:33:44:55")]         // wrong separator position
    public async Task TryParse_InvalidAddresses_ReturnsFalse(string input)
    {
        bool ok = MacAddress.TryParse(input, out MacAddress addr);
        await Assert.That(ok).IsFalse();
        await Assert.That(addr).IsEqualTo(default(MacAddress));
    }

    [Test]
    public async Task FromBytes_BigEndian()
    {
        byte[] bytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        MacAddress addr = MacAddress.FromBytes(bytes);
        await Assert.That(addr.RawValue).IsEqualTo(0x001122334455UL);
    }

    [Test]
    public async Task TryFromBytes_ShortSpan_ReturnsFalse()
    {
        await Assert.That(MacAddress.TryFromBytes(ReadOnlySpan<byte>.Empty, out MacAddress address)).IsFalse();
        await Assert.That(address).IsEqualTo(default(MacAddress));
    }

    [Test]
    public async Task FromBytes_ShortSpan_Throws()
    {
        await Assert.That(() =>
        {
            MacAddress _ = MacAddress.FromBytes(ReadOnlySpan<byte>.Empty);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
    }

    // === Formatting ===

    [Test]
    [Arguments(0x001122334455UL, "00:11:22:33:44:55")]
    [Arguments(0UL, "00:00:00:00:00:00")]
    [Arguments(0xFFFFFFFFFFFFUL, "FF:FF:FF:FF:FF:FF")]
    public async Task Format_ProducesCorrectString(ulong value, string expected)
    {
        MacAddress addr = new(value);
        await Assert.That(addr.Format()).IsEqualTo(expected);
        await Assert.That(addr.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task TryFormat_BufferTooSmall_ReturnsFalse()
    {
        MacAddress addr = new(0x001122334455UL);
        char[] buf = new char[4];
        bool ok = addr.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatTemp_ProducesCorrectString()
    {
        MacAddress addr = new(0x001122334455UL);
        string formatted;
        using (TempString temp = addr.FormatTemp())
        {
            formatted = temp.ToString();
        }
        await Assert.That(formatted).IsEqualTo("00:11:22:33:44:55");
    }

    // === Round-trip ===

    [Test]
    [Arguments("00:11:22:33:44:55")]
    [Arguments("FF:FF:FF:FF:FF:FF")]
    [Arguments("00:00:00:00:00:00")]
    public async Task ParseFormat_RoundTrip(string input)
    {
        await Assert.That(MacAddress.TryParse(input, out MacAddress addr)).IsTrue();
        await Assert.That(addr.Format()).IsEqualTo(input);
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality_SameValue_AreEqual()
    {
        MacAddress a = new(0x001122334455UL);
        MacAddress b = new(0x001122334455UL);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task CompareTo_Ordering()
    {
        MacAddress lo = new(1UL);
        MacAddress hi = new(2UL);
        MacAddress lo2 = new(1UL);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
        await Assert.That(hi.CompareTo(lo)).IsGreaterThan(0);
        await Assert.That(lo.CompareTo(lo2)).IsEqualTo(0);
        await Assert.That(lo < hi).IsTrue();
        await Assert.That(hi > lo).IsTrue();
    }

    [Test]
    public async Task IComparable_CompareTo_Null_Returns1()
    {
        IComparable addr = new MacAddress(1UL);
        await Assert.That(addr.CompareTo(null)).IsEqualTo(1);
    }

    [Test]
    public async Task IComparable_CompareTo_WrongType_Throws()
    {
        IComparable addr = new MacAddress(1UL);
        await Assert.That(() => addr.CompareTo(42)).Throws<ArgumentException>();
    }

    // === Binary Serialization ===

    [Test]
    public async Task TryGetWrittenSize_Is6()
    {
        MacAddress addr = new(0x001122334455UL);
        bool ok = addr.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(6);
    }

    [Test]
    public async Task ToBytes_BigEndian()
    {
        MacAddress addr = new(0x001122334455UL);
        byte[] buf = new byte[6];
        int written = addr.ToBytes(buf);
        await Assert.That(written).IsEqualTo(6);
        await Assert.That(buf[0]).IsEqualTo((byte)0x00);
        await Assert.That(buf[1]).IsEqualTo((byte)0x11);
        await Assert.That(buf[2]).IsEqualTo((byte)0x22);
        await Assert.That(buf[3]).IsEqualTo((byte)0x33);
        await Assert.That(buf[4]).IsEqualTo((byte)0x44);
        await Assert.That(buf[5]).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task FromBytes_ToBytes_RoundTrip()
    {
        MacAddress original = new(0x001122334455UL);
        byte[] buf = new byte[6];
        original.ToBytes(buf);
        MacAddress restored = MacAddress.FromBytes(buf);
        await Assert.That(restored).IsEqualTo(original);
    }
}
