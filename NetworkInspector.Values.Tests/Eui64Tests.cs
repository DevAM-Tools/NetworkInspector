// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// Tests for <see cref="Eui64"/>: construction, formatting, parsing,
/// equality, comparison, and binary serialization.
/// </summary>
internal sealed class Eui64Tests
{
    // === Construction ===

    [Test]
    public async Task Constructor_StoresRawValue()
    {
        Eui64 eui = new(0x0011223344556677UL);
        await Assert.That(eui.RawValue).IsEqualTo(0x0011223344556677UL);
    }

    [Test]
    public async Task Default_IsAllZeros()
    {
        Eui64 eui = default;
        await Assert.That(eui.RawValue).IsEqualTo(0UL);
    }

    // === Parsing ===

    [Test]
    [Arguments("00:11:22:33:44:55:66:77", 0x0011223344556677UL)]
    [Arguments("FF:FF:FF:FF:FF:FF:FF:FF", 0xFFFFFFFFFFFFFFFFUL)]
    [Arguments("00:00:00:00:00:00:00:00", 0UL)]
    [Arguments("aa:bb:cc:dd:ee:ff:00:11", 0xAABBCCDDEEFF0011UL)]
    public async Task TryParse_ValidAddresses(string input, ulong expected)
    {
        await Assert.That(Eui64.TryParse(input, out Eui64 eui)).IsTrue();
        await Assert.That(eui.RawValue).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("00:11:22:33:44:55:66")]         // 7 groups — too short
    [Arguments("00:11:22:33:44:55:66:77:88")]   // 9 groups — too long
    [Arguments("ZZ:11:22:33:44:55:66:77")]      // invalid hex
    [Arguments("0011:22:33:44:55:66:77")]        // wrong octet length
    public async Task TryParse_InvalidAddresses_ReturnsFalse(string input)
    {
        bool ok = Eui64.TryParse(input, out Eui64 eui);
        await Assert.That(ok).IsFalse();
        await Assert.That(eui).IsEqualTo(default(Eui64));
    }

    // === Formatting ===

    [Test]
    [Arguments(0x0011223344556677UL, "00:11:22:33:44:55:66:77")]
    [Arguments(0UL, "00:00:00:00:00:00:00:00")]
    [Arguments(0xFFFFFFFFFFFFFFFFUL, "FF:FF:FF:FF:FF:FF:FF:FF")]
    public async Task Format_ProducesCorrectString(ulong value, string expected)
    {
        Eui64 eui = new(value);
        await Assert.That(eui.Format()).IsEqualTo(expected);
        await Assert.That(eui.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task TryFormat_BufferTooSmall_ReturnsFalse()
    {
        Eui64 eui = new(0x0011223344556677UL);
        char[] buf = new char[4];
        bool ok = eui.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatTemp_ProducesCorrectString()
    {
        Eui64 eui = new(0x0011223344556677UL);
        string formatted;
        using (TempString temp = eui.FormatTemp())
        {
            formatted = temp.ToString();
        }
        await Assert.That(formatted).IsEqualTo("00:11:22:33:44:55:66:77");
    }

    // === Round-trip ===

    [Test]
    [Arguments("00:11:22:33:44:55:66:77")]
    [Arguments("FF:FF:FF:FF:FF:FF:FF:FF")]
    [Arguments("00:00:00:00:00:00:00:00")]
    public async Task ParseFormat_RoundTrip(string input)
    {
        await Assert.That(Eui64.TryParse(input, out Eui64 eui)).IsTrue();
        await Assert.That(eui.Format()).IsEqualTo(input);
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality_SameValue_AreEqual()
    {
        Eui64 a = new(0x0011223344556677UL);
        Eui64 b = new(0x0011223344556677UL);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task CompareTo_Ordering()
    {
        Eui64 lo = new(1UL);
        Eui64 hi = new(2UL);
        Eui64 lo2 = new(1UL);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
        await Assert.That(hi.CompareTo(lo)).IsGreaterThan(0);
        await Assert.That(lo.CompareTo(lo2)).IsEqualTo(0);
        await Assert.That(lo < hi).IsTrue();
        await Assert.That(hi > lo).IsTrue();
    }

    [Test]
    public async Task IComparable_CompareTo_Null_Returns1()
    {
        IComparable eui = new Eui64(1UL);
        await Assert.That(eui.CompareTo(null)).IsEqualTo(1);
    }

    [Test]
    public async Task IComparable_CompareTo_WrongType_Throws()
    {
        IComparable eui = new Eui64(1UL);
        await Assert.That(() => eui.CompareTo("wrong")).Throws<ArgumentException>();
    }

    // === Binary Serialization ===

    [Test]
    public async Task TryGetSerializedSize_Is8()
    {
        Eui64 eui = new(0x0011223344556677UL);
        bool ok = eui.TryGetSerializedSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(8);
    }

    [Test]
    public async Task ToBytes_BigEndian()
    {
        Eui64 eui = new(0x0011223344556677UL);
        byte[] buf = new byte[8];
        int written = eui.ToBytes(buf);
        await Assert.That(written).IsEqualTo(8);
        await Assert.That(buf[0]).IsEqualTo((byte)0x00);
        await Assert.That(buf[1]).IsEqualTo((byte)0x11);
        await Assert.That(buf[2]).IsEqualTo((byte)0x22);
        await Assert.That(buf[3]).IsEqualTo((byte)0x33);
        await Assert.That(buf[4]).IsEqualTo((byte)0x44);
        await Assert.That(buf[5]).IsEqualTo((byte)0x55);
        await Assert.That(buf[6]).IsEqualTo((byte)0x66);
        await Assert.That(buf[7]).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task FromBytes_ToBytes_RoundTrip()
    {
        Eui64 original = new(0x0011223344556677UL);
        byte[] buf = new byte[8];
        original.ToBytes(buf);
        Eui64 restored = Eui64.FromBytes(buf);
        await Assert.That(restored).IsEqualTo(original);
    }
}
