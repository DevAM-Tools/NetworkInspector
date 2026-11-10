// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// The CLI on the parse plan: the <c>--verbose</c> line for each kind of run, the shared parse of a filter and an exporter, the
/// observer and the tree form of the filter, rotation of split outputs with staged fields, and the rule that an output without a
/// packet leaves no file behind.
/// </summary>
internal sealed class ParsePlanCliTests
{
    #region Helpers

    private const string _Source = "random:count=20,seed=5,mode=udp4";

    private static string _ParseLine(CliResult result) =>
        result.ErrorLines.Single(static line => line.StartsWith("parse:", StringComparison.Ordinal));

    private static Stack _BuildStack(FrameInterfaceRegistry registry)
    {
        using SettingsManager settingsManager = new();
        StackBuilder stackBuilder = new(settingsManager, registry);
        stackBuilder.RegisterStandardProtocols();
        return stackBuilder.Build();
    }

    private static RandomFrameSource _StartedSource(int count, FrameInterfaceRegistry registry, RandomFrameMode mode = RandomFrameMode.UdpIPv4)
    {
        RandomFrameSource source = new(count: count, seed: 11, mode: mode);
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return source;
    }

    #endregion

    #region The verbose line

    [Test]
    public async Task AnExportOfTwoFieldsRunsOnAParseWithoutAFieldTree()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", _Source, "-o", sandbox.Combine("out.duckdb"), "-f", "duckdb:fields=ip.src;udp.dstport",
            "--verbose");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(_ParseLine(result)).IsEqualTo("parse: fields only, no tree (2 fields, 1 observer)");
    }

    [Test]
    public async Task AnExportWithAFilterAndFieldsSharesOneTreeFreeParse()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", _Source, "-o", sandbox.Combine("out.csv"), "-f", "csv:fields=ip.src,text=false,repr=false",
            "--filter", "udp.length > 0", "--verbose");

        await Assert.That(result.Exit).IsEqualTo(0);

        // Two observers (the filter's and the exporter's) in one parse, still no field tree.
        await Assert.That(_ParseLine(result)).StartsWith("parse: fields only, no tree (");
        await Assert.That(_ParseLine(result)).Contains("2 observers");
        await Assert.That(File.ReadAllLines(sandbox.Combine("out.csv")).Length).IsEqualTo(21);
    }

    [Test]
    public async Task TheHierarchyAndAnExporterWithoutAPlanNeedTheFieldTree()
    {
        using CliSandbox sandbox = new();

        CliResult json = await sandbox.RunAsync("export", _Source, "-o", sandbox.Combine("out.json"), "-f", "json", "--verbose");

        await Assert.That(json.Exit).IsEqualTo(0);
        await Assert.That(_ParseLine(json)).IsEqualTo("parse: field tree");
    }

    [Test]
    public async Task CsvPacketRowsParseNoFieldTree()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", _Source, "-o", sandbox.Combine("out.csv"), "--verbose");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(_ParseLine(result)).IsEqualTo("parse: packet only, no tree");
        await Assert.That(File.ReadAllLines(sandbox.Combine("out.csv")).Length).IsEqualTo(21);
    }

    [Test]
    public async Task ConvertWithoutAFilterParsesNothingAndWithOneParsesWithoutATree()
    {
        using CliSandbox sandbox = new();

        CliResult plain = await sandbox.RunAsync("convert", _Source, "-o", sandbox.Combine("a.pcapng"), "--verbose");
        CliResult filtered = await sandbox.RunAsync("convert", _Source, "-o", sandbox.Combine("b.pcapng"), "--filter", "udp.length > 0", "--verbose");

        await Assert.That(plain.Exit).IsEqualTo(0);
        await Assert.That(_ParseLine(plain)).IsEqualTo("parse: none");
        await Assert.That(filtered.Exit).IsEqualTo(0);
        await Assert.That(_ParseLine(filtered)).StartsWith("parse: fields only, no tree (");
    }

    [Test]
    public async Task AFilterThatNeedsTheFieldTreeFallsBackAndSaysWhy()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", _Source, "-o", sandbox.Combine("out.csv"), "-f", "csv:fields=ip.src,text=false,repr=false",
            "--filter", "$udp[0] { udp.dstport > 0 }", "--verbose");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(_ParseLine(result)).StartsWith("parse: field tree");
        await Assert.That(_ParseLine(result)).Contains("— filter needs the field tree: ");
    }

    [Test]
    public async Task WithoutVerboseNothingIsPrinted()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", _Source, "-o", sandbox.Combine("out.csv"));

        await Assert.That(result.ErrorLines.Any(static line => line.StartsWith("parse:", StringComparison.Ordinal))).IsFalse();
    }

    #endregion

    #region Filter and shapes

    [Test]
    public async Task AFilterThatMatchesNothingLeavesNoOutputForEveryFormat()
    {
        using CliSandbox sandbox = new();

        foreach ((string format, string name) in new[] { ("json", "n.json"), ("csv:fields=ip.src", "n.csv"), ("duckdb:fields=ip.src", "n.duckdb"),
            ("parquet:fields=ip.src", "n_parquet"), ("pbf:format=columnar", "n.pbf"), ("text", "n.txt") })
        {
            string output = sandbox.Combine(name);
            CliResult result = await sandbox.RunAsync("export", _Source, "-o", output, "-f", format, "--filter", "udp.length == 1");

            await Assert.That(result.Exit).IsEqualTo(0);
            await Assert.That(result.Error).Contains("0 packets written to 0");
            await Assert.That(File.Exists(output) || Directory.Exists(output)).IsFalse();
        }
    }

    [Test]
    public async Task AFilterWritesOnlyTheMatchingPacketsForTheFieldShapes()
    {
        using CliSandbox sandbox = new();
        string all = sandbox.Combine("all.csv");
        string some = sandbox.Combine("some.csv");

        // Every second frame of the random source differs in the destination port or the length; the filter keeps a strict subset.
        CliResult everything = await sandbox.RunAsync("export", _Source, "-o", all, "-f", "csv:fields=udp.length,text=false,repr=false");
        long[] lengths = [.. File.ReadAllLines(all).Skip(1).Select(static line => long.Parse(line.Split(',')[^1], CultureInfo.InvariantCulture))];
        long threshold = lengths.OrderBy(static length => length).ElementAt(lengths.Length / 2);
        CliResult filtered = await sandbox.RunAsync("export", _Source, "-o", some, "-f", "csv:fields=udp.length,text=false,repr=false",
            "--filter", Invariant($"udp.length >= {threshold}"));

        long[] kept = [.. File.ReadAllLines(some).Skip(1).Select(static line => long.Parse(line.Split(',')[^1], CultureInfo.InvariantCulture))];

        await Assert.That(everything.Exit).IsEqualTo(0);
        await Assert.That(filtered.Exit).IsEqualTo(0);
        await Assert.That(kept.Length).IsEqualTo(lengths.Count(length => length >= threshold));
        await Assert.That(kept.All(length => length >= threshold)).IsTrue();
    }

    [Test]
    [Arguments("udp.length > 30")]
    [Arguments("udp.dstport > 20000")]
    [Arguments("udp")]
    [Arguments("ip.src != 0.0.0.0")]
    [Arguments("udp && ip.src != 0.0.0.0")]
    [Arguments("flank(udp.length, from: < 50, to: >= 50, within: 50packets)")]
    public async Task TheObserverFormAndTheTreeFormOfAFilterJudgeTheSamePackets(string expression)
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        List<int> observerIds = [];
        List<int> treeIds = [];

        foreach ((bool useObserver, List<int> ids) in new[] { (true, observerIds), (false, treeIds) })
        {
            using RandomFrameSource source = _StartedSource(200, registry);
            Collector collector = new(ids);
            StringWriter error = new();
            CliRunFilter? filter;
            if (useObserver)
            {
                await Assert.That(CliRunFilter.TryCompile(expression, stack, error, out filter)).IsTrue();
                await Assert.That(filter!.FallbackReason).IsNull();
            }
            else
            {
                await Assert.That(CliFilter.TryCompile(expression, stack, error, out IFilter? tree)).IsTrue();
                filter = CliRunFilter.FromTree(tree!, stack);
            }

            // Packet ids are dense per stack: the second pass continues where the first ended (a repeated id would be a replay,
            // which records no protocol presence in the index).
            int counter = useObserver
                ? 0
                : 200;
            _ = ExportCommand.RunExportLoop([source], _ => collector, new SplitOutputManager("c.json", 0, 0), stack, filter, 0, 0, false, error,
                planLog: null, ref counter, CancellationToken.None, out _, out _);
        }

        await Assert.That(observerIds.Count).IsGreaterThan(0);
        await Assert.That(observerIds).IsEquivalentTo(treeIds.Select(static id => id - 200).ToList());
    }

    #endregion

    #region Rotation

    [Test]
    public async Task SplitByCountWithStagedFieldsWritesEveryPacketOnceInThreeThreeThreeAndOne()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", "random:count=10,seed=3,mode=udp4", "-o", sandbox.Combine("s.csv"), "-f",
            "csv:fields=udp.length,text=false,repr=false", "--split-count", "3");

        string[] files = [.. Directory.GetFiles(sandbox.Root, "s_*.csv").Order(StringComparer.Ordinal)];
        int[] rows = [.. files.Select(static file => File.ReadAllLines(file).Length - 1)];

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(string.Join(",", rows)).IsEqualTo("3,3,3,1");
        await Assert.That(result.Error).Contains("10 packets written to 4");
    }

    [Test]
    public async Task NineSplitPacketsMakeThreeOutputsAndNoEmptyFourth()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("export", "random:count=9,seed=3,mode=udp4", "-o", sandbox.Combine("t.csv"), "-f",
            "csv:fields=udp.length,text=false,repr=false", "--split-count", "3");

        await Assert.That(Directory.GetFiles(sandbox.Root, "t_*.csv").Length).IsEqualTo(3);
        await Assert.That(result.Error).Contains("9 packets written to 3");
    }

    #endregion

    #region Test doubles

    /// <summary>Records the ids of the packets it receives; demands a packet-depth parse without a tree, like a tree-free exporter.</summary>
    private sealed class Collector(List<int> ids) : IPacketListener, IParseDemandSource
    {
        public string UiName => "collector";

        public string? Description => null;

        public ParseDemand ParseDemand => new() { Depth = ParseDepth.Packet };

        public bool OnPacket(Packet packet)
        {
            ids.Add(packet.Id.Value);
            return true;
        }

        public void OnFinish()
        {
        }
    }

    #endregion
}
