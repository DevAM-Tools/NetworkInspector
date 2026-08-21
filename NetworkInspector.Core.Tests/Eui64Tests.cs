// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Eui64"/>: construction, formatting, binary serialization,
/// equality, comparison, and edge cases.
/// </summary>
internal sealed class Eui64Tests
{
    // === Construction ===

    [Test]
    public async Task Construction_RawValue()
    {
        Eui64 eui = new(0x001122334455_6677);
        await Assert.That(eui.RawValue).IsEqualTo(0x001122334455_6677UL);
    }

    [Test]
    public async Task Default_IsZero()
    {
        Eui64 eui = default;
        await Assert.That(eui.RawValue).IsEqualTo(0UL);
    }

    // === Factory ===

    [Test]
    public async Task FromBytes_8Bytes()
    {
        byte[] bytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];
        Eui64 eui = Eui64.FromBytes(bytes);
        await Assert.That(eui.RawValue).IsEqualTo(0x0011223344556677UL);
    }

    [Test]
    public async Task FromBytes_TooShort_Throws()
    {
        byte[] bytes = [0x00, 0x11, 0x22];
        await Assert.That(() =>
        {
            Eui64 _ = Eui64.FromBytes(bytes);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
        await Assert.That(Eui64.TryFromBytes(bytes, out Eui64 eui)).IsFalse();
        await Assert.That(eui).IsEqualTo(default(Eui64));
    }

    // === Binary roundtrip ===

    [Test]
    public async Task ToBytesRoundtrip()
    {
        byte[] original = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01, 0x23];
        Eui64 eui = Eui64.FromBytes(original);
        byte[] buf = new byte[8];
        int written = eui.ToBytes(buf);
        await Assert.That(written).IsEqualTo(8);
        for (int i = 0; i < 8; i++)
        {
            await Assert.That(buf[i]).IsEqualTo(original[i]);
        }
    }

    [Test]
    public async Task ToBytes_TooShort_ReturnsZero()
    {
        Eui64 eui = new(1);
        byte[] small = new byte[3];
        int written = eui.ToBytes(small);
        await Assert.That(written).IsEqualTo(0);
    }

    // === IBinarySerializable ===

    [Test]
    public async Task TryGetWrittenSize()
    {
        Eui64 eui = new(1);
        bool ok = eui.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(8);
    }

    [Test]
    public async Task TryWrite_Success()
    {
        Eui64 eui = new(0x0102030405060708);
        byte[] buf = new byte[8];
        bool ok = eui.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(8);
        await Assert.That(buf[0]).IsEqualTo((byte)0x01);
        await Assert.That(buf[7]).IsEqualTo((byte)0x08);
    }

    [Test]
    public async Task TryWrite_TooShort()
    {
        Eui64 eui = new(1);
        byte[] buf = new byte[3];
        bool ok = eui.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === Formatting ===

    [Test]
    public async Task Format()
    {
        Eui64 eui = new(0x0011223344556677);
        string formatted = eui.Format();
        await Assert.That(formatted).IsEqualTo("00:11:22:33:44:55:66:77");
    }

    [Test]
    public async Task Format_AllZeros()
    {
        Eui64 eui = new(0);
        await Assert.That(eui.Format()).IsEqualTo("00:00:00:00:00:00:00:00");
    }

    [Test]
    public async Task Format_AllOnes()
    {
        Eui64 eui = new(ulong.MaxValue);
        await Assert.That(eui.Format()).IsEqualTo("FF:FF:FF:FF:FF:FF:FF:FF");
    }

    [Test]
    public async Task FormatInto()
    {
        Eui64 eui = new(0xAABBCCDDEEFF0123);
        char[] buffer = new char[Eui64.FormattedLength];
        int written = eui.FormatInto(buffer);
        string formatted = new(buffer, 0, written);
        await Assert.That(written).IsEqualTo(23);
        await Assert.That(formatted).IsEqualTo("AA:BB:CC:DD:EE:FF:01:23");
    }

    [Test]
    public async Task FormatTemp()
    {
        Eui64 eui = new(0x0011223344556677);
        TempString temp = eui.FormatTemp();
        await Assert.That(temp.ToString()).IsEqualTo("00:11:22:33:44:55:66:77");
    }

    [Test]
    public async Task ToString_MatchesFormat()
    {
        Eui64 eui = new(0x0011223344556677);
        await Assert.That(eui.ToString()).IsEqualTo(eui.Format());
    }

    [Test]
    public async Task ToString_FormatProvider()
    {
        Eui64 eui = new(1);
        await Assert.That(eui.ToString(null, null)).IsEqualTo(eui.Format());
    }

    // === TryFormat ===

    [Test]
    public async Task TryFormat_TooShort()
    {
        Eui64 eui = new(1);
        char[] small = new char[5];
        bool ok = eui.TryFormat(small, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === TryFormat (UTF-8) ===

    [Test]
    public async Task TryFormatUtf8()
    {
        Eui64 eui = new(0x0011223344556677);
        byte[] buf = new byte[Eui64.FormattedLength];
        bool ok = eui.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        string str = System.Text.Encoding.ASCII.GetString(buf, 0, written);
        await Assert.That(str).IsEqualTo("00:11:22:33:44:55:66:77");
    }

    [Test]
    public async Task TryFormatUtf8_TooShort()
    {
        Eui64 eui = new(1);
        byte[] small = new byte[5];
        bool ok = eui.TryFormat(small, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === IStringSize ===

    [Test]
    public async Task TryGetStringSize()
    {
        Eui64 eui = new(1);
        bool ok = eui.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(Eui64.FormattedLength);
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality()
    {
        Eui64 a = new(1);
        Eui64 b = new(1);
        Eui64 c = new(2);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task Equality_ObjectBoxing()
    {
        Eui64 a = new(1);
        object b = new Eui64(1);
        object other = "not an eui64";
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(other)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_EqualObjectsSameHash()
    {
        Eui64 a = new(42);
        Eui64 b = new(42);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task CompareTo()
    {
        Eui64 low = new(1);
        Eui64 high = new(100);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
    }

    [Test]
    public async Task ComparisonOperators()
    {
        Eui64 a = new(1);
        Eui64 b = new(100);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
        Eui64 aCopy = a;
        await Assert.That(a <= aCopy).IsTrue();
        await Assert.That(a >= aCopy).IsTrue();
    }
}
