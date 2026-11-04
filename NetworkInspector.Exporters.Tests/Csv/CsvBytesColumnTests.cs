// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Csv;

/// <summary>
/// The frame-bytes column of the CSV exporter: where the builder accepts it (packet rows and the selected-fields layout), where it
/// refuses it (the layouts that repeat the packet columns on every field row), the message of each refusal, and the cell format.
/// </summary>
internal sealed class CsvBytesColumnTests
{
    #region Refusal messages, content, and writing

    /// <summary>
    /// The message a long layout gives for a frame column: it names the column kind and the shape; the plugin option reports the same
    /// wording.
    /// </summary>
    /// <param name="shape">The export shape of the long layout.</param>
    /// <returns>The expected message.</returns>
    private static string _LongLayoutMessage(ExportShape shape) =>
        Invariant($"The FrameBytes column is only valid in packet rows and the selected-fields layout, not with the shape {shape}.");

    /// <summary>A content of the given shape with the members CSV can write (no info, no frame bytes).</summary>
    /// <param name="shape">The export shape.</param>
    /// <returns>The content; the selected-fields shape selects <c>udp.dstport</c>.</returns>
    private static ExportContent _Content(ExportShape shape) =>
        shape == ExportShape.SelectedFields
            ? ExportContents.Selected(new FieldRequest("udp.dstport")) with { Info = false }
            : CsvExporter.ContentTemplate with { Shape = shape };

    /// <summary>Writes one packet whose frame is the given bytes with the columns the builder was given and returns the text.</summary>
    /// <param name="builder">The builder that writes to <paramref name="stream"/>.</param>
    /// <param name="stream">The stream the builder writes to.</param>
    /// <param name="frame">The frame bytes of the single packet.</param>
    /// <returns>The written CSV text.</returns>
    private static string _Write(CsvExporter.Builder builder, MemoryStream stream, byte[] frame)
    {
        using (CsvExporter exporter = builder.Build())
        {
            _ = TreeFreeFeed.Run(exporter, [TestHarness.CreateFrame(0, 1_000_000, frame)]);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region Refusals

    /// <summary>Proves that building a CSV exporter with a frame column and a long layout is refused with the layout message, in either call order.</summary>
    /// <param name="shape">The long layout under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(ExportShape.AllFields)]
    [Arguments(ExportShape.Hierarchy)]
    public async Task AFrameColumnNextToALongLayoutIsRefusedByBuildInEitherOrderOfTheCalls(ExportShape shape)
    {
        using MemoryStream stream = new();
        CsvExporter.Builder columnFirst = CsvExporter.CreateBuilder().ToStream(stream)
            .WithColumn(CsvColumnKind.FrameBytes).WithContent(_Content(shape));
        CsvExporter.Builder contentFirst = CsvExporter.CreateBuilder().ToStream(stream)
            .WithContent(_Content(shape)).WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.FrameBytes);

        await Assert.That(() => columnFirst.Build()).Throws<ArgumentException>().WithMessageContaining(_LongLayoutMessage(shape));
        await Assert.That(() => contentFirst.Build()).Throws<ArgumentException>().WithMessageContaining(_LongLayoutMessage(shape));
    }

    /// <summary>Proves that the packet-rows layout and the selected-fields (wide) layout accept the frame column.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ThePacketRowsAndTheWideLayoutAcceptTheFrameColumn()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder packetRows = CsvExporter.CreateBuilder().ToStream(stream).WithColumn(CsvColumnKind.FrameBytes);
        CsvExporter.Builder wide = CsvExporter.CreateBuilder().ToStream(stream)
            .WithContent(_Content(ExportShape.SelectedFields)).WithColumn(CsvColumnKind.FrameBytes);

        await Assert.That(() => packetRows.Build().Dispose()).ThrowsNothing();
        await Assert.That(() => wide.Build().Dispose()).ThrowsNothing();
    }

    /// <summary>Proves that a CSV content with the frame-bytes or info member on is refused and the message names the column kind to use instead.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheContentOfCsvRefusesFrameBytesAndNamesTheColumnKindToUseInstead()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();

        await Assert.That(() => builder.WithContent(CsvExporter.ContentTemplate with { FrameBytes = true }))
            .Throws<ArgumentException>().WithMessageContaining("WithColumn(CsvColumnKind.FrameBytes)");
        await Assert.That(() => builder.WithContent(CsvExporter.ContentTemplate with { Info = true }))
            .Throws<ArgumentException>().WithMessageContaining("WithColumn(CsvColumnKind.Info)");
    }

    /// <summary>Proves that an undefined column kind is refused and that the message lists the frame-bytes kind among the valid ones.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AnUndefinedColumnKindIsStillRefusedAndTheMessageListsFrameBytes()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();

        await Assert.That(() => builder.WithColumn((CsvColumnKind)99)).Throws<ArgumentOutOfRangeException>().WithMessageContaining("FrameBytes");
    }

    #endregion

    #region Cells

    /// <summary>Proves that the packet rows write the default columns first and the frame column last, with its header.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ThePacketRowsWriteTheDefaultColumnsAndTheFrameColumnLastWithItsHeader()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithDefaultColumns()
            .WithColumn(CsvColumnKind.FrameBytes);

        string[] lines = _Write(builder, stream, [0x01, 0xAB]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines[0]).IsEqualTo("No.,Time,Info,Length,frame_bytes");
        await Assert.That(lines[1].EndsWith(",2,01AB", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that the frame column takes a custom header and that an empty frame is an empty cell.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFrameColumnTakesACustomHeaderAndAnEmptyFrameIsAnEmptyCell()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.FrameBytes, "raw");

        string[] lines = _Write(builder, stream, Array.Empty<byte>()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // The packet number is the dense id of the test thread's parse, so only the header and the end of the row are fixed.
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("No.,raw");
        await Assert.That(lines[1].EndsWith(',')).IsTrue();
        await Assert.That(lines[1].Count(static character => character == ',')).IsEqualTo(1);
    }

    /// <summary>Proves that the frame cell of a full-size Ethernet frame is its bytes as two hexadecimal digits each.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFrameCellOfALargeFrameHasTwoDigitsPerByte()
    {
        using MemoryStream stream = new();
        byte[] frame = new byte[1514];
        Array.Fill(frame, (byte)0x5C);
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.FrameBytes);

        string[] lines = _Write(builder, stream, frame).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines[1][(lines[1].IndexOf(',', StringComparison.Ordinal) + 1)..]).IsEqualTo(string.Concat(Enumerable.Repeat("5C", 1514)));
    }

    #endregion
}
