// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscHeader"/> covering base parsing,
/// timestamp format, date/epoch computation, version comments, and defaults.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscHeaderTests
{
    // ========================================================================
    // Base parsing
    // ========================================================================

    [Test]
    public async Task BaseHex_SetsNumericBase16()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("base hex"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.NumericBase).IsEqualTo(16);
    }

    [Test]
    public async Task BaseDec_SetsNumericBase10()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("base dec"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.NumericBase).IsEqualTo(10);
    }

    [Test]
    public async Task BaseHexTimestampsAbsolute_SetsBaseAndFormat()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("base hex timestamps absolute"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.NumericBase).IsEqualTo(16);
        await Assert.That(header.TimestampFormat).IsEqualTo("absolute");
    }

    [Test]
    public async Task BaseDecTimestampsRelative_SetsBaseAndFormat()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("base dec timestamps relative"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.NumericBase).IsEqualTo(10);
        await Assert.That(header.TimestampFormat).IsEqualTo("relative");
    }

    // ========================================================================
    // Date parsing
    // ========================================================================

    [Test]
    public async Task DateLine_SetsDateString()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("date Sun Nov 24 11:44:00 AM 2019"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.DateString).IsNotNull();
    }

    [Test]
    public async Task DateLine_ComputesStartTimeEpoch()
    {
        AscHeader header = new();
        header.TryParseLine("date Sun Nov 24 11:44:00 AM 2019"u8);

        // Epoch should be non-zero for a valid date
        await Assert.That(header.StartUnixNanos).IsNotEqualTo(0L);
    }

    // ========================================================================
    // Internal events
    // ========================================================================

    [Test]
    public async Task InternalEventsLogged_SetsFlag()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("internal events logged"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.InternalEventsLogged).IsTrue();
    }

    // ========================================================================
    // Header termination
    // ========================================================================

    [Test]
    public async Task NonHeaderLine_ReturnsFalse()
    {
        AscHeader header = new();
        // A CAN data line should not be consumed as header
        bool consumed = header.TryParseLine("0.100000 1 123 Rx d 8 AA BB CC DD EE FF 00 11"u8);

        await Assert.That(consumed).IsFalse();
    }

    [Test]
    public async Task BeginTriggerblock_ReturnsFalse()
    {
        AscHeader header = new();
        // "Begin Triggerblock" ends the header, should return false
        bool consumed = header.TryParseLine("Begin Triggerblock"u8);

        await Assert.That(consumed).IsFalse();
    }

    // ========================================================================
    // Defaults
    // ========================================================================

    [Test]
    public async Task Defaults_HexBase_AbsoluteTimestamps()
    {
        AscHeader header = new();

        await Assert.That(header.NumericBase).IsEqualTo(16);
        await Assert.That(header.TimestampFormat).IsEqualTo("absolute");
        await Assert.That(header.InternalEventsLogged).IsFalse();
        await Assert.That(header.DateString).IsNull();
        await Assert.That(header.StartUnixNanos).IsEqualTo(0L);
    }

    // ========================================================================
    // Full header sequence
    // ========================================================================

    [Test]
    public async Task FullHeaderSequence_AllFieldsParsed()
    {
        AscHeader header = new();

        await Assert.That(header.TryParseLine("date Sun Nov 24 11:44:00 AM 2019"u8)).IsTrue();
        await Assert.That(header.TryParseLine("base hex timestamps absolute"u8)).IsTrue();
        await Assert.That(header.TryParseLine("internal events logged"u8)).IsTrue();

        // Next line should not be consumed (trigger block or data)
        await Assert.That(header.TryParseLine("Begin Triggerblock"u8)).IsFalse();

        await Assert.That(header.NumericBase).IsEqualTo(16);
        await Assert.That(header.TimestampFormat).IsEqualTo("absolute");
        await Assert.That(header.InternalEventsLogged).IsTrue();
        await Assert.That(header.DateString).IsNotNull();
        await Assert.That(header.StartUnixNanos).IsNotEqualTo(0L);
    }

    // ========================================================================
    // Comments in header
    // ========================================================================

    [Test]
    public async Task CommentLine_ConsumedAsHeader()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("; This is a comment"u8);

        // Comments before data are part of the header section
        await Assert.That(consumed).IsTrue();
    }

    [Test]
    public async Task DoubleSlashComment_ConsumedAsHeader()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("// Another comment"u8);

        await Assert.That(consumed).IsTrue();
    }

    [Test]
    public async Task RelativeHeaderAccumulatesTwoFrames()
    {
        using AscSource source = AscSource.FromText(
            "date Sun Nov 24 11:44:00 AM 2019\n" +
            "base dec timestamps relative\n" +
            "Begin Triggerblock\n" +
            "0.001 1 100 Rx d 1 1\n" +
            "0.001 1 100 Rx d 1 2\n" +
            "End TriggerBlock\n");
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        long start = AscDateParser.TryParseToUnixNanos("Sun Nov 24 11:44:00 AM 2019", TimeZoneInfo.Utc);

        Frame first = source.NextFrame()!.Value;
        Frame second = source.NextFrame()!.Value;

        await Assert.That(first.Timestamp.AsNanos).IsEqualTo(start + 1_000_000L);
        await Assert.That(second.Timestamp.AsNanos).IsEqualTo(start + 2_000_000L);
    }

    [Test]
    public async Task GermanWeekdayParsesToNonZero()
    {
        long nanos = AscDateParser.TryParseToUnixNanos("Die Dez 21 11:29:01 2004", TimeZoneInfo.Utc);
        DateTime civil = new(2004, 12, 21, 11, 29, 1, DateTimeKind.Unspecified);
        long expected = (new DateTimeOffset(civil, TimeSpan.Zero).UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100L;

        await Assert.That(nanos).IsEqualTo(expected);
    }

    [Test]
    public async Task EnglishMillisecondMeridianKeepsFraction()
    {
        long nanos = AscDateParser.TryParseToUnixNanos("Wed Apr 16 09:21:13.159 am 2014", TimeZoneInfo.Utc);
        DateTime civil = new(2014, 4, 16, 9, 21, 13, 159, DateTimeKind.Unspecified);
        long expected = (new DateTimeOffset(civil, TimeSpan.Zero).UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100L;

        await Assert.That(nanos).IsEqualTo(expected);
    }

    [Test]
    public async Task PreviousLogCommentStoresAbsoluteSeconds()
    {
        AscHeader header = new();
        bool consumed = header.TryParseLine("// 60.0000 previous log file: Inc_L1.asc"u8);

        await Assert.That(consumed).IsTrue();
        await Assert.That(header.PreviousLogAbsolute.HasValue).IsTrue();
        await Assert.That(header.PreviousLogAbsolute!.Value.WholeSeconds).IsEqualTo(60L);
    }
}
