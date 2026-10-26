// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Export and convert through registered formats: CSV, plugin kinds and formats, overwrite, and the frame/packet split.</summary>
internal sealed class CliExportFormatTests
{
    #region CSV

    [Test]
    public async Task CsvHasABomAHeaderAndTheDefaultColumns()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("out.csv");

        CliResult result = await sandbox.RunAsync("export", "random:count=20,seed=5,mode=udp4", "-o", output);

        await Assert.That(result.Exit).IsEqualTo(0);
        byte[] bytes = File.ReadAllBytes(output);
        await Assert.That(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })).IsTrue();
        string[] lines = File.ReadAllLines(output);
        await Assert.That(lines.Length).IsEqualTo(21);
        await Assert.That(lines[0].TrimStart('﻿')).IsEqualTo("No.,Time,Info,Length");
    }

    [Test]
    public async Task CsvOptionsChangeDelimiterHeaderBomAndColumns()
    {
        using CliSandbox sandbox = new();
        string plain = sandbox.Combine("plain.csv");
        string columns = sandbox.Combine("columns.csv");

        CliResult withOptions = await sandbox.RunAsync("export", "random:count=20,seed=5,mode=udp4", "-o", plain, "-f", "csv:delimiter=;,header=false,bom=false");
        CliResult withColumns = await sandbox.RunAsync("export", "random:count=20,seed=5,mode=udp4", "-o", columns, "-f", "csv:columns=ip.src;udp.dstport");

        await Assert.That(withOptions.Exit).IsEqualTo(0);
        string[] plainLines = File.ReadAllLines(plain);
        await Assert.That(plainLines.Length).IsEqualTo(20);
        await Assert.That(plainLines.All(static line => line.Count(static c => c == ';') >= 3)).IsTrue();
        await Assert.That(File.ReadAllBytes(plain).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })).IsFalse();
        await Assert.That(withColumns.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllLines(columns)[0].TrimStart('﻿')).IsEqualTo("ip.src,udp.dstport");
    }

    [Test]
    public async Task ABadCsvOptionIsAUsageErrorWithOneLine()
    {
        using CliSandbox sandbox = new();

        CliResult delimiter = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("a.csv"), "-f", "csv:delimiter=ab");
        CliResult column = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("b.csv"), "-f", "csv:columns=nope.field");
        CliResult unknown = await sandbox.RunAsync("export", "random:count=2", "-o", sandbox.Combine("c.csv"), "-f", "csv:colour=red");

        await Assert.That(delimiter.Exit).IsEqualTo(1);
        await Assert.That(delimiter.ErrorMessages).IsEquivalentTo(["Error: The delimiter must be exactly one character."], CollectionOrdering.Matching);
        await Assert.That(column.Exit).IsEqualTo(1);
        await Assert.That(column.ErrorMessages[0]).Contains("nope.field");
        await Assert.That(unknown.Exit).IsEqualTo(1);
        await Assert.That(unknown.ErrorMessages[0]).StartsWith("Error: Unknown option 'colour'; options: ");
        await Assert.That(File.Exists(sandbox.Combine("a.csv"))).IsFalse();
    }

    #endregion

    #region Plugin kinds and formats

    [Test]
    public async Task ThePluginSourceAndFormatWorkLikeBuiltInOnes()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));
        string udp = sandbox.Combine("udp.json");
        string tcp = sandbox.Combine("tcp.json");
        string text = sandbox.Combine("out.sampletxt");
        string noHeader = sandbox.Combine("noheader.sampletxt");

        CliResult udpRun = await sandbox.RunAsync("export", "SampleGen:count=10", "-o", udp);
        CliResult tcpRun = await sandbox.RunAsync("export", "SampleGen:count=10,transport=tcp,port=41234", "-o", tcp);
        CliResult textRun = await sandbox.RunAsync("export", "SampleGen:count=10", "-o", text, "-f", "SampleText");
        CliResult noHeaderRun = await sandbox.RunAsync("export", "SampleGen:count=10", "-o", noHeader, "-f", "SampleText:includeHeader=false");

        await Assert.That(udpRun.Exit).IsEqualTo(0);
        ExportedPackets udpPackets = ExportedPackets.Read(udp);
        await Assert.That(udpPackets.Count).IsEqualTo(10);
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(udpPackets.Number(i, "sample.counter")).IsEqualTo((long)i);
        }

        await Assert.That(tcpRun.Exit).IsEqualTo(0);
        ExportedPackets tcpPackets = ExportedPackets.Read(tcp);
        await Assert.That(Enumerable.Range(0, tcpPackets.Count).All(i => tcpPackets.Number(i, "sample.counter") is not null)).IsTrue();
        await Assert.That(textRun.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllLines(text).Length).IsEqualTo(11);
        await Assert.That(noHeaderRun.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllLines(noHeader).Length).IsEqualTo(10);
    }

    [Test]
    public async Task WithoutThePluginTheKindIsUnknown()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", "SampleGen:count=1", "-o", sandbox.Combine("x.json"));

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo(
            ["Error: Unknown source kind 'SampleGen'. Known kinds: BLF, PCAPNG, PCAP, ASC, Random."], CollectionOrdering.Matching);
    }

    #endregion

    #region Overwrite

    [Test]
    public async Task AnExistingOutputIsRefusedUnlessOverwriteIsGiven()
    {
        using CliSandbox sandbox = new();
        string json = sandbox.Combine("out.json");
        string pcapng = sandbox.Combine("out.pcapng");
        File.WriteAllText(json, "keep");
        File.WriteAllText(pcapng, "keep");

        CliResult refused = await sandbox.RunAsync("export", "random:count=5", "-o", json);
        CliResult convertRefused = await sandbox.RunAsync("convert", "random:count=5", "-o", pcapng);
        string keptJson = File.ReadAllText(json);
        string keptPcapng = File.ReadAllText(pcapng);
        CliResult replaced = await sandbox.RunAsync("export", "random:count=5", "-o", json, "--overwrite");
        CliResult convertReplaced = await sandbox.RunAsync("convert", "random:count=5", "-o", pcapng, "--overwrite");

        await Assert.That(refused.Exit).IsEqualTo(1);
        await Assert.That(refused.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{json}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
        await Assert.That(convertRefused.Exit).IsEqualTo(1);
        await Assert.That(convertRefused.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{pcapng}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
        await Assert.That(keptJson).IsEqualTo("keep");
        await Assert.That(keptPcapng).IsEqualTo("keep");
        await Assert.That(replaced.Exit).IsEqualTo(0);
        await Assert.That(convertReplaced.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(json)).IsNotEqualTo("keep");
        await Assert.That(new FileInfo(pcapng).Length).IsGreaterThan(4L);
    }

    [Test]
    public async Task AnExistingFirstSplitFileIsTheOneNamedInTheOverwriteRefusal()
    {
        using CliSandbox sandbox = new();
        string exportFirst = sandbox.Combine("part_00001.json");
        string convertFirst = sandbox.Combine("part_00001.pcapng");
        File.WriteAllText(exportFirst, "keep");
        File.WriteAllText(convertFirst, "keep");

        CliResult export = await sandbox.RunAsync("export", "random:count=5", "-o", sandbox.Combine("part.json"), "--split-count", "2");
        CliResult convert = await sandbox.RunAsync("convert", "random:count=5", "-o", sandbox.Combine("part.pcapng"), "--split-count", "2");

        // The file that exists is the first split file, not the base name the user typed (which is never written in a split run).
        await Assert.That(export.Exit).IsEqualTo(1);
        await Assert.That(export.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{exportFirst}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
        await Assert.That(convert.Exit).IsEqualTo(1);
        await Assert.That(convert.ErrorMessages).IsEquivalentTo(
            [$"Error: Output '{convertFirst}' exists; pass --overwrite to replace it."], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARefusedOverwriteRunKeepsThePreviousOutput()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("precious.csv");
        string converted = sandbox.Combine("precious.blf");
        File.WriteAllText(output, "my previous good export");
        File.WriteAllText(converted, "my previous good conversion");

        CliResult export = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", output, "-f", "csv:columns=nope.field",
            "--overwrite");
        CliResult convert = await sandbox.RunAsync("convert", "random:count=5,seed=1,mode=udp4", "-o", converted, "--output-format", "blf:compression=nope",
            "--overwrite");

        await Assert.That(export.Exit).IsEqualTo(1);
        await Assert.That(File.ReadAllText(output)).IsEqualTo("my previous good export");
        await Assert.That(convert.Exit).IsEqualTo(1);
        await Assert.That(File.ReadAllText(converted)).IsEqualTo("my previous good conversion");
        await Assert.That(Directory.GetFiles(sandbox.Root, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task ASuccessfulOverwriteRunLeavesNoBackupBehind()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("out.csv");
        File.WriteAllText(output, "old");

        CliResult result = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", output, "--overwrite");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(output)).DoesNotContain("old");
        await Assert.That(Directory.GetFiles(sandbox.Root, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task OverwriteNeverDeletesAFolderThatNoExportWrote()
    {
        using CliSandbox sandbox = new();
        string folder = sandbox.Combine("important-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");

        CliResult refused = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", folder, "-f", "parquet", "--overwrite");

        await Assert.That(refused.Exit).IsEqualTo(1);
        await Assert.That(refused.Error).Contains("was not written by an export");
        await Assert.That(File.ReadAllText(Path.Combine(folder, "notes.txt"))).IsEqualTo("mine");
    }

    [Test]
    public async Task ADatasetWrittenByAnExportIsReplacedByTheNextOverwriteRun()
    {
        using CliSandbox sandbox = new();
        string folder = sandbox.Combine("dataset");

        CliResult first = await sandbox.RunAsync("export", "random:count=5,seed=1,mode=udp4", "-o", folder, "-f", "parquet");
        CliResult second = await sandbox.RunAsync("export", "random:count=5,seed=2,mode=udp4", "-o", folder, "-f", "parquet", "--overwrite");

        await Assert.That(first.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(folder, ExportTargets.DatasetMarkerFileName))).IsTrue();
        await Assert.That(second.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(folder, ExportTargets.DatasetMarkerFileName))).IsTrue();
        await Assert.That(Directory.GetDirectories(sandbox.Root, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task ALeftoverSplitFileIsARuntimeErrorAndLeavesNoPartialOutput()
    {
        using CliSandbox sandbox = new();
        string leftover = sandbox.Combine("out_00002.json");
        File.WriteAllText(leftover, "keep");

        CliResult result = await sandbox.RunAsync("export", "random:count=5", "-o", sandbox.Combine("out.json"), "--split-count", "2");

        await Assert.That(result.Exit).IsEqualTo(3);
        await Assert.That(result.Error).Contains(leftover);
        await Assert.That(File.ReadAllText(leftover)).IsEqualTo("keep");
        await Assert.That(File.Exists(sandbox.Combine("out_00001.json"))).IsTrue();
        await Assert.That(File.Exists(sandbox.Combine("out_00003.json"))).IsFalse();
    }

    [Test]
    public async Task AnExportCancelledAfterTheFirstPacketDeletesTheOpenOutput()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("cancelled.json");
        using CancellationTokenSource cts = new();
        CancelAfterFirstPacketListener listener = new(output, cts);
        FrameInterfaceRegistry registry = new();
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, registry);
        builder.RegisterStandardProtocols();
        using Stack stack = builder.Build();
        RandomFrameSource source = new(count: 100, seed: 42, mode: RandomFrameMode.UdpIPv4);
        source.Start(registry.RegisterSource(source), registry);
        List<IFrameSource> sources = [source];
        int packetId = 0;

        // The listener cancels from inside the first packet, after it wrote the output: the output is open at that moment.
        int count = ExportCommand.RunExportLoop(sources, _ => listener, new SplitOutputManager(output, maxSize: 0, maxCount: 0), stack,
            filter: null, maxPackets: 0, progressInterval: 0, tolerant: false, TextWriter.Null, ref packetId, cts.Token, out int outputsWritten, out _);

        await Assert.That(listener.WasOpenWhenCancelled).IsTrue();
        await Assert.That(outputsWritten).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsFalse();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task ACancelledExportRunReportsCancelledAndLeavesNoOutputFile()
    {
        using CliSandbox sandbox = new();
        string output = sandbox.Combine("cancelled.json");
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        CliResult result = await sandbox.RunAsync(["export", "random:count=5", "-o", output], cts.Token);

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(output)).IsFalse();
    }

    // Writes its output with the first packet, then cancels the run: the output is open and unfinished when the loop notices.
    private sealed class CancelAfterFirstPacketListener(string path, CancellationTokenSource cancel) : IPacketListener, IDisposable
    {
        public string UiName => "cancel after first packet";

        public string? Description => null;

        public bool WasOpenWhenCancelled { get; private set; }

        public bool OnPacket(Packet packet)
        {
            File.WriteAllText(path, "partial");
            WasOpenWhenCancelled = File.Exists(path);
            cancel.Cancel();
            return true;
        }

        public void OnFinish() => throw new InvalidOperationException("A cancelled export must not be finished.");

        public void Dispose()
        {
        }
    }

    #endregion

    #region CSV formula neutralization

    [Test]
    public async Task TheCsvFormulaNeutralizationOptionIsAcceptedOnTheCommandLineAndBadValuesAreRefused()
    {
        using CliSandbox sandbox = new();

        CliResult off = await sandbox.RunAsync("export", "random:count=3", "-o", sandbox.Combine("off.csv"), "-f", "csv:neutralizeFormulas=false");
        CliResult on = await sandbox.RunAsync("export", "random:count=3", "-o", sandbox.Combine("on.csv"), "-f", "csv:neutralizeFormulas=true");
        CliResult bad = await sandbox.RunAsync("export", "random:count=3", "-o", sandbox.Combine("bad.csv"), "-f", "csv:neutralizeFormulas=maybe");

        await Assert.That(off.Exit).IsEqualTo(0);
        await Assert.That(on.Exit).IsEqualTo(0);
        await Assert.That(bad.Exit).IsEqualTo(1);
        await Assert.That(bad.ErrorMessages.Length).IsEqualTo(1);
        await Assert.That(bad.ErrorMessages[0]).Contains("neutralizeFormulas");
        await Assert.That(File.Exists(sandbox.Combine("bad.csv"))).IsFalse();
    }

    #endregion

    #region Frame and packet formats

    [Test]
    public async Task AFormatOfTheOtherKindNamesTheRightCommand()
    {
        using CliSandbox sandbox = new();

        CliResult exportBlf = await sandbox.RunAsync("export", "random:count=1", "-o", sandbox.Combine("a.blf"), "-f", "blf");
        CliResult convertCsv = await sandbox.RunAsync("convert", "random:count=1", "-o", sandbox.Combine("a.csv"), "--output-format", "csv");
        CliResult exportBlfExtension = await sandbox.RunAsync("export", "random:count=1", "-o", sandbox.Combine("b.blf"));
        CliResult convertUnknownExtension = await sandbox.RunAsync("convert", "random:count=1", "-o", sandbox.Combine("c.unknownext"));

        await Assert.That(exportBlf.Exit).IsEqualTo(1);
        await Assert.That(exportBlf.ErrorMessages).IsEquivalentTo(["Error: Format 'BLF' writes frames; use 'ni convert'."], CollectionOrdering.Matching);
        await Assert.That(convertCsv.Exit).IsEqualTo(1);
        await Assert.That(convertCsv.ErrorMessages).IsEquivalentTo(["Error: Format 'CSV' writes packets; use 'ni export'."], CollectionOrdering.Matching);
        await Assert.That(exportBlfExtension.Exit).IsEqualTo(1);
        await Assert.That(exportBlfExtension.ErrorMessages[0]).Contains("Packet formats: CSV (.csv)");
        await Assert.That(convertUnknownExtension.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(sandbox.Combine("c.unknownext"))).IsTrue();
    }

    #endregion
}
