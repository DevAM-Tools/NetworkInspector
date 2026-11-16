// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Targets;

/// <summary>The single-file output formats that take a host target.</summary>
internal enum SingleFileFormat
{
    /// <summary>The CSV exporter.</summary>
    Csv,

    /// <summary>The JSON exporter.</summary>
    Json,

    /// <summary>The text exporter.</summary>
    Text,

    /// <summary>The PBF exporter in its standard shape.</summary>
    PbfStandard,

    /// <summary>The PBF exporter in its columnar shape.</summary>
    PbfColumnar,

    /// <summary>The PCAPNG exporter.</summary>
    Pcapng,

    /// <summary>The BLF exporter.</summary>
    Blf,

    /// <summary>The ASC exporter.</summary>
    Asc,
}

/// <summary>
/// Tests that every single-file exporter writes one <see cref="ExportEntryName.Main"/> entry onto a host target, also onto a
/// forward-only one, with the bytes of the stream shortcut; that an export without an item opens what a file export creates;
/// that a refused entry is an export error; and that a caller stream stays open.
/// </summary>
internal sealed class SingleFileTargetTests
{
    #region Output and run helpers

    /// <summary>
    /// The two packets every packet run offers. Shared, so two runs of one format see the same packet identities and write the same
    /// bytes (a packet carries its number in the output).
    /// </summary>
    private static readonly Packet[] _Packets = PacketGenerators.CreateEthernetUdpPackets(2);

    /// <summary>The two frames every frame run offers; shared for the same reason as <see cref="_Packets"/>.</summary>
    private static readonly Frame[] _Frames = PacketGenerators.CreateEthernetFrames(2);

    /// <summary>The way an exporter under test receives its output.</summary>
    internal enum OutputKind
    {
        /// <summary>A host target (<c>ToTarget</c>).</summary>
        Target,

        /// <summary>A caller stream (<c>ToStream</c>).</summary>
        Stream,

        /// <summary>A file path (<c>ToFile</c>).</summary>
        File,

        /// <summary>A file path, then a host target: the second call replaces the first (<c>ToFile</c>, <c>ToTarget</c>).</summary>
        FileThenTarget,

        /// <summary>A host target, then a file path: the second call replaces the first (<c>ToTarget</c>, <c>ToFile</c>).</summary>
        TargetThenFile,
    }

    /// <summary>One output of the kinds above; the members the kind names are set: the path and the target for a replacement kind.</summary>
    /// <param name="Kind">Which member is set.</param>
    /// <param name="Target">The target of kind <see cref="OutputKind.Target"/>.</param>
    /// <param name="Stream">The stream of kind <see cref="OutputKind.Stream"/>.</param>
    /// <param name="Path">The path of kind <see cref="OutputKind.File"/>.</param>
    internal sealed record Output(OutputKind Kind, IExportTarget? Target, Stream? Stream, string? Path)
    {
        /// <summary>An output through <c>ToTarget</c>.</summary>
        /// <param name="target">The target.</param>
        /// <returns>The output.</returns>
        internal static Output ToTarget(IExportTarget target) => new(OutputKind.Target, target, null, null);

        /// <summary>An output through <c>ToStream</c>.</summary>
        /// <param name="stream">The stream.</param>
        /// <returns>The output.</returns>
        internal static Output ToStream(Stream stream) => new(OutputKind.Stream, null, stream, null);

        /// <summary>An output through <c>ToFile</c>.</summary>
        /// <param name="path">The file path.</param>
        /// <returns>The output.</returns>
        internal static Output ToFile(string path) => new(OutputKind.File, null, null, path);

        /// <summary>An output that calls <c>ToFile</c> and then <c>ToTarget</c>.</summary>
        /// <param name="path">The file path of the replaced first call.</param>
        /// <param name="target">The target of the winning second call.</param>
        /// <returns>The output.</returns>
        internal static Output FileThenTarget(string path, IExportTarget target) => new(OutputKind.FileThenTarget, target, null, path);

        /// <summary>An output that calls <c>ToTarget</c> and then <c>ToFile</c>.</summary>
        /// <param name="target">The target of the replaced first call.</param>
        /// <param name="path">The file path of the winning second call.</param>
        /// <returns>The output.</returns>
        internal static Output TargetThenFile(IExportTarget target, string path) => new(OutputKind.TargetThenFile, target, null, path);

        /// <summary>The target of this output.</summary>
        /// <returns>The target.</returns>
        /// <exception cref="InvalidOperationException">The output is not of kind <see cref="OutputKind.Target"/>.</exception>
        internal IExportTarget AsTarget() => Target
            ?? throw new InvalidOperationException("The output is not a target.");

        /// <summary>The stream of this output.</summary>
        /// <returns>The stream.</returns>
        /// <exception cref="InvalidOperationException">The output is not of kind <see cref="OutputKind.Stream"/>.</exception>
        internal Stream AsStream() => Stream
            ?? throw new InvalidOperationException("The output is not a stream.");

        /// <summary>The path of this output.</summary>
        /// <returns>The path.</returns>
        /// <exception cref="InvalidOperationException">The output is not of kind <see cref="OutputKind.File"/>.</exception>
        internal string AsPath() => Path
            ?? throw new InvalidOperationException("The output is not a file.");
    }

    /// <summary>A started exporter of one format with the items it is fed and its statistics.</summary>
    /// <param name="Statistics">The error counter and event of the exporter.</param>
    /// <param name="Exporter">The exporter, disposed with the run.</param>
    /// <param name="Offer">Offers item number <c>index</c> and returns whether the exporter accepted it.</param>
    /// <param name="Finish">Finishes the export.</param>
    internal sealed record Run(IErrorTolerantExporter Statistics, IDisposable Exporter, Func<int, bool> Offer, Action Finish)
        : IDisposable
    {
        /// <summary>Disposes the exporter.</summary>
        public void Dispose() => Exporter.Dispose();
    }

    /// <summary>Wraps a packet exporter and the two shared packets into a run.</summary>
    /// <typeparam name="T">The exporter type.</typeparam>
    /// <param name="exporter">The exporter.</param>
    /// <returns>The run.</returns>
    private static Run _PacketRun<T>(T exporter)
        where T : IPacketListener, IErrorTolerantExporter, IDisposable
    {
        return new Run(exporter, exporter, index => exporter.OnPacket(_Packets[index]), exporter.OnFinish);
    }

    /// <summary>Wraps a frame exporter and the two shared frames into a run.</summary>
    /// <typeparam name="T">The exporter type.</typeparam>
    /// <param name="exporter">The exporter.</param>
    /// <returns>The run.</returns>
    private static Run _FrameRun<T>(T exporter)
        where T : IFrameListener, IErrorTolerantExporter, IDisposable
    {
        return new Run(exporter, exporter, index => exporter.OnFrame(_Frames[index]), exporter.OnFinish);
    }

    /// <summary>Builds the exporter of a format on an output; nothing is written yet.</summary>
    /// <param name="format">The format.</param>
    /// <param name="output">The output of the exporter.</param>
    /// <returns>The run.</returns>
    internal static Run Create(SingleFileFormat format, Output output)
    {
        switch (format)
        {
            case SingleFileFormat.Csv:
            {
                CsvExporter.Builder builder = CsvExporter.CreateBuilder();
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _PacketRun(builder.Build());
            }

            case SingleFileFormat.Json:
            {
                JsonExporter.Builder builder = JsonExporter.CreateBuilder();
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _PacketRun(builder.Build());
            }

            case SingleFileFormat.Text:
            {
                TextExporter.Builder builder = TextExporter.CreateBuilder();
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _PacketRun(builder.Build());
            }

            case SingleFileFormat.PbfStandard:
            case SingleFileFormat.PbfColumnar:
            {
                PbfExporter.Builder builder = PbfExporter.CreateBuilder()
                    .WithFormat(format == SingleFileFormat.PbfStandard
                        ? PbfExportFormat.Standard
                        : PbfExportFormat.Columnar);
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _PacketRun(builder.Build());
            }

            case SingleFileFormat.Pcapng:
            {
                PcapngExporter.Builder builder = PcapngExporter.CreateBuilder();
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _FrameRun(builder.Build());
            }

            case SingleFileFormat.Blf:
            {
                BlfExporter.Builder builder = BlfExporter.CreateBuilder();
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _FrameRun(builder.Build());
            }

            case SingleFileFormat.Asc:
            {
                AscExporter.Builder builder = AscExporter.CreateBuilder();
                builder = output.Kind switch
                {
                    OutputKind.Target => builder.ToTarget(output.AsTarget()),
                    OutputKind.Stream => builder.ToStream(output.AsStream()),
                    OutputKind.FileThenTarget => builder.ToFile(output.AsPath()).ToTarget(output.AsTarget()),
                    OutputKind.TargetThenFile => builder.ToTarget(output.AsTarget()).ToFile(output.AsPath()),
                    _ => builder.ToFile(output.AsPath()),
                };
                return _FrameRun(builder.Build());
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown single-file format.");
        }
    }

    /// <summary>Exports a number of items of a format onto an output and finishes.</summary>
    /// <param name="format">The format.</param>
    /// <param name="output">The output.</param>
    /// <param name="itemCount">How many of the two generated items to offer: 0, 1, or 2.</param>
    internal static void Export(SingleFileFormat format, Output output, int itemCount)
    {
        using Run run = Create(format, output);
        for (int index = 0; index < itemCount; index++)
        {
            run.Offer(index);
        }

        run.Finish();
    }

    /// <summary>
    /// Compares the output of two runs of one format. PBF writes the start time of the export into its header, so two runs differ
    /// in the first bytes; for PBF the length and everything after the header must match, for every other format all bytes.
    /// </summary>
    /// <param name="format">The format of both outputs.</param>
    /// <param name="left">The first output.</param>
    /// <param name="right">The second output.</param>
    /// <returns><see langword="true"/> when the outputs are the same apart from the PBF start time.</returns>
    internal static bool SameOutput(SingleFileFormat format, byte[] left, byte[] right)
    {
        const int pbfHeaderSize = 64;
        if (format is not (SingleFileFormat.PbfStandard or SingleFileFormat.PbfColumnar))
        {
            return left.AsSpan().SequenceEqual(right);
        }

        return left.Length == right.Length
            && left.AsSpan(pbfHeaderSize).SequenceEqual(right.AsSpan(pbfHeaderSize));
    }

    #endregion

    #region Entries of a target

    [Test]
    [Arguments(SingleFileFormat.Csv)]
    [Arguments(SingleFileFormat.Json)]
    [Arguments(SingleFileFormat.Text)]
    [Arguments(SingleFileFormat.PbfStandard)]
    [Arguments(SingleFileFormat.PbfColumnar)]
    [Arguments(SingleFileFormat.Pcapng)]
    [Arguments(SingleFileFormat.Asc)]
    public async Task AForwardOnlyTargetReceivesTheBytesOfTheStreamShortcut(SingleFileFormat format)
    {
        MemoryExportTarget target = new(seekable: false);
        using MemoryStream stream = new();

        Export(format, Output.ToTarget(target), 2);
        Export(format, Output.ToStream(stream), 2);

        await Assert.That(target.EntryNames.Count).IsEqualTo(1);
        await Assert.That(target.EntryNames[0]).IsEqualTo(string.Empty);
        await Assert.That(target.IsClosed(string.Empty)).IsTrue();
        await Assert.That(target.Bytes(string.Empty).Length).IsGreaterThan(0);
        await Assert.That(SameOutput(format, target.Bytes(string.Empty), stream.ToArray())).IsTrue();
    }

    [Test]
    public async Task ABlfEntryOnAForwardOnlyTargetKeepsThePlaceholderHeaderAndTheSameObjects()
    {
        const int headerSize = 144;
        MemoryExportTarget target = new(seekable: false);
        using MemoryStream stream = new();

        Export(SingleFileFormat.Blf, Output.ToTarget(target), 2);
        Export(SingleFileFormat.Blf, Output.ToStream(stream), 2);

        byte[] forwardOnly = target.Bytes(string.Empty);
        byte[] seekable = stream.ToArray();
        await Assert.That(forwardOnly.Length).IsEqualTo(seekable.Length);
        await Assert.That(forwardOnly.AsSpan(headerSize).SequenceEqual(seekable.AsSpan(headerSize))).IsTrue();
        await Assert.That(forwardOnly.AsSpan(0, headerSize).SequenceEqual(seekable.AsSpan(0, headerSize))).IsFalse();
    }

    [Test]
    [Arguments(SingleFileFormat.Csv)]
    [Arguments(SingleFileFormat.Json)]
    [Arguments(SingleFileFormat.Text)]
    [Arguments(SingleFileFormat.PbfStandard)]
    [Arguments(SingleFileFormat.PbfColumnar)]
    [Arguments(SingleFileFormat.Pcapng)]
    [Arguments(SingleFileFormat.Blf)]
    [Arguments(SingleFileFormat.Asc)]
    public async Task AnExportWithoutAnItemOpensWhatAFileExportCreates(SingleFileFormat format)
    {
        using TestDir dir = new("single_file_empty");
        string path = dir.FilePath("out.bin");
        MemoryExportTarget target = new(seekable: true);

        Export(format, Output.ToFile(path), 0);
        Export(format, Output.ToTarget(target), 0);

        await Assert.That(target.EntryNames.Count).IsEqualTo(File.Exists(path)
            ? 1
            : 0);
        if (File.Exists(path))
        {
            await Assert.That(SameOutput(format, target.Bytes(string.Empty), await File.ReadAllBytesAsync(path))).IsTrue();
        }
    }

    [Test]
    [Arguments(SingleFileFormat.Csv, 1)]
    [Arguments(SingleFileFormat.Csv, 2)]
    [Arguments(SingleFileFormat.Json, 1)]
    [Arguments(SingleFileFormat.Json, 2)]
    [Arguments(SingleFileFormat.Text, 1)]
    [Arguments(SingleFileFormat.Text, 2)]
    [Arguments(SingleFileFormat.PbfStandard, 1)]
    [Arguments(SingleFileFormat.PbfStandard, 2)]
    [Arguments(SingleFileFormat.PbfColumnar, 1)]
    [Arguments(SingleFileFormat.PbfColumnar, 2)]
    [Arguments(SingleFileFormat.Pcapng, 1)]
    [Arguments(SingleFileFormat.Pcapng, 2)]
    [Arguments(SingleFileFormat.Blf, 1)]
    [Arguments(SingleFileFormat.Blf, 2)]
    [Arguments(SingleFileFormat.Asc, 1)]
    [Arguments(SingleFileFormat.Asc, 2)]
    public async Task OneOrTwoItemsOpenExactlyOneEntry(SingleFileFormat format, int itemCount)
    {
        MemoryExportTarget target = new();

        Export(format, Output.ToTarget(target), itemCount);

        await Assert.That(target.EntryNames.Count).IsEqualTo(1);
    }

    #endregion

    #region Refusal and ownership

    [Test]
    [Arguments(SingleFileFormat.Csv)]
    [Arguments(SingleFileFormat.Json)]
    [Arguments(SingleFileFormat.Text)]
    [Arguments(SingleFileFormat.PbfStandard)]
    [Arguments(SingleFileFormat.PbfColumnar)]
    [Arguments(SingleFileFormat.Pcapng)]
    [Arguments(SingleFileFormat.Blf)]
    [Arguments(SingleFileFormat.Asc)]
    public async Task ARefusedMainEntryIsOneIoErrorAndNoException(SingleFileFormat format)
    {
        MemoryExportTarget target = new(refuse: static _ => "share offline");
        using Run run = Create(format, Output.ToTarget(target));
        List<ExportErrorEventArgs> events = [];
        run.Statistics.ItemSkipped += (_, args) => events.Add(args);

        bool accepted = run.Offer(0);
        run.Finish();

        if (format != SingleFileFormat.Text)
        {
            // The text exporter buffers its first lines and meets the refusal when the buffer is written, at the latest in
            // OnFinish; every other format opens its entry with the first item.
            await Assert.That(accepted).IsFalse();
        }

        await Assert.That(run.Statistics.ErrorCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.IoError);
        await Assert.That(events[0].Message).Contains("share offline");
    }

    [Test]
    [Arguments(SingleFileFormat.Csv)]
    [Arguments(SingleFileFormat.Json)]
    [Arguments(SingleFileFormat.Text)]
    [Arguments(SingleFileFormat.PbfStandard)]
    [Arguments(SingleFileFormat.PbfColumnar)]
    [Arguments(SingleFileFormat.Pcapng)]
    [Arguments(SingleFileFormat.Blf)]
    [Arguments(SingleFileFormat.Asc)]
    public async Task ACallerStreamStaysOpenAndKeepsItsPositionAfterTheExport(SingleFileFormat format)
    {
        using MemoryStream stream = new();

        Export(format, Output.ToStream(stream), 2);

        stream.WriteByte(0x7F);
        stream.Seek(0, SeekOrigin.Begin);
        int first = stream.ReadByte();
        await Assert.That(stream.CanWrite).IsTrue();
        await Assert.That(first).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task ABlfExportOntoACallerStreamAtANonZeroStartPatchesItsHeaderThere()
    {
        const int prefix = 7;
        using MemoryStream stream = new();
        using MemoryStream reference = new();
        stream.Write(new byte[prefix]);

        Export(SingleFileFormat.Blf, Output.ToStream(stream), 2);
        Export(SingleFileFormat.Blf, Output.ToStream(reference), 2);

        byte[] withPrefix = stream.ToArray();
        await Assert.That(withPrefix.Length).IsEqualTo(reference.ToArray().Length + prefix);
        await Assert.That(withPrefix.AsSpan(prefix).SequenceEqual(reference.ToArray())).IsTrue();
    }

    #endregion

    #region Null guards

    [Test]
    public async Task EveryBuilderRefusesANullStreamAndANullTargetAtTheCall()
    {
        await Assert.That(static () => CsvExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => CsvExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => JsonExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => JsonExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => TextExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => TextExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => PbfExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => PbfExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => PcapngExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => PcapngExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => BlfExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => BlfExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => AscExporter.CreateBuilder().ToStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => AscExporter.CreateBuilder().ToTarget(null!)).Throws<ArgumentNullException>();
    }

    #endregion
}
