// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// The catalog exporters (PBF columnar, Parquet, DuckDB) build their protocol catalog from the stack of the first packet. A
/// packet of another stack (a session restart between two <c>OnPacket</c> calls) would write field ids that name other fields
/// in that catalog, so it must be refused with <see cref="ExportErrorKind.StackMismatch"/> and the dataset of the earlier
/// packets must stay valid.
/// </summary>
internal sealed class ExporterStackMismatchTests
{
    #region Fixtures

    /// <summary>
    /// Two packets of this thread's stack followed by one packet of a second stack that shares the first stack's frame registry
    /// (as every stack of one session does).
    /// </summary>
    private static (Packet[] First, Packet Other) _TwoStacks()
    {
        Packet[] first = PacketGenerators.CreateEthernetUdpPackets(2);

        Stack own = TestHarness.GetStack();
        SettingsManager? settingsManager = new();
        Stack second;
        try
        {
            StackBuilder builder = new(settingsManager, own.FrameInterfaceRegistry);
            builder.RegisterStandardProtocols();
            second = builder.Build();
            settingsManager = null; // ownership transferred to the stack
        }
        finally
        {
            settingsManager?.Dispose();
        }

        Frame frame = TestHarness.CreateFrame(2, 2_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(32));
        ParseOptions options = new();
        if (!Packet.TryParse(0, second, frame, in options, out Packet? other, out ParseFailure failure) || other is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return (first, other);
    }

    private static List<ExportErrorEventArgs> _Collect(IErrorTolerantExporter exporter)
    {
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        return events;
    }

    #endregion

    #region Parquet

    [Test]
    [Category("Slow")]
    public async Task ParquetRefusesAPacketOfAnotherStackAndKeepsTheEarlierRowsValid()
    {
        MemoryExportTarget target = new(seekable: true);
        (Packet[] first, Packet other) = _TwoStacks();
        using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToTarget(target).Build();
        List<ExportErrorEventArgs> events = _Collect(exporter);

        bool a = exporter.OnPacket(first[0]);
        bool b = exporter.OnPacket(first[1]);
        bool refused = exporter.OnPacket(other);
        exporter.OnFinish();

        using MemoryStream stream = new(target.Bytes("packets.parquet"));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        long rows = 0;
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(i);
            rows += rowGroup.RowCount;
        }

        await Assert.That(a && b).IsTrue();
        await Assert.That(refused).IsFalse();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.StackMismatch);
        await Assert.That(events[0].ItemIndex).IsEqualTo(2);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.PacketCount).IsEqualTo(2);
        await Assert.That(rows).IsEqualTo(2);
    }

    #endregion

    #region DuckDB

    [Test]
    [Category("Slow")]
    public async Task DuckDbRefusesAPacketOfAnotherStackAndKeepsTheEarlierRowsValid()
    {
        using TestDir dir = new("duckdb_stack_mismatch");
        string outputPath = dir.FilePath("output.duckdb");
        (Packet[] first, Packet other) = _TwoStacks();
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(outputPath).Build();
        List<ExportErrorEventArgs> events = _Collect(exporter);

        bool a = exporter.OnPacket(first[0]);
        bool b = exporter.OnPacket(first[1]);
        bool refused = exporter.OnPacket(other);
        exporter.OnFinish();

        using DuckDBConnection connection = new(Invariant($"Data Source={outputPath}"));
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM packets;";
        long rows = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        await Assert.That(a && b).IsTrue();
        await Assert.That(refused).IsFalse();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.StackMismatch);
        await Assert.That(events[0].ItemIndex).IsEqualTo(2);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.PacketCount).IsEqualTo(2);
        await Assert.That(rows).IsEqualTo(2);
    }

    #endregion

    #region Row formats with a field selection

    [Test]
    public async Task CsvJsonAndTextRefuseAPacketOfAnotherStackThanTheirFieldSelection()
    {
        // A selection holds the field ids of one stack; the same ids name other fields on another stack, so a packet of another
        // stack must be refused instead of written with the wrong fields.
        ExportContent selected = ExportContents.Selected(new FieldRequest("udp.dstport"));
        FieldSelection? fields = selected.Fields;
        ExportContent csvContent = CsvExporter.ContentTemplate with
        {
            Shape = ExportShape.SelectedFields,
            Fields = fields,
            CustomText = false,
            CustomRepresentation = false,
        };
        ExportContent jsonContent = JsonExporter.DefaultContent with { Shape = ExportShape.SelectedFields, Fields = fields };
        ExportContent textContent = TextExporter.DefaultContent with { Shape = ExportShape.SelectedFields, Fields = fields };
        (Packet[] first, Packet other) = _TwoStacks();
        foreach (string format in new[] { "csv", "json", "text" })
        {
            using MemoryStream stream = new();
            IPacketListener exporter = format switch
            {
                "csv" => CsvExporter.CreateBuilder().ToStream(stream).WithContent(csvContent).Build(),
                "json" => JsonExporter.CreateBuilder().ToStream(stream).WithContent(jsonContent).Build(),
                _ => TextExporter.CreateBuilder().ToStream(stream).WithContent(textContent).Build(),
            };
            List<ExportErrorEventArgs> events = _Collect((IErrorTolerantExporter)exporter);

            bool ownStack = exporter.OnPacket(first[0]);
            bool refused = exporter.OnPacket(other);
            exporter.OnFinish();
            ((IDisposable)exporter).Dispose();

            await Assert.That(ownStack).IsTrue();
            await Assert.That(refused).IsFalse();
            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.StackMismatch);
            await Assert.That(((IExporterStatistics)exporter).WrittenCount).IsEqualTo(1);
        }
    }

    #endregion

    #region PBF

    [Test]
    public async Task PbfColumnarRefusesAPacketOfAnotherStackAndFinishesAValidFile()
    {
        using MemoryStream stream = new();
        (Packet[] first, Packet other) = _TwoStacks();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Columnar)
            .WithCompressed(false)
            .Build();
        List<ExportErrorEventArgs> events = _Collect(exporter);

        bool a = exporter.OnPacket(first[0]);
        bool b = exporter.OnPacket(first[1]);
        bool refused = exporter.OnPacket(other);
        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.FromBytes(stream.ToArray());
        await Assert.That(a && b).IsTrue();
        await Assert.That(refused).IsFalse();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.StackMismatch);
        await Assert.That(events[0].ItemIndex).IsEqualTo(2);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.PacketCount).IsEqualTo(2);
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
    }

    [Test]
    public async Task PbfStandardFormatDoesNotCheckTheStackBecauseItCarriesNoCatalog()
    {
        using MemoryStream stream = new();
        (Packet[] first, Packet other) = _TwoStacks();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .Build();
        List<ExportErrorEventArgs> events = _Collect(exporter);

        _ = exporter.OnPacket(first[0]);
        _ = exporter.OnPacket(first[1]);
        bool accepted = exporter.OnPacket(other);
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(events.Any(static e => e.Kind == ExportErrorKind.StackMismatch)).IsFalse();
        await Assert.That(exporter.PacketCount).IsEqualTo(3);
    }

    #endregion
}
