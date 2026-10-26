// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FailingStream = NetworkInspector.Exporters.Tests.Helpers.TestStreams.FailingStream;

namespace NetworkInspector.Exporters.Tests.Text;

/// <summary>
/// Tests for the maximum text length of <see cref="TextExporter"/> (cut position and ellipsis) and for the counters in
/// Strict mode.
/// </summary>
internal sealed class TextExporterTruncationTests
{
    /// <summary>Returns the output line that starts with the given label (for example "  Text: "), without line end.</summary>
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

    private static string _Export(Packet packet, int maxTextLength, TextDetailLevel level)
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(stream)
            .WithMaxTextLength(maxTextLength)
            .WithDetailLevel(level)
            .Build())
        {
            exporter.OnPacket(packet);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Test]
    public async Task StringValueLongerThanTheLimitIsCutAtTheLimitAndGetsAnEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "abcdefghijklmnopqrst");

        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: abcdefghij…");
    }

    [Test]
    public async Task StringValueExactlyAtTheLimitIsWrittenWithoutEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "abcdefghij");

        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: abcdefghij");
    }

    [Test]
    public async Task StringValueOneCharacterOverTheLimitLosesExactlyThatCharacter()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "abcdefghijk");

        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: abcdefghij…");
    }

    [Test]
    public async Task NonAsciiStringValueIsCutBetweenCharactersNotBetweenBytes()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "äöüäöüäöüäöü");

        string output = _Export(packet, 5, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Text: ")).IsEqualTo("  Text: äöüäö…");
    }

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

    [Test]
    public async Task FormattedValueLongerThanTheLimitIsCutAtTheLimit()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text");

        // The timestamp text has 30 characters.
        string output = _Export(packet, 10, TextDetailLevel.Standard);

        await Assert.That(_Line(output, "  Timestamp: ")).IsEqualTo("  Timestamp: 1970-01-01…");
    }

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

    [Test]
    public async Task BytesFieldHexOutputNeverExceedsTheLimitBeforeTheEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, null, frameData: [0xAA, 0xBB, 0xCC, 0xDD]);

        // "aa bb" is five characters, one more than the limit of four, so only "aa" fits.
        string output = _Export(packet, 4, TextDetailLevel.Full);

        await Assert.That(_Line(output, "  Bytes: ")).IsEqualTo("  Bytes: aa…");
    }

    [Test]
    public async Task BytesFieldHexOutputUsesTheWholeLimitWhenItFitsExactly()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, null, frameData: [0xAA, 0xBB, 0xCC, 0xDD]);

        string output = _Export(packet, 5, TextDetailLevel.Full);

        await Assert.That(_Line(output, "  Bytes: ")).IsEqualTo("  Bytes: aa bb…");
    }

    [Test]
    public async Task BytesFieldThatFitsTheLimitIsWrittenWithoutEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, null, frameData: [0xAA, 0xBB]);

        string output = _Export(packet, 5, TextDetailLevel.Full);

        await Assert.That(_Line(output, "  Bytes: ")).IsEqualTo("  Bytes: aa bb");
    }

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
}
