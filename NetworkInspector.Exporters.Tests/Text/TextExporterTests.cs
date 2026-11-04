// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FailingStream = NetworkInspector.Exporters.Tests.Helpers.TestStreams.FailingStream;

namespace NetworkInspector.Exporters.Tests.Text;

/// <summary>
/// Tests for the <see cref="TextExporter"/> — validates builder configuration, detail levels,
/// output content, export lifecycle, statistics, and cancellation.
/// </summary>
internal sealed class TextExporterTests
{
    #region Offering packets

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

    #endregion

    #region Builder identity and output

    /// <summary>Proves that building a Text exporter without an output target throws <see cref="InvalidOperationException"/>.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuilderRequiresOutput()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    /// <summary>Proves that an exporter built without a name reports the default UI name.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task UiNameReturnsDefault()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.UiName).IsEqualTo("Text Exporter");
    }

    /// <summary>Proves that an exporter reports the UI name the builder was given.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task UiNameReturnsCustomValue()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("My Text Exporter")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("My Text Exporter");
    }

    /// <summary>Proves that an exporter built without a description reports none.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DescriptionReturnsNullWhenNotSet()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.Description).IsNull();
    }

    /// <summary>Proves that an exporter reports the description the builder was given.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DescriptionReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("Human-readable packet dump")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("Human-readable packet dump");
    }

    #endregion

    #region Basic output

    /// <summary>Proves that a single packet produces output that starts the packet with a numbered header line.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task SinglePacketProducesOutput()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        _Offer(exporter, packets[0]);
        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Each packet starts with "Packet N  [timestamp]"
        await Assert.That(text.Contains("Packet 1", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that several packets are all written, each with its numbered header, and counted as packets and as written.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task MultiplePacketsAllWritten()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 5;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            _Offer(exporter, packet);
        }

        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Each packet produces a numbered header line
        for (int i = 1; i <= count; i++)
        {
            await Assert.That(text.Contains(Invariant($"Packet {i}"), StringComparison.Ordinal)).IsTrue();
        }

        await Assert.That(exporter.PacketCount).IsEqualTo(count);
        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
    }

    /// <summary>Proves that an export without any packet writes a single newline.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task EmptyExportWritesMinimalNewline()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(ms.Length).IsEqualTo(1);
        await Assert.That(ms.ToArray()[0]).IsEqualTo((byte)'\n');
    }

    /// <summary>Proves that an export to a file writes readable UTF-8 text with the header of every packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FileOutputProducesValidText()
    {
        using TestDir dir = new("text_file");
        string path = dir.FilePath("output.txt");

        using TextExporter exporter = TextExporter.CreateBuilder().ToFile(path).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            _Offer(exporter, packet);
        }

        exporter.OnFinish();

        string text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);

        await Assert.That(text.Length).IsGreaterThan(0);
        await Assert.That(text.Contains("Packet 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("Packet 3", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that an export to a stream writes readable UTF-8 text with the header of every packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StreamOutputProducesValidText()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            _Offer(exporter, packet);
        }

        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        string text = Encoding.UTF8.GetString(ms.ToArray());
        await Assert.That(text.Contains("Packet 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("Packet 2", StringComparison.Ordinal)).IsTrue();
    }

    #endregion

    #region Detail levels and escaped control characters

    /// <summary>Proves that the Standard level separates packets with a blank line that comes after the packet's field tree.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DetailLevelStandardContainsSeparatorBlankLine()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDetailLevel(TextDetailLevel.Standard)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        _Offer(exporter, packets[0]);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // A blank separator line is produced after the field tree (two consecutive newlines).
        await Assert.That(text.Contains("\n\n", StringComparison.Ordinal)).IsTrue();

        // Separator must appear AFTER at least some field content (not before the tree)
        int separatorIdx = text.IndexOf("\n\n", StringComparison.Ordinal);
        int headerIdx = text.IndexOf("Packet ", StringComparison.Ordinal);
        await Assert.That(separatorIdx).IsGreaterThan(headerIdx);
    }

    /// <summary>Proves that the Summary level writes less output than the Standard level for the same packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DetailLevelSummaryProducesLessOutputThanStandard()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);

        using MemoryStream msSummary = new();
        using TextExporter summaryExporter = TextExporter.CreateBuilder()
            .ToStream(msSummary)
            .WithDetailLevel(TextDetailLevel.Summary)
            .Build();
        summaryExporter.OnPacket(packets[0]);
        summaryExporter.OnFinish();

        using MemoryStream msStandard = new();
        using TextExporter standardExporter = TextExporter.CreateBuilder()
            .ToStream(msStandard)
            .WithDetailLevel(TextDetailLevel.Standard)
            .Build();
        standardExporter.OnPacket(packets[0]);
        standardExporter.OnFinish();

        // Summary mode suppresses sub-fields, so output is shorter
        await Assert.That(msSummary.Length).IsLessThan(msStandard.Length);
    }

    /// <summary>Proves that the Summary level lists the protocol layers and leaves out their value fields.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DetailLevelSummaryOnlyShowsContainerFields()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);

        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDetailLevel(TextDetailLevel.Summary)
            .Build();
        _Offer(exporter, packets[0]);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Value fields produced by IPv4 and UDP protocols must be absent in Summary mode.
        await Assert.That(text.Contains("Source Address:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("Source Port:", StringComparison.Ordinal)).IsFalse();

        // The protocol layers themselves are listed: Summary is a structural overview, not an empty one.
        await Assert.That(text.Contains("Internet Protocol Version 4", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("User Datagram Protocol", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that the Standard level lists protocols and their value fields but no raw-bytes line.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StandardShowsProtocolsAndTheirValueFieldsButNoRawBytes()
    {
        string text = _Export(TextDetailLevel.Standard, PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(text).Contains("Internet Protocol Version 4");
        await Assert.That(text).Contains("Source Address: ");
        await Assert.That(text).Contains("Source Port: ");
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(
            text,
            "^\\s*\\w[\\w ]*: ([0-9A-F]{2} )+[0-9A-F]{2}$",
            System.Text.RegularExpressions.RegexOptions.Multiline)).IsFalse();
    }

    /// <summary>Proves that the Full level lists protocols, value fields, and the raw-bytes line.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FullShowsProtocolsValueFieldsAndRawBytes()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text", frameData: [0xAA, 0xBB, 0xCC]);

        string text = _Export(TextDetailLevel.Full, packet);

        await Assert.That(text).Contains("Test");
        await Assert.That(text).Contains("  Text: text");
        await Assert.That(text).Contains("  Bytes: AA BB CC");
    }

    /// <summary>Proves that the Standard and Summary levels leave out a raw-bytes leaf but keep its container; Summary also leaves out value fields.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task StandardAndSummaryOmitARawBytesLeafButKeepTheContainer()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text", frameData: [0xAA, 0xBB, 0xCC], containerText: "Test container");

        string standard = _Export(TextDetailLevel.Standard, packet);
        string summary = _Export(TextDetailLevel.Summary, packet);

        await Assert.That(standard).Contains("Test container");
        await Assert.That(standard).Contains("  Text: text");
        await Assert.That(standard.Contains("Bytes:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(summary).Contains("Test container");
        await Assert.That(summary.Contains("Text:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(summary.Contains("Bytes:", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>Exports one packet at the given detail level and returns the text.</summary>
    /// <param name="level">The detail level of the tree.</param>
    /// <param name="packet">The packet to export.</param>
    /// <returns>The exported text.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string _Export(TextDetailLevel level, Packet packet)
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithDetailLevel(level).Build())
        {
            _Offer(exporter, packet);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Proves that control characters in a value and in a custom text are written as visible escapes and cannot forge a packet header.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AControlCharacterInAValueIsWrittenEscaped()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "x\ty\u007fz\0", containerText: "a\nPacket 99  [x]\u001b[2J\r");

        string text = _Export(TextDetailLevel.Standard, packet);

        // The display text of the container stays on one line and the control characters are visible as text.
        await Assert.That(text).Contains("a\\nPacket 99  [x]\\x1b[2J\\r\n");
        await Assert.That(text).Contains("  Text: x\\ty\\x7fz\\x00\n");
        await Assert.That(text.Contains('\u001b', StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains('\r', StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("\nPacket 99", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Split('\n').Count(static line => line.StartsWith("Packet ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>Proves that the selected-fields and all-fields shapes escape control characters the same way as the tree.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AControlCharacterIsEscapedInTheFlatShapesToo()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "a\nPacket 99\u001b[2J");

        string selected = _ExportFlat(factory, packet, ExportShape.SelectedFields);
        string all = _ExportFlat(factory, packet, ExportShape.AllFields);

        await Assert.That(selected).Contains("tst.text=Text: a\\nPacket 99\\x1b[2J\n");
        await Assert.That(selected.Split('\n').Count(static line => line.Length > 0)).IsEqualTo(1);
        await Assert.That(all).Contains("tst.text[0]=Text: a\\nPacket 99\\x1b[2J\n");
        await Assert.That(all.Contains('\u001b', StringComparison.Ordinal)).IsFalse();
        await Assert.That(all.Contains("\nPacket 99", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>Exports one tree packet in a flat shape that selects <c>tst.text</c> and returns the text.</summary>
    /// <param name="factory">The factory that built the packet and knows its fields.</param>
    /// <param name="packet">The packet to export.</param>
    /// <param name="shape">The flat shape: selected fields or all fields.</param>
    /// <returns>The exported text.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string _ExportFlat(CustomFieldPacketFactory factory, Packet packet, ExportShape shape)
    {
        ExportContent selection = factory.SelectFields("tst.text");
        ExportContent content = shape == ExportShape.SelectedFields
            ? selection
            : new ExportContent { Shape = shape, FrameBytes = false, Info = false, CustomText = false };

        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithContent(content).Build())
        {
            _Offer(exporter, packet);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region Maximum text length

    /// <summary>Proves that a text limit of 0 writes a very long value whole, without an ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task MaxTextLengthZeroDoesNotTruncate()
    {
        // A string value of 1000 characters is far over the default limit of 256; with the limit 0 its line is written whole and
        // without the ellipsis.
        using CustomFieldPacketFactory factory = new();
        string value = new('x', 1000);
        Packet packet = factory.Create(1, value);
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDetailLevel(TextDetailLevel.Standard)
            .WithMaxTextLength(0)
            .Build();

        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();
        string line = Encoding.UTF8.GetString(ms.ToArray()).Split('\n').First(static l => l.StartsWith("  Text: ", StringComparison.Ordinal));

        await Assert.That(accepted).IsTrue();
        await Assert.That(line).IsEqualTo("  Text: " + value);
    }

    #endregion

    #region Finishing and disposing

    /// <summary>Proves that an exporter is not finished before <c>OnFinish</c> is called.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task IsFinishedFalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    /// <summary>Proves that an exporter is finished after <c>OnFinish</c> is called.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task IsFinishedTrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    /// <summary>Proves that a packet offered after the export finished is refused and not counted.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task OnPacketAfterFinishReturnsFalse()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        bool accepted = exporter.OnPacket(packets[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.PacketCount).IsEqualTo(0);
    }

    /// <summary>Proves that finishing an exporter twice does not throw and leaves the counters as they were.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DoubleFinishIsIdempotent()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        _Offer(exporter, packets[0]);

        // Calling OnFinish twice must not throw
        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    /// <summary>Proves that disposing an exporter twice does not throw and keeps what was written.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task DisposeIsIdempotent()
    {
        MemoryStream ms = new();
        TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        _Offer(exporter, packets[0]);

        // Disposing twice must not throw
        exporter.Dispose();
        exporter.Dispose();

        await Assert.That(ms.Length).IsGreaterThan(0);

        await ms.DisposeAsync().ConfigureAwait(false);
    }

    #endregion

    #region Cancellation

    /// <summary>Proves that after the cancellation token is cancelled the exporter refuses further packets.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task CancellationStopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);

        _Offer(exporter, packets[0]);
        _Offer(exporter, packets[1]);
        await cts.CancelAsync().ConfigureAwait(false);

        // After cancellation, OnPacket must return false
        bool accepted = exporter.OnPacket(packets[2]);

        await Assert.That(accepted).IsFalse();

        exporter.OnFinish();
    }

    /// <summary>Proves that an exporter reports itself finished once its cancellation token is cancelled.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task IsFinishedTrueAfterCancellation()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region Target packet count

    /// <summary>Proves that an exporter accepts only as many packets as the target packet count allows.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TargetPacketCountLimitsExport()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetPacketCount(3)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        int accepted = 0;
        foreach (Packet packet in packets)
        {
            if (exporter.OnPacket(packet))
            {
                accepted++;
            }
        }

        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(3);
        await Assert.That(accepted).IsEqualTo(3);
    }

    /// <summary>Proves that an exporter reports itself finished once the target packet count is reached.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task IsFinishedTrueAfterTargetReached()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetPacketCount(2)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.IsFinished).IsTrue();
        exporter.OnFinish();
    }

    #endregion

    #region Counters and write errors

    /// <summary>Proves that the packet and written counters follow the packets offered and that no skip or error is counted for them.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task PacketCountTracksCorrectly()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 7;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.PacketCount).IsEqualTo(count);
        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(exporter.HasErrors).IsFalse();

        exporter.OnFinish();
    }

    /// <summary>Proves that in tolerant mode every packet whose write fails is counted as skipped and raises the skipped event.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task SkippedCountIncreasesOnTolerantError()
    {
        // A stream that accepts zero bytes will cause each packet write to fail.
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(skippedRaised).IsEqualTo(3);
        await Assert.That(exporter.SkippedCount).IsEqualTo(3);
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    /// <summary>Proves that a failed write of the output is reported as an I/O error and the export carries on with the next packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFailedWriteOfTheOutputIsReportedAsAnIoError()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> errors = [];
        exporter.ItemSkipped += (_, error) => errors.Add(error);

        bool goesOn = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(goesOn).IsTrue();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.IoError);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
    }

    /// <summary>Proves that a packet without a field tree is skipped with a serialization error event instead of an exception.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task APacketWithoutAFieldTreeIsSkippedAsASerializationErrorWithoutAnException()
    {
        using MemoryStream stream = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> errors = [];
        exporter.ItemSkipped += (_, error) => errors.Add(error);
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(8)), in noTree);

        bool goesOn = exporter.OnPacket(packet);
        exporter.OnFinish();

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(goesOn).IsTrue();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(errors[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.PacketCount).IsEqualTo(0);
    }

    /// <summary>Proves that in strict mode a packet without a field tree stops the export and raises no skipped event.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task APacketWithoutAFieldTreeStopsAStrictExportWithoutAnEvent()
    {
        using MemoryStream stream = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;
        int raised = 0;
        exporter.ItemSkipped += (_, _) => raised++;
        ParseOptions noTree = new(FieldTreeMode.Skip);
        Packet packet = TestHarness.ParseFrame(TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(8)), in noTree);

        bool goesOn = exporter.OnPacket(packet);

        await Assert.That(goesOn).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(raised).IsEqualTo(0);
    }

    /// <summary>Proves that a text that fails while it is formatted is reported as a serialization error and leaves no partial packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFaultyTextIsReportedAsASerializationErrorAndLeavesNoPartialPacket()
    {
        using CustomFieldPacketFactory factory = new();
        using MemoryStream stream = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> errors = [];
        exporter.ItemSkipped += (_, error) => errors.Add(error);

        bool goesOn = exporter.OnPacket(factory.Create(1, "text", faultyText: true));
        exporter.OnFinish();

        await Assert.That(goesOn).IsTrue();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(exporter.PacketCount).IsEqualTo(0);
        await Assert.That(stream.Length).IsEqualTo(0); // the failed packet wrote nothing, not even its header
    }

    #endregion

    #region Packet header

    /// <summary>Proves that the packet header line ends with the timestamp in ISO 8601 form with nine fractional digits and the UTC marker.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task PacketHeaderContainsTimestamp()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        bool accepted = exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string header = Encoding.UTF8.GetString(ms.ToArray()).Split('\n')[0];

        // The header line ends with the timestamp in ISO 8601 form with nine fractional digits and the Z of UTC.
        await Assert.That(accepted).IsTrue();
        await Assert.That(header.EndsWith("[1970-01-01T00:00:00.000000000Z]", StringComparison.Ordinal)).IsTrue();
    }

    #endregion

    #region Builder validation of text length and detail level

    /// <summary>
    /// <see cref="TextExporter.Builder.WithMaxTextLength"/> must throw
    /// <see cref="ArgumentOutOfRangeException"/> when a negative value is supplied.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuilderWithMaxTextLengthNegativeThrowsArgumentOutOfRangeException()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();

        await Assert.That(() => builder.WithMaxTextLength(-1)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Proves that the builder accepts a text limit of 0, which means unlimited.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuilderWithMaxTextLengthZeroAccepted()
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        // Zero means "unlimited" — must not throw
        builder.WithMaxTextLength(0);

        await Assert.That(() => builder.Build().Dispose()).ThrowsNothing();
    }

    /// <summary>Proves that the builder accepts a positive text limit.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuilderWithMaxTextLengthPositiveAccepted()
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        builder.WithMaxTextLength(512);

        await Assert.That(() => builder.Build().Dispose()).ThrowsNothing();
    }

    /// <summary>Proves that the builder refuses a detail level value that is not defined.</summary>
    /// <param name="level">The undefined detail level.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments((TextDetailLevel)3)]
    [Arguments((TextDetailLevel)7)]
    [Arguments((TextDetailLevel)255)]
    public async Task AnUndefinedDetailLevelIsRefused(TextDetailLevel level)
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();

        await Assert.That(() => builder.WithDetailLevel(level)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Proves that the builder accepts every defined detail level.</summary>
    /// <param name="level">The detail level.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(TextDetailLevel.Summary)]
    [Arguments(TextDetailLevel.Standard)]
    [Arguments(TextDetailLevel.Full)]
    public async Task EveryDefinedDetailLevelIsAccepted(TextDetailLevel level)
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        builder.WithDetailLevel(level);

        await Assert.That(() => builder.Build().Dispose()).ThrowsNothing();
    }

    #endregion

    #region Builder validation of the target packet count

    /// <summary>
    /// <see cref="TextExporter.Builder.WithTargetPacketCount"/> must reject
    /// negative values with <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuilderWithTargetPacketCountNegativeThrowsArgumentOutOfRangeException()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();

        await Assert.That(() => builder.WithTargetPacketCount(-1)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Proves that the builder accepts a target packet count of 0, which means unlimited.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task BuilderWithTargetPacketCountZeroAccepted()
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        // Zero means "unlimited" — must not throw
        builder.WithTargetPacketCount(0);

        await Assert.That(() => builder.Build().Dispose()).ThrowsNothing();
    }

    #endregion

    #region Timestamps before the epoch

    /// <summary>
    /// A negative nanosecond timestamp (before Unix epoch) must produce a valid
    /// ISO 8601 timestamp with non-negative fractional seconds in the text output.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TimestampPreEpochFormatsCorrectly()
    {
        // -500_000_000 ns = 1969-12-31T23:59:59.500000000Z
        const long preEpochNanos = -500_000_000L;

        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet packet = PacketGenerators.CreateParsedPacket(
            0,
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            preEpochNanos);

        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();

        // The packet header line holds the timestamp; the protocol lines below it contain other 'Z' characters.
        string header = Encoding.UTF8.GetString(ms.ToArray()).Split('\n')[0];

        // The documented text of half a second before the epoch: the seconds go back, the fraction stays non-negative.
        await Assert.That(accepted).IsTrue();
        await Assert.That(header.Contains("1969-12-31T23:59:59.500000000Z", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A timestamp of exactly -1_000_000_000 ns (-1 second)
    /// must format as ...T23:59:59.000000000Z, not ...T23:59:59.-000000000Z.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TimestampExactlyMinusOneSecondFormatsCorrectly()
    {
        const long minusOneSecondNanos = -1_000_000_000L;

        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet packet = PacketGenerators.CreateParsedPacket(
            0,
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            minusOneSecondNanos);

        bool accepted = exporter.OnPacket(packet);
        exporter.OnFinish();

        string header = Encoding.UTF8.GetString(ms.ToArray()).Split('\n')[0];

        // Exactly one second before the epoch is a whole second with a zero fraction, never a negative fraction.
        await Assert.That(accepted).IsTrue();
        await Assert.That(header.Contains("1969-12-31T23:59:59.000000000Z", StringComparison.Ordinal)).IsTrue();
    }

    #endregion
}
