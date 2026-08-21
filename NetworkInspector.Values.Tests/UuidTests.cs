// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// Tests for <see cref="Uuid"/>: construction, formatting, parsing,
/// equality, comparison, and binary serialization.
/// </summary>
internal sealed class UuidTests
{
    // === Construction ===

    [Test]
    public async Task Constructor_StoresHighLow()
    {
        Uuid uuid = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
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

    // === Parsing ===

    [Test]
    [Arguments("00000000-0000-0000-0000-000000000000", 0UL, 0UL)]
    [Arguments("01020304-0506-0708-090a-0b0c0d0e0f10", 0x0102030405060708UL, 0x090A0B0C0D0E0F10UL)]
    [Arguments("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", 0xFFFFFFFFFFFFFFFFUL, 0xFFFFFFFFFFFFFFFFUL)]
    public async Task TryParse_ValidUuids(string input, ulong expectedHigh, ulong expectedLow)
    {
        await Assert.That(Uuid.TryParse(input, out Uuid uuid)).IsTrue();
        await Assert.That(uuid.High).IsEqualTo(expectedHigh);
        await Assert.That(uuid.Low).IsEqualTo(expectedLow);
    }

    [Test]
    [Arguments("")]
    [Arguments("00000000000000000000000000000000")]          // no dashes
    [Arguments("00000000-0000-0000-0000-00000000000")]       // too short
    [Arguments("00000000-0000-0000-0000-0000000000000")]     // too long
    [Arguments("00000000-0000-0000-0000-00000000000G")]      // invalid hex
    [Arguments("00000000X0000-0000-0000-000000000000")]      // wrong dash position
    public async Task TryParse_InvalidUuids_ReturnsFalse(string input)
    {
        bool ok = Uuid.TryParse(input, out Uuid uuid);
        await Assert.That(ok).IsFalse();
        await Assert.That(uuid).IsEqualTo(default(Uuid));
    }

    // === Formatting ===

    // Format() uses uppercase hex (HexChars = "0123456789ABCDEF")
    [Test]
    public async Task Format_AllZeros() =>
        await Assert.That(default(Uuid).Format()).IsEqualTo("00000000-0000-0000-0000-000000000000");

    [Test]
    public async Task Format_AllOnes()
    {
        Uuid uuid = new(0xFFFFFFFFFFFFFFFFUL, 0xFFFFFFFFFFFFFFFFUL);
        await Assert.That(uuid.Format()).IsEqualTo("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
    }

    [Test]
    public async Task TryFormat_BufferTooSmall_ReturnsFalse()
    {
        Uuid uuid = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        char[] buf = new char[10];
        bool ok = uuid.TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatTemp_ProducesCorrectString()
    {
        Uuid uuid = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        string formatted;
        using (TempString temp = uuid.FormatTemp())
        {
            formatted = temp.ToString();
        }
        await Assert.That(formatted).IsEqualTo("01020304-0506-0708-090A-0B0C0D0E0F10");
    }

    // === Round-trip ===

    [Test]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    [Arguments("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [Arguments("01020304-0506-0708-090A-0B0C0D0E0F10")]
    public async Task ParseFormat_RoundTrip(string input)
    {
        // TryParse accepts both cases; Format() always outputs uppercase
        await Assert.That(Uuid.TryParse(input, out Uuid uuid)).IsTrue();
        await Assert.That(uuid.Format()).IsEqualTo(input.ToUpperInvariant());
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality_SameValue_AreEqual()
    {
        Uuid a = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        Uuid b = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task CompareTo_Ordering_ByHighFirst()
    {
        Uuid lo = new(0UL, 1UL);
        Uuid hi = new(1UL, 0UL);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
        await Assert.That(hi.CompareTo(lo)).IsGreaterThan(0);
        await Assert.That(lo < hi).IsTrue();
    }

    [Test]
    public async Task CompareTo_SameHigh_OrdersByLow()
    {
        Uuid lo = new(1UL, 0UL);
        Uuid hi = new(1UL, 1UL);
        await Assert.That(lo.CompareTo(hi)).IsLessThan(0);
    }

    [Test]
    public async Task IComparable_CompareTo_Null_Returns1()
    {
        IComparable uuid = new Uuid(0UL, 1UL);
        await Assert.That(uuid.CompareTo(null)).IsEqualTo(1);
    }

    [Test]
    public async Task IComparable_CompareTo_WrongType_Throws()
    {
        IComparable uuid = new Uuid(0UL, 1UL);
        await Assert.That(() => uuid.CompareTo(42)).Throws<ArgumentException>();
    }

    // === Binary Serialization ===

    [Test]
    public async Task TryGetWrittenSize_Is16()
    {
        Uuid uuid = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        bool ok = uuid.TryGetWrittenSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(16);
    }

    [Test]
    public async Task ToBytes_FromBytes_RoundTrip()
    {
        Uuid original = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        byte[] buf = new byte[16];
        original.ToBytes(buf);
        Uuid restored = Uuid.FromBytes(buf);
        await Assert.That(restored).IsEqualTo(original);
    }

    [Test]
    public async Task TryFromBytes_ShortSpan_ReturnsFalse()
    {
        await Assert.That(Uuid.TryFromBytes(ReadOnlySpan<byte>.Empty, out Uuid uuid)).IsFalse();
        await Assert.That(uuid).IsEqualTo(default(Uuid));
    }

    [Test]
    public async Task FromBytes_ShortSpan_Throws()
    {
        await Assert.That(() =>
        {
            Uuid _ = Uuid.FromBytes(ReadOnlySpan<byte>.Empty);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
    }

    // === GetHashCode ===

    [Test]
    public async Task GetHashCode_SameValue_SameHash()
    {
        Uuid a = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        Uuid b = new(0x0102030405060708UL, 0x090A0B0C0D0E0F10UL);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
