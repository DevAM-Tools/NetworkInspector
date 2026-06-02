// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// Tests for <see cref="Timestamp"/>: construction, formatting, parsing, arithmetic,
/// binary serialization, equality, comparison, and edge cases including overflow and negative values.
/// </summary>
internal sealed class TimestampTests
{
    // === Construction and Factory Methods ===

    [Test]
    public async Task Constructor_StoresNanos()
    {
        Timestamp ts = new(123_456_789L);
        await Assert.That(ts.AsNanos).IsEqualTo(123_456_789L);
    }

    [Test]
    public async Task Default_IsZero()
    {
        Timestamp ts = default;
        await Assert.That(ts.AsNanos).IsEqualTo(0L);
    }

    [Test]
    public async Task FromNanos_RoundTrip()
    {
        Timestamp ts = Timestamp.FromNanos(999L);
        await Assert.That(ts.AsNanos).IsEqualTo(999L);
    }

    [Test]
    public async Task FromMicros_ConvertsCorrectly()
    {
        Timestamp ts = Timestamp.FromMicros(1L);
        await Assert.That(ts.AsNanos).IsEqualTo(1_000L);
    }

    [Test]
    public async Task FromMillis_ConvertsCorrectly()
    {
        Timestamp ts = Timestamp.FromMillis(1L);
        await Assert.That(ts.AsNanos).IsEqualTo(1_000_000L);
    }

    [Test]
    public async Task FromSecs_ConvertsCorrectly()
    {
        Timestamp ts = Timestamp.FromSecs(1L);
        await Assert.That(ts.AsNanos).IsEqualTo(1_000_000_000L);
    }

    [Test]
    public async Task FromSecsAndNanos_CombinesCorrectly()
    {
        Timestamp ts = Timestamp.FromSecsAndNanos(1L, 500_000_000);
        await Assert.That(ts.AsNanos).IsEqualTo(1_500_000_000L);
    }

    // === Properties ===

    [Test]
    public async Task AsMicros_TruncatesCorrectly()
    {
        Timestamp ts = Timestamp.FromNanos(1_999L);
        await Assert.That(ts.AsMicros).IsEqualTo(1L);
    }

    [Test]
    public async Task AsMillis_TruncatesCorrectly()
    {
        Timestamp ts = Timestamp.FromNanos(1_999_999L);
        await Assert.That(ts.AsMillis).IsEqualTo(1L);
    }

    [Test]
    public async Task Secs_TruncatesCorrectly()
    {
        Timestamp ts = Timestamp.FromNanos(1_999_999_999L);
        await Assert.That(ts.Secs).IsEqualTo(1L);
    }

    [Test]
    public async Task SubsecNanos_ReturnsRemainder()
    {
        Timestamp ts = Timestamp.FromSecsAndNanos(2L, 123_456_789);
        await Assert.That(ts.SubsecNanos).IsEqualTo(123_456_789);
    }

    // === Negative Values ===

    [Test]
    public async Task Negative_AsNanos_IsPreUnixEpoch()
    {
        Timestamp ts = Timestamp.FromNanos(-1L);
        await Assert.That(ts.AsNanos).IsEqualTo(-1L);
    }

    [Test]
    public async Task Negative_CompareTo_Positive_IsLess()
    {
        Timestamp neg = Timestamp.FromNanos(-1L);
        Timestamp pos = Timestamp.FromNanos(1L);
        await Assert.That(neg.CompareTo(pos)).IsLessThan(0);
    }

    // === Now ===

    [Test]
    public async Task Now_IsPositive()
    {
        // Unix epoch was in 1970; a positive value means we're after it
        Timestamp ts = Timestamp.Now;
        await Assert.That(ts.AsNanos).IsGreaterThan(0L);
    }

    // === Parsing ===

    [Test]
    [Arguments("1970-01-01T00:00:00.000000000Z", 0L)]
    [Arguments("1970-01-01T00:00:00.000000001Z", 1L)]
    [Arguments("1970-01-01T00:00:01.000000000Z", 1_000_000_000L)]
    public async Task TryParse_ValidTimestamps(string input, long expectedNanos)
    {
        await Assert.That(Timestamp.TryParse(input, out Timestamp ts)).IsTrue();
        await Assert.That(ts.AsNanos).IsEqualTo(expectedNanos);
    }

    [Test]
    [Arguments("")]                                     // empty
    [Arguments("1970-01-01T00:00:00.000000000")]        // missing trailing Z (29 chars)
    [Arguments("1970-01-01T00:00:00.00000000Z")]        // too short nanos (28+1=29 chars but wrong nanos length)
    [Arguments("1970-01-01T00:00:00.0000000000Z")]      // too many nanos digits (31 chars)
    [Arguments("1970-01-01 00:00:00.000000000Z")]       // space instead of T
    [Arguments("1970-13-01T00:00:00.000000000Z")]       // invalid month 13
    [Arguments("1970-00-01T00:00:00.000000000Z")]       // invalid month 0
    [Arguments("1970-01-00T00:00:00.000000000Z")]       // invalid day 0
    [Arguments("1970-01-32T00:00:00.000000000Z")]       // invalid day 32
    [Arguments("1970-01-01T24:00:00.000000000Z")]       // invalid hour 24
    [Arguments("1970-01-01T00:60:00.000000000Z")]       // invalid minute 60
    [Arguments("1970-01-01T00:00:60.000000000Z")]       // invalid second 60
    [Arguments("1970-02-30T00:00:00.000000000Z")]       // invalid Feb 30
    [Arguments("1970-01-01T00:00:00.0000X0000Z")]       // non-digit in nanos
    public async Task TryParse_InvalidTimestamps_ReturnsFalse(string input)
    {
        bool ok = Timestamp.TryParse(input, out Timestamp ts);
        await Assert.That(ok).IsFalse();
        await Assert.That(ts).IsEqualTo(default(Timestamp));
    }

    // === Format Round-trip ===

    [Test]
    [Arguments(0L)]
    [Arguments(1L)]
    [Arguments(1_000_000_000L)]
    [Arguments(1_000_000_001L)]
    public async Task ParseFormat_RoundTrip(long nanos)
    {
        Timestamp ts = Timestamp.FromNanos(nanos);
        string formatted = ts.Format();
        await Assert.That(Timestamp.TryParse(formatted, out Timestamp restored)).IsTrue();
        await Assert.That(restored.AsNanos).IsEqualTo(nanos);
    }

    // === Formatting ===

    [Test]
    public async Task Format_UnixEpoch_IsCorrectString() =>
        await Assert.That(default(Timestamp).Format()).IsEqualTo("1970-01-01T00:00:00.000000000Z");

    [Test]
    public async Task Format_OutputLength_IsMaxFormattedLength()
    {
        // Verify the format constant matches actual output length
        int len = Timestamp.MaxFormattedLength;
        string formatted = default(Timestamp).Format();
        await Assert.That(formatted.Length).IsEqualTo(len);
    }

    [Test]
    public async Task TryFormat_BufferTooSmall_ReturnsFalse()
    {
        char[] buf = new char[10];
        bool ok = default(Timestamp).TryFormat(buf, out int written, default, null);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task FormatTemp_ProducesCorrectString()
    {
        string formatted;
        using (TempString temp = default(Timestamp).FormatTemp())
        {
            formatted = temp.ToString();
        }
        await Assert.That(formatted).IsEqualTo("1970-01-01T00:00:00.000000000Z");
    }

    // === Arithmetic ===

    [Test]
    public async Task Add_TimeSpan_AddsCorrectly()
    {
        Timestamp ts = Timestamp.FromSecs(1L);
        Timestamp result = ts + TimeSpan.FromSeconds(1);
        await Assert.That(result.Secs).IsEqualTo(2L);
    }

    [Test]
    public async Task Subtract_TimeSpan_SubtractsCorrectly()
    {
        Timestamp ts = Timestamp.FromSecs(5L);
        Timestamp result = ts - TimeSpan.FromSeconds(2);
        await Assert.That(result.Secs).IsEqualTo(3L);
    }

    [Test]
    public async Task Subtract_Timestamps_ReturnsDuration()
    {
        Timestamp a = Timestamp.FromSecs(10L);
        Timestamp b = Timestamp.FromSecs(3L);
        TimeSpan duration = a - b;
        await Assert.That(duration).IsEqualTo(TimeSpan.FromSeconds(7));
    }

    [Test]
    public async Task Subtract_Timestamps_NegativeDelta_IsNegativeDuration()
    {
        Timestamp a = Timestamp.FromSecs(1L);
        Timestamp b = Timestamp.FromSecs(5L);
        TimeSpan duration = a - b;
        await Assert.That(duration).IsEqualTo(TimeSpan.FromSeconds(-4));
    }

    [Test]
    public async Task Subtract_Timestamps_Overflow_ThrowsOverflowException()
    {
        Timestamp maxTs = Timestamp.FromNanos(long.MaxValue);
        Timestamp minTs = Timestamp.FromNanos(long.MinValue);
        // long.MaxValue - long.MinValue overflows a signed 64-bit integer
        await Assert.That(() =>
        {
            TimeSpan _ = Timestamp.Subtract(maxTs, minTs);
            return Task.CompletedTask;
        }).Throws<OverflowException>();
    }

    // === Equality & Comparison ===

    [Test]
    public async Task Equality_SameNanos_AreEqual()
    {
        Timestamp a = Timestamp.FromNanos(42L);
        Timestamp b = Timestamp.FromNanos(42L);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != b).IsFalse();
    }

    [Test]
    public async Task CompareTo_Ordering()
    {
        Timestamp lo = Timestamp.FromNanos(1L);
        Timestamp hi = Timestamp.FromNanos(2L);
        Timestamp lo2 = Timestamp.FromNanos(1L);
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
        IComparable ts = Timestamp.FromNanos(1L);
        await Assert.That(ts.CompareTo(null)).IsEqualTo(1);
    }

    [Test]
    public async Task IComparable_CompareTo_WrongType_Throws()
    {
        IComparable ts = Timestamp.FromNanos(1L);
        await Assert.That(() => ts.CompareTo("wrong")).Throws<ArgumentException>();
    }

    // === Binary Serialization ===

    [Test]
    public async Task TryGetSerializedSize_Is8()
    {
        Timestamp ts = Timestamp.FromNanos(42L);
        bool ok = ts.TryGetSerializedSize(out int size);
        await Assert.That(ok).IsTrue();
        await Assert.That(size).IsEqualTo(8);
    }

    [Test]
    public async Task TryWrite_WritesBigEndianNanos()
    {
        Timestamp ts = Timestamp.FromNanos(0x0102030405060708L);
        byte[] buf = new byte[8];
        bool ok = ts.TryWrite(buf, out int written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(8);
        await Assert.That(buf[0]).IsEqualTo((byte)0x01);
        await Assert.That(buf[1]).IsEqualTo((byte)0x02);
        await Assert.That(buf[2]).IsEqualTo((byte)0x03);
        await Assert.That(buf[3]).IsEqualTo((byte)0x04);
        await Assert.That(buf[4]).IsEqualTo((byte)0x05);
        await Assert.That(buf[5]).IsEqualTo((byte)0x06);
        await Assert.That(buf[6]).IsEqualTo((byte)0x07);
        await Assert.That(buf[7]).IsEqualTo((byte)0x08);
    }

    [Test]
    public async Task TryWrite_BufferTooSmall_ReturnsFalse()
    {
        Timestamp ts = Timestamp.FromNanos(42L);
        byte[] buf = new byte[4];
        bool ok = ts.TryWrite(buf, out int written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    // === GetHashCode ===

    [Test]
    public async Task GetHashCode_SameValue_SameHash()
    {
        Timestamp a = Timestamp.FromNanos(42L);
        Timestamp b = Timestamp.FromNanos(42L);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
