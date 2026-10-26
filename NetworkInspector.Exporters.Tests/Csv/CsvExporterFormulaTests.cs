// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Csv;

/// <summary>
/// Tests for the spreadsheet formula neutralization of <see cref="CsvExporter"/> (default on) and for the scratch buffer
/// growth of its field columns.
/// </summary>
internal sealed class CsvExporterFormulaTests
{
    /// <summary>Exports the packets with the given text and Info columns and returns the data lines (header excluded).</summary>
    private static string[] _ExportRows(
        CustomFieldPacketFactory factory, Packet[] packets, Action<CsvExporter.Builder>? configure = null)
    {
        using MemoryStream stream = new();
        CsvExporter.Builder builder = CsvExporter.CreateBuilder()
            .ToStream(stream)
            .WithBom(false)
            .WithColumn(CsvColumnKind.Info)
            .WithFieldColumn("tst.text", factory.TextFieldId)
            .WithFieldColumn("tst.number", factory.NumberFieldId);
        configure?.Invoke(builder);
        using (CsvExporter exporter = builder.Build())
        {
            foreach (Packet packet in packets)
            {
                exporter.OnPacket(packet);
            }

            exporter.OnFinish();
        }

        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        return lines[1..];
    }

    [Test]
    [Arguments("=1+1", "'=1+1")]
    [Arguments("+1", "'+1")]
    [Arguments("-1", "'-1")]
    [Arguments("@SUM(A1)", "'@SUM(A1)")]
    [Arguments("\tcmd", "'\tcmd")]
    public async Task TextFieldStartingWithAFormulaCharacterGetsALeadingApostrophe(string text, string expectedCell)
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(5, text)]);

        await Assert.That(rows[0]).IsEqualTo($",{expectedCell},5");
    }

    [Test]
    public async Task InfoColumnStartingWithAFormulaCharacterGetsALeadingApostrophe()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(5, "text", "=HYPERLINK(\"x\")")]);

        await Assert.That(rows[0]).StartsWith("\"'=HYPERLINK(\"\"x\"\")\",");
    }

    [Test]
    public async Task CarriageReturnAtTheStartIsPrefixedInsideTheQuotes()
    {
        using CustomFieldPacketFactory factory = new();

        // The CR also forces RFC 4180 quoting; the apostrophe goes inside the quotes. The raw output is read because the
        // row helper splits at CRLF.
        using MemoryStream stream = new();
        using (CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(stream)
            .WithBom(false)
            .WithHeader(false)
            .WithFieldColumn("tst.text", factory.TextFieldId)
            .Build())
        {
            exporter.OnPacket(factory.Create(5, "\rcmd"));
            exporter.OnFinish();
        }

        string raw = Encoding.UTF8.GetString(stream.ToArray());

        await Assert.That(raw).IsEqualTo("\"'\rcmd\"\r\n");
    }

    [Test]
    public async Task QuotedCellKeepsTheApostropheInsideTheQuotes()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(5, "=SUM(A1,A2)")]);

        await Assert.That(rows[0]).IsEqualTo(",\"'=SUM(A1,A2)\",5");
    }

    [Test]
    public async Task CellThatStartsWithAQuoteIsOnlyQuotedNotPrefixed()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(5, "\"=1\"")]);

        await Assert.That(rows[0]).IsEqualTo(",\"\"\"=1\"\"\",5");
    }

    [Test]
    public async Task NegativeNumericFieldIsNotPrefixed()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(-42, "text")]);

        await Assert.That(rows[0]).IsEqualTo(",text,-42");
    }

    [Test]
    public async Task OrdinaryTextIsUnchanged()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(1, "plain text", "info text")]);

        await Assert.That(rows[0]).IsEqualTo("info text,plain text,1");
    }

    [Test]
    public async Task EmptyTextIsUnchanged()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(factory, [factory.Create(1, string.Empty)]);

        await Assert.That(rows[0]).IsEqualTo(",,1");
    }

    [Test]
    public async Task NeutralizationCanBeSwitchedOff()
    {
        using CustomFieldPacketFactory factory = new();

        string[] rows = _ExportRows(
            factory,
            [factory.Create(1, "=1+1", "-note")],
            builder => builder.WithFormulaNeutralization(false));

        await Assert.That(rows[0]).IsEqualTo("-note,=1+1,1");
    }

    [Test]
    public async Task FieldColumnScratchGrowsGeometricallyForRisingValueLengths()
    {
        using CustomFieldPacketFactory factory = new();
        Packet[] packets = new Packet[90];
        for (int index = 0; index < packets.Length; index++)
        {
            // 171 chars and more need a UTF-8 worst case above the 512-byte stack limit, so the instance scratch is used.
            packets[index] = factory.Create(index, new string('a', 171 + index));

            // Expand the lazy field tree now, so the measurement below sees the exporter's own allocations only.
            _ = packets[index].FieldCount(materialize: true);
        }

        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(Stream.Null) // a sink that keeps nothing, so growing output buffers do not distort the measurement
            .WithBom(false)
            .WithHeader(false)
            .WithFieldColumn("tst.text", factory.TextFieldId)
            .Build();
        exporter.OnPacket(packets[0]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 1; index < packets.Length; index++)
        {
            exporter.OnPacket(packets[index]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // One array per row (about 520 to 760 bytes each) would add up to over 50 KB; doubling needs a single array.
        await Assert.That(allocated).IsLessThan(12_000L);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
    }
}
