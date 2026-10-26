// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Hostile or ambiguous ASC lines: the parsers must reject them through the <c>Try</c> contract (false), never throw, and
/// must not misread decimal ids, symbol names, or hex payload groups.
/// </summary>
internal sealed class AscParserHardeningTests
{
    #region FlexRay limits

    [Test]
    [Arguments("1.000000 Fr 1 V9 800 0 0 0 0 x 0", 16)]
    [Arguments("1.000000 Fr 1 V9 FFFF 0 0 0 0 x 0", 16)]
    [Arguments("1.000000 Fr 1 V9 2048 0 0 0 0 x 0", 10)]
    [Arguments("1.000000 Fr 1 V9 65535 0 0 0 0 x 0", 10)]
    public async Task OldFlexRayLineWithSlotAboveTheIsoFieldIsRejectedNotThrown(string line, int numericBase)
    {
        bool ok = AscFlexRayParser.TryParse(Encoding.ASCII.GetBytes(line), numericBase, out _, out _, out byte[] frame);

        await Assert.That(ok).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("0.039255 Fr RMSG 0 0 1 1 2048 7 Rx 0 14 5 32 151 Message_2 4 4 27 24 29 241")]
    [Arguments("0.039255 Fr RMSG 0 0 1 1 65535 7 Rx 0 14 5 32 151 Message_2 4 4 27 24 29 241")]
    public async Task NewFlexRayLineWithSlotAboveTheIsoFieldIsRejectedNotThrown(string line)
    {
        bool ok = AscFlexRayParser.TryParse(Encoding.ASCII.GetBytes(line), 10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FlexRaySlotAtTheIsoMaximumStillParses()
    {
        bool ok = AscFlexRayParser.TryParse("1.000000 Fr 1 V9 7FF 0 0 0 0 x 0"u8, 16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out _)).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)0x7FF);
    }

    #endregion

    #region Timestamp range checks

    /// <summary>Counts the exceptions thrown (even when caught) on the current thread while <paramref name="action"/> runs.</summary>
    private static int _CountExceptionsThrown(Action action)
    {
        int threadId = Environment.CurrentManagedThreadId;
        int count = 0;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == threadId)
            {
                _ = Interlocked.Increment(ref count);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            action();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        return count;
    }

    [Test]
    [Arguments(long.MaxValue, 0, 1L, 0)]
    [Arguments(long.MaxValue, 600_000_000, 0L, 600_000_000)]
    [Arguments(long.MinValue, 0, -1L, 0)]
    [Arguments(long.MinValue, 0, long.MinValue, 0)]
    public async Task TimestampAdditionThatOverflowsIsRejectedWithoutAnException(long leftSeconds, int leftNanos, long rightSeconds, int rightNanos)
    {
        bool ok = true;
        AscTimestamp sum = default;

        int thrown = _CountExceptionsThrown(() =>
            ok = AscTimestamp.TryAdd(new AscTimestamp(leftSeconds, leftNanos), new AscTimestamp(rightSeconds, rightNanos), out sum));

        await Assert.That(ok).IsFalse();
        await Assert.That(sum).IsEqualTo(default(AscTimestamp));
        await Assert.That(thrown).IsEqualTo(0);
    }

    [Test]
    public async Task TimestampAdditionCarriesTheFraction()
    {
        bool ok = AscTimestamp.TryAdd(new AscTimestamp(1, 600_000_000), new AscTimestamp(2, 600_000_000), out AscTimestamp sum);

        await Assert.That(ok).IsTrue();
        await Assert.That(sum.WholeSeconds).IsEqualTo(4L);
        await Assert.That(sum.Nanoseconds).IsEqualTo(200_000_000);
    }

    [Test]
    public async Task TimestampAdditionAtTheLargestSecondCountStillWorks()
    {
        bool ok = AscTimestamp.TryAdd(new AscTimestamp(long.MaxValue - 1, 600_000_000), new AscTimestamp(0, 399_999_999), out AscTimestamp sum);
        bool carryOk = AscTimestamp.TryAdd(new AscTimestamp(long.MaxValue - 1, 600_000_000), new AscTimestamp(0, 400_000_000), out AscTimestamp carried);

        await Assert.That(ok).IsTrue();
        await Assert.That(sum.WholeSeconds).IsEqualTo(long.MaxValue - 1);
        await Assert.That(carryOk).IsTrue();
        await Assert.That(carried.WholeSeconds).IsEqualTo(long.MaxValue);
        await Assert.That(carried.Nanoseconds).IsEqualTo(0);
    }

    [Test]
    [Arguments(0L, 9_223_372_037L, 0)]
    [Arguments(0L, 9_223_372_036L, 854_775_808)]
    [Arguments(0L, -9_223_372_037L, 0)]
    [Arguments(long.MaxValue, 1L, 0)]
    [Arguments(long.MinValue, -1L, 0)]
    [Arguments(1L, 9_223_372_036L, 854_775_807)]
    public async Task UnixNanosThatDoNotFitAreRejectedWithoutAnException(long start, long offsetSeconds, int offsetNanos)
    {
        bool ok = true;
        long result = -1;

        int thrown = _CountExceptionsThrown(() =>
            ok = AscTimestamp.TryToUnixNanos(start, new AscTimestamp(offsetSeconds, offsetNanos), out result));

        await Assert.That(ok).IsFalse();
        await Assert.That(result).IsEqualTo(0L);
        await Assert.That(thrown).IsEqualTo(0);
    }

    [Test]
    [Arguments(0L, 9_223_372_036L, 854_775_807, long.MaxValue)]
    [Arguments(0L, -9_223_372_036L, 0, -9_223_372_036_000_000_000L)]
    [Arguments(1_704_067_200_000_000_000L, 1L, 500_000_000, 1_704_067_201_500_000_000L)]
    public async Task UnixNanosAtTheEdgesOfTheRangeAreExact(long start, long offsetSeconds, int offsetNanos, long expected)
    {
        bool ok = AscTimestamp.TryToUnixNanos(start, new AscTimestamp(offsetSeconds, offsetNanos), out long result);

        await Assert.That(ok).IsTrue();
        await Assert.That(result).IsEqualTo(expected);
    }

    #endregion

    #region Line classification by token position

    [Test]
    [Arguments("0.700000 1 ErrorFrame", AscLineType.CanErrorFrame)]
    [Arguments("0.700000 1  errorframe ECC: 10001100", AscLineType.CanErrorFrame)]
    [Arguments("0.700000 2 OverloadFrame", AscLineType.CanOverloadFrame)]
    [Arguments("0.100000 1 123 ErrorFrameCounter Rx d 1 AA", AscLineType.CanMessage)]
    [Arguments("0.100000 1 123 Rx d 8 01 02 03 04 05 06 07 08 OverloadFrameCount", AscLineType.CanMessage)]
    [Arguments("0.100000 1 ErrorFrameCounter", AscLineType.CanMessage)]
    [Arguments("0.100000 1 Statistic: D 1 R 0", AscLineType.CanBusStatistics)]
    [Arguments("0.100000 1 123 Rx d 1 AA", AscLineType.CanMessage)]
    [Arguments("0.100000 1", AscLineType.CanMessage)]
    [Arguments("0.039255 Fr RMSG 0 0 1 1 4 7 Rx 0 14 5 32 151 ErrorFrameName 4 4 27 24 29 241", AscLineType.FlexRayMessage)]
    [Arguments("0.100000 ETH 1 Rx 4:AABBCCDD", AscLineType.EthernetPacket)]
    [Arguments("0.100000 SV: ErrorFrameCount = 3", AscLineType.SystemVariable)]
    [Arguments("0.050000 CAN 1 Status: bus off", AscLineType.CanStatus)]
    [Arguments("0.050000 CAN 1 Foo StatusX", AscLineType.Unknown)]
    [Arguments("0.100000 Li 3C Rx 8 01 02 03 04 05 06 07 08 SleepRequest checksum = F0", AscLineType.LinMessage)]
    [Arguments("0.100000 L2 10 Rx 4 AA BB CC DD WakeupCounter", AscLineType.LinMessage)]
    [Arguments("0.100000 Li Sleep", AscLineType.LinEvent)]
    [Arguments("0.100000 Li 1 WakeupFrame", AscLineType.LinEvent)]
    [Arguments("0.100000 L3 WAKEUP", AscLineType.LinEvent)]
    public async Task ClassifierDecidesByTokenPositionInBothOverloads(string line, AscLineType expected)
    {
        AscLineType fromBytes = AscLineClassifier.Classify(Encoding.ASCII.GetBytes(line));
        AscLineType fromChars = AscLineClassifier.Classify(line.AsSpan());

        await Assert.That(fromBytes).IsEqualTo(expected);
        await Assert.That(fromChars).IsEqualTo(expected);
    }

    [Test]
    public async Task SymbolNamedLikeAKeywordDoesNotCountAsAParseFailureInASource()
    {
        // A classic CAN message whose symbolic name contains "ErrorFrame" must parse as a message (it used to be routed
        // to the error-frame parser and reported as a parse failure).
        string text = "base hex timestamps absolute\n"
            + "0.100000 1 123 Rx d 1 AA ErrorFrameCounter\n";
        using AscSource source = AscSource.FromText(text, "symbols.asc");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
    }

    #endregion

    #region Negative decimal ids

    [Test]
    [Arguments("0.100000 Li -1 Rx 1 AA")]
    [Arguments("0.100000 Li -61 Rx 1 AA")]
    [Arguments("0.100000 Li 64 Rx 1 AA")]
    public async Task LinDecimalIdOutsideSixBitsIsRejected(string line)
    {
        bool ok = AscLinParser.TryParse(Encoding.ASCII.GetBytes(line), 10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LinDecimalIdInRangeStillParses()
    {
        bool ok = AscLinParser.TryParse("0.100000 Li 60 Rx 1 AA"u8, 10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[5]).IsEqualTo(LinProtectedId.Compute(60));
    }

    [Test]
    [Arguments("0.100000 1 -1 Rx d 1 AA")]
    [Arguments("0.100000 1 -2147483647 Rx d 1 AA")]
    [Arguments("0.100000 1 -5x Rx d 1 AA")]
    public async Task CanDecimalNegativeIdIsRejected(string line)
    {
        bool ok = AscCanParser.TryParse(Encoding.ASCII.GetBytes(line), 10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsFalse();
        await Assert.That(frame.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CanDecimalIdInRangeStillParses()
    {
        bool ok = AscCanParser.TryParse("0.100000 1 291 Rx d 1 AA"u8, 10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame)).IsEqualTo(291u);
    }

    #endregion

    #region Ethernet continuation lines and slab use

    [Test]
    [Arguments("12 34")]
    [Arguments("00 11 22")]
    [Arguments("0042\t0043")]
    public async Task HexPayloadLineWithBlanksIsNotALoggingEvent(string line)
    {
        bool isEvent = AscEthernetParser.IsNewLoggingEvent(Encoding.ASCII.GetBytes(line));

        await Assert.That(isEvent).IsFalse();
    }

    [Test]
    [Arguments("0.600000 1 123 Rx d 1 01")]
    [Arguments("12.5 ETH 1 Rx 2:AABB")]
    public async Task LineStartingWithATimestampAndMoreTokensIsALoggingEvent(string line)
    {
        bool isEvent = AscEthernetParser.IsNewLoggingEvent(Encoding.ASCII.GetBytes(line));

        await Assert.That(isEvent).IsTrue();
    }

    [Test]
    public async Task WrappedPayloadWithBlanksAndDecimalLookingGroupsIsOnePacket()
    {
        byte[] record = Encoding.ASCII.GetBytes("0.500000 ETH 1 Rx 8:\n12 34\n56 78\n90 11 22 33\n");

        bool ok = AscEthernetParser.TryParse(record, 16, out _, out int channel, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(1);
        await Assert.That(frame).IsEquivalentTo(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x90, 0x11, 0x22, 0x33 });
    }

    [Test]
    public async Task StreamSourceKeepsAWrappedPayloadWithBlanksTogether()
    {
        string text = "base hex timestamps absolute\n"
            + "0.500000 ETH 1 Rx 8:1234\n"
            + "56 78\n"
            + "90 11 22 33\n"
            + "0.600000 1 100 Rx d 1 AA\n";
        using AscStreamSource source = AscStreamSource.FromStream(new MemoryStream(Encoding.ASCII.GetBytes(text)), "wrapped.asc");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? ethernet = source.NextFrame();
        Frame? can = source.NextFrame();

        await Assert.That(ethernet).IsNotNull();
        await Assert.That(ethernet!.Value.Data.ToArray()).IsEquivalentTo(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x90, 0x11, 0x22, 0x33 });
        await Assert.That(can).IsNotNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task FileSourceKeepsAWrappedPayloadWithBlanksTogether()
    {
        string text = "base hex timestamps absolute\n"
            + "0.500000 ETH 1 Rx 8:1234\n"
            + "56 78\n"
            + "90 11 22 33\n"
            + "0.600000 1 100 Rx d 1 AA\n";
        using AscSource source = AscSource.FromText(text, "wrapped.asc");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? ethernet = source.NextFrame();
        Frame? can = source.NextFrame();

        await Assert.That(ethernet).IsNotNull();
        await Assert.That(ethernet!.Value.Data.ToArray()).IsEquivalentTo(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x90, 0x11, 0x22, 0x33 });
        await Assert.That(can).IsNotNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task DiskSourceKeepsAWrappedPayloadWithBlanksTogether()
    {
        string text = "base hex timestamps absolute\n"
            + "0.500000 ETH 1 Rx 8:1234\n"
            + "56 78\n"
            + "90 11 22 33\n"
            + "0.600000 1 100 Rx d 1 AA\n";
        using TempCaptureFile file = new(Encoding.ASCII.GetBytes(text), ".asc");
        using AscSource source = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? ethernet = source.NextFrame();
        Frame? can = source.NextFrame();

        await Assert.That(ethernet).IsNotNull();
        await Assert.That(ethernet!.Value.Data.ToArray()).IsEquivalentTo(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x90, 0x11, 0x22, 0x33 });
        await Assert.That(can).IsNotNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task StreamSourceReportsAnEthernetPacketThatOverflowsTheRecordLimit()
    {
        // 1518 bytes (0x5EE), one per line with ten leading blanks: about 19.7 KB of text, above the 16 KiB record limit.
        StringBuilder text = new();
        _ = text.Append("base hex timestamps absolute\n");
        _ = text.Append("0.500000 ETH 1 Rx 5EE:\n");
        for (int i = 0; i < 1518; i++)
        {
            _ = text.Append("          AA\n");
        }

        _ = text.Append("0.600000 1 100 Rx d 1 AA\n");
        using AscStreamSource source = AscStreamSource.FromStream(
            new MemoryStream(Encoding.ASCII.GetBytes(text.ToString())), "overflow.asc");
        List<string> messages = [];
        source.FrameSkipped += (_, error) => messages.Add(error.Message);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();
        Frame? after = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(after).IsNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(1);
        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0]).Contains(AscEthernetParser.MaxRecordBytes.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task StreamSourceDisposedWhileParsingEndsQuietlyInsteadOfThrowing()
    {
        // The skip handler disposes the source in the middle of NextFrame, as another thread's Dispose could at any time.
        // The next line is a valid frame, but the registry is gone: the read ends without a frame and without an exception.
        string text = "base hex timestamps absolute\n"
            + "0.100000 1 ZZZ Rx d 1 AA\n"
            + "0.200000 1 100 Rx d 1 AA\n";
        AscStreamSource source = AscStreamSource.FromStream(new MemoryStream(Encoding.ASCII.GetBytes(text)), "disposed.asc");
        source.FrameSkipped += (_, _) => source.Dispose();
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task StreamSourceStillReadsAWrappedPacketThatFitsTheRecordLimit()
    {
        StringBuilder text = new();
        _ = text.Append("base hex timestamps absolute\n");
        _ = text.Append("0.500000 ETH 1 Rx 5EE:\n");
        for (int i = 0; i < 1518; i++)
        {
            _ = text.Append("AA\n");
        }

        _ = text.Append("0.600000 1 100 Rx d 1 AA\n");
        using AscStreamSource source = AscStreamSource.FromStream(
            new MemoryStream(Encoding.ASCII.GetBytes(text.ToString())), "fits.asc");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? ethernet = source.NextFrame();
        Frame? can = source.NextFrame();

        await Assert.That(ethernet).IsNotNull();
        await Assert.That(ethernet!.Value.Data.Length).IsEqualTo(1518);
        await Assert.That(can).IsNotNull();
        await Assert.That(source.SkippedFrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedEthernetPayloadDoesNotConsumeSlabSpace()
    {
        FrameSlabAllocator slabs = new(FrameSlabAllocator.MinSlabSize);
        byte[] malformed = Encoding.ASCII.GetBytes("0.500000 ETH 1 Rx 40:0011ZZ");

        bool ok = AscEthernetParser.TryParse(malformed, 16, slabs, out _, out _, out _);
        Memory<byte> next = slabs.Allocate(1);

        await Assert.That(ok).IsFalse();
        await Assert.That(MemoryMarshal.TryGetArray(next, out ArraySegment<byte> segment)).IsTrue();
        await Assert.That(segment.Offset).IsEqualTo(0);
    }

    #endregion
}
