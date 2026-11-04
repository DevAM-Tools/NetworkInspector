// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The feed of the row exporters (CSV, JSON, Text, PBF Standard) when a plugin dissector's custom text throws: the packet is
/// skipped with a message that names the cause, <c>OnPacket</c> does not throw, and the next healthy packet is written. The same
/// failure inside a stage's <c>EndPacket</c> (tree-free parse) is refused with the same kind of message, in the row stage and in
/// the columnar batch.
/// </summary>
internal sealed class RowFeedTests
{
    #region Helpers

    private static ExportContent _Content(CustomFieldPacketFactory factory) => factory.SelectFields("tst.text") with { CustomText = true };

    /// <summary>Feeds a faulty tree packet and a healthy one; returns the skip events and the skipped count.</summary>
    private static (List<ExportErrorEventArgs> Errors, int Skipped, int Written) _Feed(
        CustomFieldPacketFactory factory, IPacketListener exporter, Func<int> skipped, Func<int> written)
    {
        List<ExportErrorEventArgs> errors = [];
        if (exporter is IErrorTolerantExporter events)
        {
            events.ItemSkipped += (_, error) => errors.Add(error);
        }

        Packet faulty = factory.Create(1, "boom", faultyText: true);
        bool first = exporter.OnPacket(faulty);
        Packet healthy = factory.Create(2, "fine");
        bool second = exporter.OnPacket(healthy);
        exporter.OnFinish();
        _ = first;
        _ = second;
        return (errors, skipped(), written());
    }

    #endregion

    #region Exporters

    [Test]
    public async Task CsvSkipsAPacketWhoseTextThrowsAndWritesTheNext()
    {
        using CustomFieldPacketFactory factory = new();
        using MemoryStream stream = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithContent(_Content(factory)).Build();

        (List<ExportErrorEventArgs> errors, int skipped, int written) = _Feed(factory, exporter, () => exporter.SkippedCount, () => exporter.WrittenCount);

        await Assert.That(skipped).IsEqualTo(1);
        await Assert.That(written).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("text boom");
        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).Contains("fine");
    }

    [Test]
    public async Task JsonSkipsAPacketWhoseTextThrowsAndWritesTheNext()
    {
        using CustomFieldPacketFactory factory = new();
        using MemoryStream stream = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithContent(_Content(factory)).Build();

        (List<ExportErrorEventArgs> errors, int skipped, int written) = _Feed(factory, exporter, () => exporter.SkippedCount, () => exporter.WrittenCount);

        await Assert.That(skipped).IsEqualTo(1);
        await Assert.That(written).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("text boom");
        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).Contains("fine");
    }

    [Test]
    public async Task TextSkipsAPacketWhoseTextThrowsAndWritesTheNext()
    {
        using CustomFieldPacketFactory factory = new();
        using MemoryStream stream = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithContent(_Content(factory)).Build();

        (List<ExportErrorEventArgs> errors, int skipped, int written) = _Feed(factory, exporter, () => exporter.SkippedCount, () => exporter.WrittenCount);

        await Assert.That(skipped).IsEqualTo(1);
        await Assert.That(written).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("text boom");
        await Assert.That(Encoding.UTF8.GetString(stream.ToArray())).Contains("fine");
    }

    [Test]
    public async Task PbfStandardSkipsAPacketWhoseTextThrowsAndWritesTheNext()
    {
        using CustomFieldPacketFactory factory = new();
        using MemoryStream stream = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .WithContent(_Content(factory) with { FrameBytes = false })
            .Build();

        (List<ExportErrorEventArgs> errors, int skipped, int written) = _Feed(factory, exporter, () => exporter.SkippedCount, () => exporter.WrittenCount);

        await Assert.That(skipped).IsEqualTo(1);
        await Assert.That(written).IsEqualTo(1);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("text boom");
    }

    [Test]
    public async Task TheColumnarFeedSkipsAPacketWhoseTextThrowsAndKeepsTheNext()
    {
        using CustomFieldPacketFactory factory = new();
        using ColumnarPacketBatch batch = new(_Content(factory), maxPacketsPerBlock: 100, maxBlockSize: 1 << 20, isTimestampSorted: true);
        ExportFieldCollector collector = new(_Content(factory), batch);

        // The faulty packet is fed from its tree: the collector reads the text and the stage ends the packet, which throws inside the
        // feed. The guarded columnar feed turns that into a refusal; the batch stays as it was.
        bool added = ColumnarFeed.TryAdd(batch, collector, factory.Create(1, "boom", faultyText: true), out ExportErrorKind kind, out string? message);
        bool healthy = ColumnarFeed.TryAdd(batch, collector, factory.Create(2, "fine"), out _, out _);

        await Assert.That(added).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("text boom");
        await Assert.That(healthy).IsTrue();
        await Assert.That(batch.PacketCount).IsEqualTo(1);
    }

    #endregion

    #region Stages (tree-free parse)

    [Test]
    public async Task ARowStageWhoseTextThrowsInEndPacketRefusesTheCommitWithTheCause()
    {
        using RowFieldStage stage = new();
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "x");
        FieldValueData value = FieldValue.NewString("x").Data;
        LazyString faulty = LazyString.Lazy(static () => throw new InvalidOperationException("text boom"));

        stage.BeginPacket(packet.Id.Value, 0);
        stage.AddValue(factory.TextFieldId.Value, 0, in value, default, faulty);
        stage.EndPacket(complete: true);
        bool committed = stage.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("a custom text failed");
        await Assert.That(message).Contains("text boom");
    }

    [Test]
    public async Task AColumnarBatchWhoseTextThrowsInEndPacketRefusesTheCommitWithTheCause()
    {
        using CustomFieldPacketFactory factory = new();
        ExportContent content = _Content(factory);
        using ColumnarPacketBatch batch = new(content, maxPacketsPerBlock: 100, maxBlockSize: 1 << 20, isTimestampSorted: true);
        Packet packet = factory.Create(1, "x");
        FieldValueData value = FieldValue.NewString("x").Data;
        LazyString faulty = LazyString.Lazy(static () => throw new InvalidOperationException("text boom"));

        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(factory.TextFieldId.Value, 0, in value, default, faulty);
        batch.EndPacket(complete: true);
        bool committed = batch.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("a custom text failed");
        await Assert.That(batch.PacketCount).IsEqualTo(0);
    }

    #endregion
}
