// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Csv;

/// <summary>
/// The buffers column of the CSV exporter: where the builder accepts it (packet rows and the selected-fields layout), where it
/// refuses it (the layouts that repeat the packet columns on every field row), the message of each refusal, and the cell format
/// (<c>&lt;index&gt;:&lt;HEX&gt;</c> records joined by <c>;</c>, an empty cell for a packet without additional buffers, quoting when
/// the delimiter could split the cell).
/// </summary>
internal sealed class CsvBuffersColumnTests
{
    #region Refusal messages, content, and writing

    /// <summary>The message a long layout gives for a frame or buffers column; the plugin options report the same text.</summary>
    /// <param name="shape">The export shape of the long layout.</param>
    /// <returns>The expected message.</returns>
    private static string _LongLayoutMessage(ExportShape shape) =>
        Invariant($"The Buffers column is only valid in packet rows and the selected-fields layout, not with the shape {shape}.");

    /// <summary>A content of the given shape with the members CSV can write (no info, no frame bytes, no buffers).</summary>
    /// <param name="shape">The export shape.</param>
    /// <returns>The content; the selected-fields shape selects <c>udp.dstport</c>.</returns>
    private static ExportContent _Content(ExportShape shape) =>
        shape == ExportShape.SelectedFields
            ? ExportContents.Selected(new FieldRequest("udp.dstport")) with { Info = false }
            : CsvExporter.ContentTemplate with { Shape = shape };

    /// <summary>Writes the packets of the probe factory with the columns the builder was given and returns the rows of the text.</summary>
    /// <param name="builder">The builder that writes to <paramref name="stream"/>.</param>
    /// <param name="stream">The stream the builder writes to.</param>
    /// <param name="packets">The packets to export, in order.</param>
    /// <returns>The non-empty lines of the written CSV text, the header first.</returns>
    private static string[] _Write(CsvExporter.Builder builder, MemoryStream stream, IEnumerable<Packet> packets)
    {
        using (CsvExporter exporter = builder.Build())
        {
            foreach (Packet packet in packets)
            {
                _ = exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    #endregion

    #region Refusals

    /// <summary>Proves that building a CSV exporter with a buffers column and a long layout is refused with the layout message, in either call order.</summary>
    /// <param name="shape">The long layout under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(ExportShape.AllFields)]
    [Arguments(ExportShape.Hierarchy)]
    public async Task ABuffersColumnNextToALongLayoutIsRefusedByBuildInEitherOrderOfTheCalls(ExportShape shape)
    {
        using MemoryStream stream = new();
        CsvExporter.Builder columnFirst = CsvExporter.CreateBuilder().ToStream(stream)
            .WithColumn(CsvColumnKind.Buffers).WithContent(_Content(shape));
        CsvExporter.Builder contentFirst = CsvExporter.CreateBuilder().ToStream(stream)
            .WithContent(_Content(shape)).WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Buffers);

        await Assert.That(() => columnFirst.Build()).Throws<ArgumentException>().WithMessageContaining(_LongLayoutMessage(shape));
        await Assert.That(() => contentFirst.Build()).Throws<ArgumentException>().WithMessageContaining(_LongLayoutMessage(shape));
    }

    /// <summary>Proves that the packet-rows layout and the selected-fields (wide) layout accept the buffers column.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ThePacketRowsAndTheWideLayoutAcceptTheBuffersColumn()
    {
        using MemoryStream stream = new();
        CsvExporter.Builder packetRows = CsvExporter.CreateBuilder().ToStream(stream).WithColumn(CsvColumnKind.Buffers);
        CsvExporter.Builder wide = CsvExporter.CreateBuilder().ToStream(stream)
            .WithContent(_Content(ExportShape.SelectedFields)).WithColumn(CsvColumnKind.Buffers);

        await Assert.That(() => packetRows.Build().Dispose()).ThrowsNothing();
        await Assert.That(() => wide.Build().Dispose()).ThrowsNothing();
    }

    /// <summary>Proves that a CSV content with the buffers member on is refused and the message names the column kind to use instead.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheContentOfCsvRefusesBuffersAndNamesTheColumnKindToUseInstead()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();

        await Assert.That(() => builder.WithContent(CsvExporter.ContentTemplate with { Buffers = true }))
            .Throws<ArgumentException>().WithMessageContaining("WithColumn(CsvColumnKind.Buffers)");
    }

    #endregion

    #region Cells

    /// <summary>Proves that the buffers column has the header <c>buffers</c> and that a packet without additional buffers has an empty cell.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheBuffersColumnHasTheHeaderBuffersAndAPacketWithoutBuffersHasAnEmptyCell()
    {
        using BufferProbePacketFactory factory = new();
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Buffers);

        string[] lines = _Write(builder, stream, [factory.CreatePacket(), factory.CreatePacket([0xAB])]);

        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("No.,buffers");
        await Assert.That(lines[1]).IsEqualTo("0,");
        await Assert.That(lines[2]).IsEqualTo("1,1:AB");
    }

    /// <summary>Proves that several buffers are records joined by semicolons in index order, and that an empty buffer is a record without digits.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task SeveralBuffersAreRecordsJoinedBySemicolonsInIndexOrderAndAnEmptyBufferIsARecordWithoutDigits()
    {
        using BufferProbePacketFactory factory = new();
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Buffers);
        byte[] large = new byte[300];
        Array.Fill(large, (byte)0x5C);

        string[] lines = _Write(builder, stream, [factory.CreatePacket([0x01, 0x02, 0x0F], [], large)]);

        // Upper-case hex without separators, one record per buffer, the index in decimal before the colon.
        await Assert.That(lines[1]).IsEqualTo("0,1:01020F;2:;3:" + string.Concat(Enumerable.Repeat("5C", 300)));
    }

    /// <summary>Proves that with both columns on, the frame column comes before the buffers column.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task FramesAndBuffersTogetherGiveTheFrameColumnBeforeTheBuffersColumn()
    {
        using BufferProbePacketFactory factory = new();
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.FrameBytes).WithColumn(CsvColumnKind.Buffers);

        string[] lines = _Write(builder, stream, [factory.CreatePacket([0x01])]);

        // The probe frame is 14 zero bytes.
        await Assert.That(lines[0]).IsEqualTo("No.,frame_bytes,buffers");
        await Assert.That(lines[1]).IsEqualTo("0," + new string('0', 28) + ",1:01");
    }

    /// <summary>Proves that a buffers cell is quoted as a whole when the delimiter is the semicolon that separates its records.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ABuffersCellIsQuotedWhenTheDelimiterIsTheSemicolonThatSeparatesItsRecords()
    {
        // The semicolon joins the records of the cell and is also a common CSV delimiter, so the cell would split at it and is quoted
        // as a whole. A digit, a letter, and the colon cannot be the delimiter at all (the builder refuses them), and any other
        // delimiter leaves the cell bare.
        char delimiter = ';';
        using BufferProbePacketFactory factory = new();
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithDelimiter(delimiter)
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Buffers);

        string[] lines = _Write(builder, stream, [factory.CreatePacket([0x01], [0x02])]);

        await Assert.That(lines[1]).IsEqualTo("0" + delimiter + "\"1:01;2:02\"");
    }

    /// <summary>Proves that a buffers cell is left bare when the delimiter cannot split its records.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ABuffersCellIsNotQuotedForAnOrdinaryDelimiter()
    {
        using BufferProbePacketFactory factory = new();
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithDelimiter('\t')
            .WithColumn(CsvColumnKind.PacketNumber).WithColumn(CsvColumnKind.Buffers);

        string[] lines = _Write(builder, stream, [factory.CreatePacket([0x01], [0x02])]);

        await Assert.That(lines[1]).IsEqualTo("0\t1:01;2:02");
    }

    #endregion
}
