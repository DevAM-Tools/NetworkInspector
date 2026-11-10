// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FailingStream = NetworkInspector.Exporters.Tests.Helpers.TestStreams.FailingStream;

namespace NetworkInspector.Exporters.Tests.Text;

/// <summary>
/// Tests for the maximum text length of <see cref="TextExporter"/> (cut position and ellipsis) and for the counters in
/// Strict mode.
/// </summary>
internal sealed class TextExporterTruncationTests
{
    #region Offering packets and reading lines

    /// <summary>Offers a packet that the exporter must accept; a refusal ends the test here instead of leaving the checks below vacuous.</summary>
    /// <param name="exporter">The exporter.</param>
    /// <param name="packet">The packet.</param>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static void _Offer(TextExporter exporter, Packet packet)
    {
        if (!exporter.OnPacket(packet))
        {
            throw new InvalidOperationException("The exporter refused a packet that the test needs written.");
        }
    }

    /// <summary>Returns the output line that starts with the given label (for example "  Text: "), without line end.</summary>
    /// <param name="output">The exported text.</param>
    /// <param name="prefix">The label the line starts with.</param>
    /// <returns>The first line with that prefix, or an empty string when there is none.</returns>
    private static string _Line(string output, string prefix)
    {
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line.TrimEnd('\r');
            }
        }

        return string.Empty;
    }

    /// <summary>Exports one packet with the Text exporter at the given text limit and detail level.</summary>
    /// <param name="packet">The packet to export.</param>
    /// <param name="maxTextLength">The text limit in characters; 0 is unlimited.</param>
    /// <param name="level">The detail level of the tree.</param>
    /// <returns>The exported text.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string _Export(Packet packet, int maxTextLength, TextDetailLevel level)
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(stream)
            .WithMaxTextLength(maxTextLength)
            .WithDetailLevel(level)
            .Build())
        {
            _Offer(exporter, packet);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region Cutting strings and numbers

    /// <summary>Proves that a string value longer than the limit is cut at the limit and followed by an ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StringValueLongerThanTheLimitIsCutAtTheLimitAndGetsAnEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "abcdefghijklmnopqrst");

        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: abcdefghij…");
    }

    /// <summary>Proves that a string value exactly as long as the limit is written whole, without an ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StringValueExactlyAtTheLimitIsWrittenWithoutEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "abcdefghij");

        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: abcdefghij");
    }

    /// <summary>Proves that a string value one character over the limit loses exactly that character and gets an ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StringValueOneCharacterOverTheLimitLosesExactlyThatCharacter()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "abcdefghijk");

        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: abcdefghij…");
    }

    /// <summary>Proves that a non-ASCII string is cut between characters, not between the bytes of one character.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task NonAsciiStringValueIsCutBetweenCharactersNotBetweenBytes()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "äöüäöüäöüäöü");

        string output = _Export(packet, 5, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: äöüäö…");
    }

    /// <summary>Proves that a number shorter than the limit gets no ellipsis, although its size estimate is larger than the limit.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task NumberShorterThanTheLimitGetsNoEllipsisEvenThoughItsSizeEstimateIsLarger()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text");

        // An unsigned number reports a size estimate of up to 20 characters, but "0" is one character.
        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Packet Id: ")).IsEqualTo("  Packet Id: 0");
        await Assert.That(_Line(output, "  Number: ")).IsEqualTo("  Number: 1");
    }

    /// <summary>Proves that a formatted value, here a timestamp, longer than the limit is cut at the limit.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FormattedValueLongerThanTheLimitIsCutAtTheLimit()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text");

        // The timestamp text has 30 characters.
        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Timestamp: ")).IsEqualTo("  Timestamp: 1970-01-01…");
    }

    #endregion

    #region Surrogate pairs and very long values

    /// <summary>Proves that a cut in a string value never splits a surrogate pair: a pair that does not fit is left out as a whole.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task SurrogatePairIsNotSplitAtTheCut()
    {
        using CustomFieldPacketFactory factory = new();

        // Five 16-bit units are "ab" plus one pair (2 units) plus one more pair that would need units 5 and 6: the cut after
        // five units would fall inside the second pair, so that pair is left out as a whole.
        Packet packet = factory.Create(1, "ab\U0001F600\U0001F600cd");

        string output = _Export(packet, 5, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: ab\U0001F600…");
    }

    /// <summary>Proves that a cut in a custom text never splits a surrogate pair, so no lone surrogate reaches the output.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task SurrogatePairInACustomTextIsNotSplitAtTheCut()
    {
        using CustomFieldPacketFactory factory = new();

        // The container text takes the display-text path: the cut after five units would fall inside the second pair, so the pair is
        // left out as a whole and no lone surrogate (a replacement character) reaches the output.
        Packet packet = factory.Create(1, "x", containerText: "ab\U0001F600\U0001F600cd");

        string output = _Export(packet, 5, TextDetailLevel.Standard);

        await Assert.That(output).Contains("\nab\U0001F600…\n");
        await Assert.That(output.Contains('�', StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// A string of five million characters is cut at the default limit of 256 characters without formatting the whole value first:
    /// the line holds exactly the first 256 characters and the ellipsis, whatever the length of the value.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFiveMillionCharacterStringIsWrittenAsItsFirst256CharactersAndAnEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, new string('y', 5_000_000));

        string output = _Export(packet, 256, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: " + new string('y', 256) + "…");
    }

    #endregion

    #region Escaping control characters and backslashes

    /// <summary>Proves that C1 control characters and a backslash in a text value are escaped, and a backslash is doubled.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AC1ControlAndABackslashInATextValueAreEscaped()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "a\u009Bb\\n\u009D");

        string output = _Export(packet, 256, TextDetailLevel.Standard);

        // U+009B (CSI) and U+009D (OSC) are written as \u009b and \u009d, and the backslash is doubled, so the two characters
        // backslash and n read differently from an escaped line feed.
        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: a\\u009bb\\\\n\\u009d");
    }

    /// <summary>Proves that a real line feed is written as an escaped line feed and differs from a literal backslash followed by n.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ARealLineFeedStaysAnEscapedLineFeedAndALiteralBackslashNDiffersFromIt()
    {
        using CustomFieldPacketFactory factory = new();
        Packet feed = factory.Create(1, "a\nb");
        Packet literal = factory.Create(1, "a\\nb");

        string feedLine = _Line(_Export(feed, 256, TextDetailLevel.Standard), "  Text: ");
        string literalLine = _Line(_Export(literal, 256, TextDetailLevel.Standard), "  Text: ");

        await Assert.That(feedLine).IsEqualTo("  Text: a\\nb");
        await Assert.That(literalLine).IsEqualTo("  Text: a\\\\nb");
    }

    /// <summary>Proves that a C1 control character and a backslash count with their escaped width at the cut, and an escape is never split.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AC1ControlAndABackslashCountWithTheirEscapedWidthAtTheCut()
    {
        using CustomFieldPacketFactory factory = new();

        // The escaped widths are 6 for U+009B and 2 for the backslash: "a" and the C1 control take seven output characters, the
        // backslash would make nine, so the cut after eight characters leaves the backslash out whole.
        Packet packet = factory.Create(1, "a\u009B\\b");

        string output = _Export(packet, 8, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: a\\u009b…");
    }

    /// <summary>Proves that printable characters that share a lead byte with the C1 controls are written unchanged.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task APrintableCharacterWithTheLeadByteOfAC1ControlIsNotEscaped()
    {
        using CustomFieldPacketFactory factory = new();

        // U+00B0 (degree sign) and U+00A0 share the lead byte 0xC2 with the C1 controls but are printable: they stay as they are.
        Packet packet = factory.Create(1, "20°C ok");

        string output = _Export(packet, 256, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: 20°C ok");
    }

    #endregion

    #region Bytes fields

    /// <summary>Proves that the hex of a bytes field never exceeds the limit before the ellipsis, and a pair that does not fit is left out.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BytesFieldHexOutputNeverExceedsTheLimitBeforeTheEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, null, frameData: [0xAA, 0xBB, 0xCC, 0xDD]);

        // "AA BB" is five characters, one more than the limit of four, so only "AA" fits.
        string output = _Export(packet, 4, TextDetailLevel.Full);

        await Assert.That(_Line(output, "  Bytes: ")).IsEqualTo("  Bytes: AA…");
    }

    /// <summary>Proves that the hex of a bytes field uses the whole limit when the pairs fit it exactly.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BytesFieldHexOutputUsesTheWholeLimitWhenItFitsExactly()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, null, frameData: [0xAA, 0xBB, 0xCC, 0xDD]);

        string output = _Export(packet, 5, TextDetailLevel.Full);

        await Assert.That(_Line(output, "  Bytes: ")).IsEqualTo("  Bytes: AA BB…");
    }

    /// <summary>Proves that a bytes field whose hex fits the limit is written whole, without an ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BytesFieldThatFitsTheLimitIsWrittenWithoutEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, null, frameData: [0xAA, 0xBB]);

        string output = _Export(packet, 5, TextDetailLevel.Full);

        await Assert.That(_Line(output, "  Bytes: ")).IsEqualTo("  Bytes: AA BB");
    }

    #endregion

    #region Escaped width at the cut

    /// <summary>Proves that an escaped control character counts with its escaped width at the cut, in a value and in a custom text.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AnEscapedControlCharacterCountsWithItsEscapedWidthAtTheCut()
    {
        using CustomFieldPacketFactory factory = new();

        // The value takes the formatted-value path, the container text the display-text path. "a", the two characters of "\n", and
        // "b" are four output characters; the next control character does not fit.
        Packet packet = factory.Create(1, "a\nb\nc", containerText: "a\nb\nc");

        string output = _Export(packet, 4, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: a\\nb…");
        await Assert.That(output).Contains("\na\\nb…\n");
    }

    /// <summary>Proves that a control character whose escape is wider than the limit is left out whole, leaving only the ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AControlCharacterWiderThanTheLimitIsLeftOutWhole()
    {
        using CustomFieldPacketFactory factory = new();

        // "\x01" takes four output characters, one more than the limit of three: nothing of the value fits, so only the ellipsis
        // is written and the escape is never cut in the middle.
        Packet packet = factory.Create(1, "\u0001ab", containerText: "\u0001ab");

        string output = _Export(packet, 3, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: …");
        await Assert.That(output).Contains("\n…\n");
    }

    #endregion

    #region Scratch buffer allocation

    /// <summary>Proves that a series of ever longer values reuses a growing pooled scratch buffer instead of allocating an array per length.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AGrowingSeriesOfLongValuesDoesNotAllocateAnArrayPerLength()
    {
        using CustomFieldPacketFactory factory = new();
        const int count = 200;
        Packet[] packets = new Packet[count];
        for (int i = 0; i < count; i++)
        {
            // Every value is longer than the one before and above the 512-byte stack limit, so the scratch has to grow.
            packets[i] = factory.Create(1, new string('x', 700 + (i * 5)));
        }

        // Each round starts a fresh exporter, so every round grows the scratch again from its first size; that growth is what the
        // bound judges. The smallest of up to five rounds keeps it and drops a round that a one-off allocation inflated.
        long total = long.MaxValue;
        for (int round = 0; round < 5 && total >= 50_000; round++)
        {
            using TextExporter exporter = TextExporter.CreateBuilder().ToStream(Stream.Null).WithMaxTextLength(0).Build();

            long roundTotal = 0;
            foreach (Packet packet in packets)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                _ = exporter.OnPacket(packet);
                roundTotal += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            exporter.OnFinish();
            total = Math.Min(total, roundTotal);
        }

        // An array of exactly the needed size per step would be 200 arrays of 2 to 5 KB (about 700 KB). The doubling pooled scratch
        // is rented a handful of times.
        await Assert.That(total).IsLessThan(50_000).Because(Invariant($"allocated {total} bytes"));
    }

    #endregion

    #region Strict mode counters

    /// <summary>Proves that in strict mode a packet the Text exporter fails to write counts as skipped and as an error.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StrictModeCountsAFailedPacketAsSkippedAndAsAnError()
    {
        using FailingStream stream = new() { ThrowAfterByte = 0 };
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
    }

    /// <summary>Proves that in strict mode a row the CSV exporter fails to write counts as skipped and as an error.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StrictModeCountsAFailedCsvRowAsSkippedAndAsAnError()
    {
        using FailingStream stream = new() { ThrowAfterByte = 0 };
        using CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithHeader(false).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
    }

    #endregion
}
