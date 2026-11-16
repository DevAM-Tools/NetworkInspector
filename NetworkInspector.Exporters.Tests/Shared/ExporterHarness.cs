// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Shared;

/// <summary>The nine exporter kinds of the shared contract tests.</summary>
internal enum ExporterKind
{
    /// <summary>The ASC exporter (frames).</summary>
    Asc,

    /// <summary>The BLF exporter (frames).</summary>
    Blf,

    /// <summary>The PCAPNG exporter (frames).</summary>
    Pcapng,

    /// <summary>The PBF exporter (packets).</summary>
    Pbf,

    /// <summary>The JSON exporter (packets).</summary>
    Json,

    /// <summary>The text exporter (packets).</summary>
    Text,

    /// <summary>The CSV exporter (packets).</summary>
    Csv,

    /// <summary>The Parquet exporter (packets).</summary>
    Parquet,

    /// <summary>The DuckDB exporter (packets); the one that needs a file.</summary>
    DuckDb,
}

/// <summary>
/// Builds one exporter of a kind on an in-memory target (DuckDB on a fresh path in a <see cref="TestDir"/>) and feeds it the
/// smallest valid items of its kind: frames for ASC, BLF, PCAPNG; packets of one shared stack for the others. Disposes the
/// exporter and the folder. Not thread-safe.
/// </summary>
internal sealed class ExporterHarness : IDisposable
{
    #region Configuration and state

    /// <summary>The items every harness offers: ten generated packets and ten frames, shared because they are only read.</summary>
    private const int _ItemCount = 10;

    /// <summary>The packets of the packet kinds.</summary>
    private static readonly Packet[] _Packets = PacketGenerators.CreateEthernetUdpPackets(_ItemCount);

    /// <summary>The frames of the frame kinds.</summary>
    private static readonly Frame[] _Frames = PacketGenerators.CreateEthernetFrames(_ItemCount);

    /// <summary>The error counter and the statistics of the exporter.</summary>
    private readonly IErrorTolerantExporter _Statistics;

    /// <summary>The exporter, disposed with the harness.</summary>
    private readonly IDisposable _Exporter;

    /// <summary>Offers item number <c>index</c> and returns the answer of the exporter.</summary>
    private readonly Func<int, bool> _Offer;

    /// <summary>Finishes the export.</summary>
    private readonly Action _Finish;

    /// <summary>The folder of the DuckDB file; <see langword="null"/> for the other kinds.</summary>
    private readonly TestDir? _Dir;

    /// <summary>The index of the next item to offer.</summary>
    private int _Next;

    /// <summary>Wraps a built exporter.</summary>
    /// <param name="statistics">The statistics of the exporter.</param>
    /// <param name="exporter">The exporter.</param>
    /// <param name="offer">Offers an item by index.</param>
    /// <param name="finish">Finishes the export.</param>
    /// <param name="uiName">The UI name the exporter reports.</param>
    /// <param name="description">The description the exporter reports.</param>
    /// <param name="dir">The folder to delete with the harness, or <see langword="null"/>.</param>
    private ExporterHarness(IErrorTolerantExporter statistics, IDisposable exporter, Func<int, bool> offer, Action finish,
        string uiName, string? description, TestDir? dir)
    {
        _Statistics = statistics;
        _Exporter = exporter;
        _Offer = offer;
        _Finish = finish;
        UiName = uiName;
        Description = description;
        _Dir = dir;
    }

    #endregion

    #region Building

    /// <summary>Builds the exporter of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="targetCount">The target item count; 0 for none.</param>
    /// <param name="uiName">The UI name to set, or <see langword="null"/> for the default.</param>
    /// <param name="description">The description to set, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">The token that cancels the export.</param>
    /// <returns>The harness; dispose it.</returns>
    internal static ExporterHarness Create(ExporterKind kind, int targetCount = 0, string? uiName = null, string? description = null,
        CancellationToken cancellationToken = default)
    {
        MemoryExportTarget target = new();
        switch (kind)
        {
            case ExporterKind.Asc:
            {
                AscExporter.Builder builder = AscExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetFrameCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _FrameHarness(builder.Build(), null);
            }

            case ExporterKind.Blf:
            {
                BlfExporter.Builder builder = BlfExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetFrameCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _FrameHarness(builder.Build(), null);
            }

            case ExporterKind.Pcapng:
            {
                PcapngExporter.Builder builder = PcapngExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetFrameCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _FrameHarness(builder.Build(), null);
            }

            case ExporterKind.Pbf:
            {
                PbfExporter.Builder builder = PbfExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetPacketCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _PacketHarness(builder.Build(), null);
            }

            case ExporterKind.Json:
            {
                JsonExporter.Builder builder = JsonExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetPacketCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _PacketHarness(builder.Build(), null);
            }

            case ExporterKind.Text:
            {
                TextExporter.Builder builder = TextExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetPacketCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _PacketHarness(builder.Build(), null);
            }

            case ExporterKind.Csv:
            {
                CsvExporter.Builder builder = CsvExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetPacketCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _PacketHarness(builder.Build(), null);
            }

            case ExporterKind.Parquet:
            {
                ParquetExporter.Builder builder = ParquetExporter.CreateBuilder().ToTarget(target).WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetPacketCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _PacketHarness(builder.Build(), null);
            }

            case ExporterKind.DuckDb:
            {
                TestDir dir = new("harness_duckdb");
                DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder()
                    .ToFile(dir.FilePath("harness.duckdb"))
                    .WithCancellationToken(cancellationToken);
                builder = targetCount > 0
                    ? builder.WithTargetPacketCount(targetCount)
                    : builder;
                builder = uiName is not null
                    ? builder.WithUiName(uiName)
                    : builder;
                builder = description is not null
                    ? builder.WithDescription(description)
                    : builder;
                return _PacketHarness(builder.Build(), dir);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown exporter kind.");
        }
    }

    /// <summary>Calls <c>Build()</c> of the builder of a kind that has no output configured.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>An action that throws when the builder refuses to build.</returns>
    internal static Action BuildWithoutOutput(ExporterKind kind) => kind switch
    {
        ExporterKind.Asc => static () => AscExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Blf => static () => BlfExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Pcapng => static () => PcapngExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Pbf => static () => PbfExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Json => static () => JsonExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Text => static () => TextExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Csv => static () => CsvExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.Parquet => static () => ParquetExporter.CreateBuilder().Build().Dispose(),
        ExporterKind.DuckDb => static () => DuckDbExporter.CreateBuilder().Build().Dispose(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown exporter kind."),
    };

    /// <summary>Wraps a frame exporter.</summary>
    /// <typeparam name="T">The exporter type.</typeparam>
    /// <param name="exporter">The exporter.</param>
    /// <param name="dir">The folder to delete with the harness, or <see langword="null"/>.</param>
    /// <returns>The harness.</returns>
    private static ExporterHarness _FrameHarness<T>(T exporter, TestDir? dir)
        where T : IFrameListener, IErrorTolerantExporter, IDisposable =>
        new(exporter, exporter, index => exporter.OnFrame(_Frames[index]), exporter.OnFinish, exporter.UiName, exporter.Description, dir);

    /// <summary>Wraps a packet exporter.</summary>
    /// <typeparam name="T">The exporter type.</typeparam>
    /// <param name="exporter">The exporter.</param>
    /// <param name="dir">The folder to delete with the harness, or <see langword="null"/>.</param>
    /// <returns>The harness.</returns>
    private static ExporterHarness _PacketHarness<T>(T exporter, TestDir? dir)
        where T : IPacketListener, IErrorTolerantExporter, IDisposable =>
        new(exporter, exporter, index => exporter.OnPacket(_Packets[index]), exporter.OnFinish, exporter.UiName, exporter.Description, dir);

    #endregion

    #region Driving the exporter

    /// <summary>Offers the next item.</summary>
    /// <returns>The answer of <c>OnFrame</c> or <c>OnPacket</c>.</returns>
    internal bool Offer()
    {
        int index = _Next;
        _Next++;
        return _Offer(index);
    }

    /// <summary>Finishes the export.</summary>
    internal void Finish() => _Finish();

    /// <summary>Whether the exporter reports itself finished.</summary>
    internal bool IsFinished => _Statistics.IsFinished;

    /// <summary>The number of items the exporter wrote.</summary>
    internal int WrittenCount => _Statistics.WrittenCount;

    /// <summary>The UI name the exporter reported when it was built.</summary>
    internal string UiName { get; }

    /// <summary>The description the exporter reported when it was built.</summary>
    internal string? Description { get; }

    /// <summary>Disposes the exporter and deletes the folder of a DuckDB kind.</summary>
    public void Dispose()
    {
        _Exporter.Dispose();
        _Dir?.Dispose();
    }

    #endregion
}
