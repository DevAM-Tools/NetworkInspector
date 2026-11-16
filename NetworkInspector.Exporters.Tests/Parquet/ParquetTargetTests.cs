// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// Tests for the Parquet dataset on an <see cref="IExportTarget"/>: the entries equal the files of a directory export, a refused
/// entry is an export error with the target's reason and leaves the other entries closed and readable, and an empty export opens
/// nothing.
/// </summary>
[Category("Slow")]
internal sealed class ParquetTargetTests
{
    #region Helpers

    /// <summary>The fixed entries of a hierarchy export in open order: export info, protocols, packets, topology.</summary>
    private const int _FixedEntryCount = 4;

    /// <summary>Content of a shape with frame bytes and buffers switched on.</summary>
    /// <param name="selected"><see langword="true"/> for the selected-fields shape, <see langword="false"/> for the hierarchy.</param>
    /// <returns>The content.</returns>
    private static ExportContent _Content(bool selected) =>
        (selected
            ? ExportContents.Selected(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"))
            : ExportContents.Hierarchy()) with
        {
            FrameBytes = true,
            Buffers = true,
        };

    /// <summary>Exports three packets onto a builder output and finishes.</summary>
    /// <param name="builder">The builder with its output already set.</param>
    /// <param name="packets">The packets to export.</param>
    /// <returns>The error events of the export.</returns>
    private static List<ExportErrorEventArgs> _Export(ParquetExporter.Builder builder, Packet[] packets)
    {
        using ParquetExporter exporter = builder.Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();
        return events;
    }

    /// <summary>Reads the shape of a Parquet file from its bytes.</summary>
    /// <param name="bytes">The bytes of a finished Parquet file.</param>
    /// <returns>The data column names, then the total row count, as one text.</returns>
    private static async Task<string> _SchemaAndRows(byte[] bytes)
    {
        using MemoryStream stream = new(bytes);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        long rows = 0;
        for (int group = 0; group < reader.RowGroupCount; group++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
            rows += rowGroup.RowCount;
        }

        string columns = string.Join(',', reader.Schema.GetDataFields().Select(static field => field.Name));
        return Invariant($"{columns}|{rows}");
    }

    /// <summary>Asserts that every entry but the named ones is closed and a readable Parquet file.</summary>
    /// <param name="target">The target after the export.</param>
    /// <param name="except">Entries that are not part of the check.</param>
    /// <returns>The task of the assertions.</returns>
    private static async Task _AssertEntriesClosedAndReadable(MemoryExportTarget target, string? except = null)
    {
        foreach (string name in target.EntryNames)
        {
            if (name == except)
            {
                continue;
            }

            await Assert.That(target.IsClosed(name)).IsTrue();
            await Assert.That((await _SchemaAndRows(target.Bytes(name))).Length).IsGreaterThan(0);
        }
    }

    #endregion

    #region Entries equal the directory export

    [Test]
    [Arguments(false, ExportWriteMode.Background)]
    [Arguments(false, ExportWriteMode.Synchronous)]
    [Arguments(true, ExportWriteMode.Background)]
    [Arguments(true, ExportWriteMode.Synchronous)]
    public async Task AForwardOnlyTargetReceivesTheEntriesOfTheDirectoryExport(bool selected, ExportWriteMode mode)
    {
        using TestDir dir = new("parquet_target_same");
        string folder = Path.Combine(dir.DirectoryPath, "dataset");
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        MemoryExportTarget target = new(seekable: false);

        _Export(ParquetExporter.CreateBuilder().ToTarget(target).WithContent(_Content(selected)).WithWriteMode(mode), packets);
        _Export(ParquetExporter.CreateBuilder().ToDirectory(folder).WithContent(_Content(selected)).WithWriteMode(mode), packets);

        string[] files = Directory.GetFiles(folder, "*.parquet", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] entries = target.EntryNames.Order(StringComparer.Ordinal).ToArray();
        await Assert.That(string.Join(';', entries)).IsEqualTo(string.Join(';', files));
        await Assert.That(entries.Length).IsGreaterThan(_FixedEntryCount - 1);
        foreach (string entry in entries)
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(Path.Combine(folder, entry.Replace('/', Path.DirectorySeparatorChar)));
            await Assert.That(target.IsClosed(entry)).IsTrue();
            await Assert.That(await _SchemaAndRows(target.Bytes(entry))).IsEqualTo(await _SchemaAndRows(fileBytes));
            await Assert.That(target.Bytes(entry).AsSpan().SequenceEqual(fileBytes)).IsTrue();
        }
    }

    [Test]
    public async Task TheProtocolsAndTheExportInfoAreTheFirstTwoEntriesOpened()
    {
        MemoryExportTarget target = new(seekable: false);

        _Export(ParquetExporter.CreateBuilder().ToTarget(target).WithContent(_Content(selected: false)),
            PacketGenerators.CreateEthernetUdpPackets(3));

        string[] firstTwo = target.EntryNames.Take(2).Order(StringComparer.Ordinal).ToArray();
        await Assert.That(string.Join(';', firstTwo)).IsEqualTo("export_info.parquet;protocols.parquet");
    }

    #endregion

    #region Refused entries

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task ARefusedProtocolsEntryIsAStartFailureWithTheReason(ExportWriteMode mode)
    {
        MemoryExportTarget target = new(refuse: static name => name.Text == "protocols.parquet"
            ? "share offline"
            : null);
        using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToTarget(target).WithWriteMode(mode).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);
        exporter.OnFinish();

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
        await Assert.That(events[0].Message).Contains("share offline");
    }

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task ARefusedFieldEntryLosesTheBatchAndLeavesTheOtherEntriesReadable(ExportWriteMode mode)
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        MemoryExportTarget probe = new();
        _Export(ParquetExporter.CreateBuilder().ToTarget(probe).WithMaxPacketsPerBlock(3).WithWriteMode(mode), packets);
        string refused = probe.EntryNames.First(static name => name.StartsWith("fields/", StringComparison.Ordinal));
        MemoryExportTarget target = new(refuse: name => name.Text == refused
            ? "share offline"
            : null);
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToTarget(target)
            .WithMaxPacketsPerBlock(3)
            .WithWriteMode(mode)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(exporter.SkippedCount).IsEqualTo(3);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Message).Contains("share offline");
        await Assert.That(target.EntryNames.Contains(refused)).IsFalse();
        await _AssertEntriesClosedAndReadable(target);
    }

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task ATargetWithALimitOfEntriesRefusesTheNextOneAndKeepsTheFirstOnesReadable(ExportWriteMode mode)
    {
        const int limit = _FixedEntryCount + 1;
        int opened = 0;
        MemoryExportTarget target = new(refuse: _ => Interlocked.Increment(ref opened) > limit
            ? Invariant($"The target accepts at most {limit} entries.")
            : null);
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        using ParquetExporter exporter = ParquetExporter.CreateBuilder()
            .ToTarget(target)
            .WithMaxPacketsPerBlock(3)
            .WithWriteMode(mode)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);

        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(events.Count).IsGreaterThan(0);
        await Assert.That(events.All(static args => args.Message.Contains("at most 5 entries", StringComparison.Ordinal))).IsTrue();
        await Assert.That(target.EntryNames.Count).IsEqualTo(limit);
        await _AssertEntriesClosedAndReadable(target);
    }

    #endregion

    #region Clearing an earlier dataset

    [Test]
    public async Task ToDirectoryRemovesTheSixFixedFilesAndTheFieldParquetFilesAndNothingElse()
    {
        using TestDir dir = new("parquet_target_clear_scope");
        string folder = Path.Combine(dir.DirectoryPath, "dataset");
        string fieldsFolder = Path.Combine(folder, "fields");
        Directory.CreateDirectory(fieldsFolder);
        string[] fixedNames =
        [
            "packets.parquet", "packet_buffers.parquet", "topology.parquet", "catalog.parquet", "protocols.parquet", "export_info.parquet",
        ];
        foreach (string name in fixedNames)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, name), "stale");
        }

        string staleField = Path.Combine(fieldsFolder, "field_999999.parquet");
        string foreignRoot = Path.Combine(folder, "notes.txt");
        string foreignField = Path.Combine(fieldsFolder, "keep.txt");
        string foreignParquet = Path.Combine(folder, "mine.parquet");
        await File.WriteAllTextAsync(staleField, "stale");
        await File.WriteAllTextAsync(foreignRoot, "mine");
        await File.WriteAllTextAsync(foreignField, "mine");
        await File.WriteAllTextAsync(foreignParquet, "mine");

        // A flat shape writes no topology and no buffers, so a stale copy of those two can only vanish through the clean-up.
        _Export(ParquetExporter.CreateBuilder().ToDirectory(folder).WithContent(ExportContents.Selected(new FieldRequest("ip.src"))),
            PacketGenerators.CreateEthernetUdpPackets(2));

        await Assert.That(File.Exists(staleField)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(folder, "packets.parquet"))).IsNotEqualTo("stale");
        await Assert.That(File.Exists(Path.Combine(folder, "topology.parquet"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(folder, "packet_buffers.parquet"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(foreignRoot)).IsEqualTo("mine");
        await Assert.That(await File.ReadAllTextAsync(foreignField)).IsEqualTo("mine");
        await Assert.That(await File.ReadAllTextAsync(foreignParquet)).IsEqualTo("mine");
    }

    [Test]
    public async Task ToTargetClearsNothingOnADirectoryTarget()
    {
        using TestDir dir = new("parquet_target_no_clear");
        string staleField = Path.Combine(dir.DirectoryPath, "fields", "field_999999.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(staleField)!);
        await File.WriteAllTextAsync(staleField, "stale");

        _Export(ParquetExporter.CreateBuilder().ToTarget(new DirectoryExportTarget(dir.DirectoryPath)),
            PacketGenerators.CreateEthernetUdpPackets(2));

        await Assert.That(await File.ReadAllTextAsync(staleField)).IsEqualTo("stale");
        await Assert.That(File.Exists(Path.Combine(dir.DirectoryPath, "packets.parquet"))).IsTrue();
    }

    #endregion

    #region Replaced output

    [Test]
    public async Task TheLastOutputCallWinsAndTheReplacedOutputCreatesNothing()
    {
        using TestDir dir = new("parquet_target_replaced");
        string replacedFolder = Path.Combine(dir.DirectoryPath, "replaced");
        string winningFolder = Path.Combine(dir.DirectoryPath, "wins");
        MemoryExportTarget winningTarget = new();
        MemoryExportTarget replacedTarget = new();
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);

        // ToDirectory then ToTarget: the target wins. ToTarget then ToDirectory: the folder wins.
        _Export(ParquetExporter.CreateBuilder().ToDirectory(replacedFolder).ToTarget(winningTarget), packets);
        _Export(ParquetExporter.CreateBuilder().ToTarget(replacedTarget).ToDirectory(winningFolder), packets);

        await Assert.That(Directory.Exists(replacedFolder)).IsFalse();
        await Assert.That(winningTarget.EntryNames.Count).IsGreaterThan(0);
        await Assert.That(replacedTarget.EntryNames.Count).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(winningFolder, "packets.parquet"))).IsTrue();
    }

    #endregion

    #region Empty export

    [Test]
    public async Task AnEmptyExportOpensNoEntryAndALatePacketOpensNone()
    {
        MemoryExportTarget target = new();
        using ParquetExporter exporter = ParquetExporter.CreateBuilder().ToTarget(target).Build();

        exporter.OnFinish();
        bool late = exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0]);

        await Assert.That(target.EntryNames.Count).IsEqualTo(0);
        await Assert.That(late).IsFalse();
    }

    [Test]
    public async Task TheBuilderRefusesANullTargetAndBuildsNothingWithoutAnOutput()
    {
        await Assert.That(static () => ParquetExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => ParquetExporter.CreateBuilder().Build()).Throws<InvalidOperationException>();
    }

    #endregion
}
