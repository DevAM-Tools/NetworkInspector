// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Uuid"/>: construction, formatting, binary serialization,
/// equality, comparison, and edge cases.
/// </summary>
internal sealed class UuidTests
{
    // === Construction ===

    [Test]
    public async Task Construction_HighLow()
    {
        Uuid uuid = new(0x0102030405060708, 0x090A0B0C0D0E0F10);
        await Assert.That(uuid.High).IsEqualTo(0x0102030405060708UL);
        await Assert.That(uuid.Low).IsEqualTo(0x090A0B0C0D0E0F10UL);
    }

    [Test]
    public async Task Default_IsAllZeros()
    {
        Uuid uuid = default;
        await Assert.That(uuid.High).IsEqualTo(0UL);
        await Assert.That(uuid.Low).IsEqualTo(0UL);
    }

    // === Factory ===

    [Test]
    public async Task FromBytes_16Bytes()
    {
        byte[] bytes = [0x55, 0x0E, 0x84, 0x00, 0xE2, 0x9B, 0x41, 0xD4,
                        0xA7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00];
        Uuid uuid = Uuid.FromBytes(bytes);
        await Assert.That(uuid.High).IsEqualTo(0x550E8400E29B41D4UL);
        await Assert.That(uuid.Low).IsEqualTo(0xA716446655440000UL);
    }

    [Test]
    public async Task FromBytes_TooShort_Throws()
    {
        byte[] bytes = [0x55, 0x0E];
        await Assert.That(() =>
        {
            Uuid _ = Uuid.FromBytes(bytes);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
        await Assert.That(Uuid.TryFromBytes(bytes, out Uuid uuid)).IsFalse();
        await Assert.That(uuid).IsEqualTo(default(Uuid));
    }

    // === Binary roundtrip ===

    [Test]
    public async Task ToBytesRoundtrip()
    {
        byte[] original = [0x55, 0x0E, 0x84, 0x00, 0xE2, 0x9B, 0x41, 0xD4,
                           0xA7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00];
        Uuid uuid = Uuid.FromBytes(original);
        byte[] buf = new byte[16];
        int written = uuid.ToBytes(buf);
        await Assert.That(written).IsEqualTo(16);
        for (int i = 0; i < 16; i++)
        {
            await Assert.That(buf[i]).IsEqualTo(original[i]);
        }
    }

    [Test]
    public async Task ToBytes_TooShort_ReturnsZero()
    {
        Uuid uuid = new(1, 2);
        byte[] small = new byte[5];
        int written = uuid.ToBytes(small);
        await Assert.That(written).IsEqualTo(0);
    }

    // === IBinarySerializable ===

    [Test]
    public async Task TryGetWrittenSize()
    {
        Uuid uuid = new(1, 2);
        bool ok = uuid.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(16);
    }

    [Test]
    public async Task TryWrite_Success()
    {
        Uuid uuid = new(0x0102030405060708, 0x090A0B0C0D0E0F10);
        byte[] buf = new byte[16];
        bool ok = uuid.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(16);
        await Assert.That(buf[0]).IsEqualTo((byte)0x01);
        await Assert.That(buf[15]).IsEqualTo((byte)0x10);
    }

    [Test]
    public async Task TryWrite_TooShort()
    {
        Uuid uuid = new(1, 2);
        byte[] buf = new byte[5];
        bool ok = uuid.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === Formatting ===

    [Test]
    public async Task Format_KnownUuid()
    {
        // 550E8400-E29B-41D4-A716-446655440000
        Uuid uuid = new(0x550E8400E29B41D4, 0xA716446655440000);
        string formatted = uuid.Format();
        await Assert.That(formatted).IsEqualTo("550E8400-E29B-41D4-A716-446655440000");
    }

    [Test]
    public async Task Format_AllZeros()
    {
        Uuid uuid = new(0, 0);
        string formatted = uuid.Format();
        await Assert.That(formatted).IsEqualTo("00000000-0000-0000-0000-000000000000");
    }

    [Test]
    public async Task Format_AllOnes()
    {
        Uuid uuid = new(ulong.MaxValue, ulong.MaxValue);
        string formatted = uuid.Format();
        await Assert.That(formatted).IsEqualTo("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
    }

    [Test]
    public async Task FormatInto()
    {
        Uuid uuid = new(0x550E8400E29B41D4, 0xA716446655440000);
        char[] buffer = new char[Uuid.FormattedLength];
        int written = uuid.FormatInto(buffer);
        string formatted = new(buffer, 0, written);
        await Assert.That(written).IsEqualTo(36);
        await Assert.That(formatted).IsEqualTo("550E8400-E29B-41D4-A716-446655440000");
    }

    [Test]
    public async Task FormatTemp()
    {
        Uuid uuid = new(0x550E8400E29B41D4, 0xA716446655440000);
        TempString temp = uuid.FormatTemp();
        await Assert.That(temp.ToString()).IsEqualTo("550E8400-E29B-41D4-A716-446655440000");
    }

    [Test]
    public async Task ToString_MatchesFormat()
    {
        Uuid uuid = new(0x550E8400E29B41D4, 0xA716446655440000);
        await Assert.That(uuid.ToString()).IsEqualTo(uuid.Format());
    }

    [Test]
    public async Task ToString_FormatProvider()
    {
        Uuid uuid = new(1, 2);
        await Assert.That(uuid.ToString(null, null)).IsEqualTo(uuid.Format());
    }

    // === TryFormat ===

    [Test]
    public async Task TryFormat_TooShort()
    {
        Uuid uuid = new(1, 2);
        char[] small = new char[10];
        bool ok = uuid.TryFormat(small, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === TryFormat (UTF-8) ===

    [Test]
    public async Task TryFormatUtf8()
    {
        Uuid uuid = new(0x550E8400E29B41D4, 0xA716446655440000);
        byte[] buf = new byte[Uuid.FormattedLength];
        bool ok = uuid.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsTrue();
        string str = System.Text.Encoding.ASCII.GetString(buf, 0, written);
        await Assert.That(str).IsEqualTo("550E8400-E29B-41D4-A716-446655440000");
    }

    [Test]
    public async Task TryFormatUtf8_TooShort()
    {
        Uuid uuid = new(1, 2);
        byte[] small = new byte[10];
        bool ok = uuid.TryFormat(small, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === IStringSize ===

    [Test]
    public async Task TryGetStringSize()
    {
        Uuid uuid = new(1, 2);
        bool ok = uuid.TryGetStringSize(default, null, out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(Uuid.FormattedLength);
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality()
    {
        Uuid a = new(1, 2);
        Uuid b = new(1, 2);
        Uuid c = new(3, 4);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task Equality_ObjectBoxing()
    {
        Uuid a = new(1, 2);
        object b = new Uuid(1, 2);
        object other = "not a uuid";
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(other)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_EqualObjectsSameHash()
    {
        Uuid a = new(1, 2);
        Uuid b = new(1, 2);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task CompareTo()
    {
        Uuid low = new(0, 1);
        Uuid high = new(1, 0);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
    }

    [Test]
    public async Task ComparisonOperators()
    {
        Uuid a = new(0, 1);
        Uuid b = new(1, 0);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
        Uuid aCopy = a;
        await Assert.That(a <= aCopy).IsTrue();
        await Assert.That(a >= aCopy).IsTrue();
    }
}
