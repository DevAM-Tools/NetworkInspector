// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// <see cref="ExportFieldCollector"/>: the selection and the occurrence policy, applied while the fields arrive, and the proof
/// that a tree-free parse and a walk of a finished tree produce the same entries. Uses the tunnel frame (IPv4 in IPv4), the one
/// packet in which a field occurs twice.
/// </summary>
internal sealed class ExportFieldCollectorTests
{
    #region Helpers

    private static FieldSelection _Select(params FieldRequest[] requests)
    {
        Stack stack = TestHarness.GetStack();
        if (!FieldSelection.TryCreate(stack, requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        return selection;
    }

    private static string _Address(in RowFieldStage.Entry entry) =>
        entry.Value.TryGetAsIPv4(out IPv4Address address)
            ? address.ToString(null, CultureInfo.InvariantCulture)
            : "<not an address>";

    /// <summary>Parses the frame without a tree through the collector's observer and returns the committed entries as words.</summary>
    private static List<string> _ParseFeed(ExportContent content, Frame frame)
    {
        using RowFieldStage stage = new();
        ExportFieldCollector collector = new(content, stage);
        ParseObserver[] observers = [collector.CreateDemand().Observer];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);

        Packet packet = TestHarness.ParseFrame(frame, in options);

        return stage.TryCommit(packet, out _, out _)
            ? _Words(stage)
            : ["<not committed>"];
    }

    /// <summary>Parses the frame with a tree and walks it through the collector; returns the committed entries as words.</summary>
    private static List<string> _TreeFeed(ExportContent content, Frame frame)
    {
        using RowFieldStage stage = new();
        ExportFieldCollector collector = new(content, stage);
        ParseOptions options = new(FieldTreeMode.Build);

        Packet packet = TestHarness.ParseFrame(frame, in options);
        collector.FeedTree(packet);

        return stage.TryCommit(packet, out _, out _)
            ? _Words(stage)
            : ["<not committed>"];
    }

    private static List<string> _Words(RowFieldStage stage)
    {
        List<string> words = [];
        foreach (RowFieldStage.Entry entry in stage.Entries)
        {
            string value = entry.Type == FieldType.None
                ? string.Empty
                : _Address(in entry);
            words.Add(Invariant($"{entry.FieldId}#{entry.Occurrence}={entry.Type}:{value}|{entry.Representation}|{entry.Text}"));
        }

        return words;
    }

    #endregion

    #region Occurrences

    [Test]
    public async Task FirstKeepsTheOuterAddressLastTheInnerAndAllBothWithOccurrenceNumbers()
    {
        foreach ((Occurrences choice, string[] expected) in new (Occurrences, string[])[]
        {
            (Occurrences.First, ["0=10.0.0.1"]),
            (Occurrences.Last, ["0=192.168.0.1"]),
            (Occurrences.All, ["0=10.0.0.1", "1=192.168.0.1"]),
        })
        {
            ExportContent content = new() { Shape = ExportShape.SelectedFields, Fields = _Select(new FieldRequest("ip.src", choice)) };
            using RowFieldStage stage = new();
            ExportFieldCollector collector = new(content, stage);
            ParseObserver[] observers = [collector.CreateDemand().Observer];
            ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
            Packet packet = TestHarness.ParseFrame(PacketGenerators.CreateTunnelFrame(), in options);

            bool committed = stage.TryCommit(packet, out _, out _);
            List<string> actual = [];
            foreach (RowFieldStage.Entry entry in stage.Entries)
            {
                actual.Add(Invariant($"{entry.Occurrence}={_Address(in entry)}"));
            }

            await Assert.That(committed).IsTrue();
            await Assert.That(actual).IsEquivalentTo(expected);
        }
    }

    [Test]
    public async Task APlainPacketGivesOneRowInEveryModeAndAPacketWithoutTheFieldGivesNone()
    {
        foreach (Occurrences choice in new[] { Occurrences.First, Occurrences.Last, Occurrences.All })
        {
            ExportContent content = new() { Shape = ExportShape.SelectedFields, Fields = _Select(new FieldRequest("ip.src", choice)) };

            List<string> plain = _ParseFeed(content, TestHarness.CreateFrame(
                0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(8)));
            List<string> can = _ParseFeed(content, PacketGenerators.CreateCanFrame());

            await Assert.That(plain.Count).IsEqualTo(1);
            await Assert.That(plain[0]).Contains("#0=");
            await Assert.That(can).IsEmpty();
        }
    }

    [Test]
    public async Task WithoutAChoiceTheTunnelPacketExportsBothAddresses()
    {
        ExportContent content = new() { Shape = ExportShape.SelectedFields, Fields = _Select(new FieldRequest("ip.src")) };

        List<string> words = _ParseFeed(content, PacketGenerators.CreateTunnelFrame());

        await Assert.That(words.Count).IsEqualTo(2);
        await Assert.That(words[0]).Contains("#0=");
        await Assert.That(words[1]).Contains("#1=");
    }

    #endregion

    #region Feeds

    [Test]
    public async Task TheParseFeedAndTheTreeFeedProduceIdenticalEntriesForTheMixedFrames()
    {
        ExportContent selected = new()
        {
            Shape = ExportShape.SelectedFields,
            Fields = _Select(new FieldRequest("ip.src"), new FieldRequest("udp.dstport"), new FieldRequest("can.id")),
        };
        ExportContent all = new() { Shape = ExportShape.AllFields };

        foreach (ExportContent content in new[] { selected, all })
        {
            foreach (Frame frame in PacketGenerators.CreateMixedFrames(6))
            {
                List<string> viaParse = _ParseFeed(content, frame);
                List<string> viaTree = _TreeFeed(content, frame);

                // The same values, texts, and occurrence numbers whichever way the fields arrive; for all fields the order of
                // arrival is the parse order in both feeds, too.
                await Assert.That(viaParse).IsEquivalentTo(viaTree);
            }
        }
    }

    [Test]
    public async Task AllFieldsExportsFarMoreValuesThanTwoSelectedFields()
    {
        ExportContent selected = new()
        {
            Shape = ExportShape.SelectedFields,
            Fields = _Select(new FieldRequest("udp.srcport"), new FieldRequest("udp.dstport")),
        };
        ExportContent all = new() { Shape = ExportShape.AllFields };
        Frame frame = TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(16));

        List<string> two = _ParseFeed(selected, frame);
        List<string> every = _ParseFeed(all, frame);

        await Assert.That(two.Count).IsEqualTo(2);
        await Assert.That(every.Count).IsGreaterThan(two.Count);
    }

    #endregion

    #region Content flags

    [Test]
    public async Task ClearedTextFlagsKeepRepresentationsAndTextsOutOfTheEntriesAndOutOfTheDemand()
    {
        FieldSelection selection = _Select(new FieldRequest("udp.dstport"));
        ExportContent withText = new() { Shape = ExportShape.SelectedFields, Fields = selection };
        ExportContent withoutText = withText with { CustomText = false, CustomRepresentation = false };
        using RowFieldStage stage = new();

        ParseObserver demandWith = new ExportFieldCollector(withText, stage).CreateDemand().Observer;
        ParseObserver demandWithout = new ExportFieldCollector(withoutText, stage).CreateDemand().Observer;

        // The display watch decides whether dissectors build texts at all in a tree-free parse.
        FieldId dstPort = TestHarness.GetStack().GetFieldId("udp.dstport")!.Value;
        await Assert.That(demandWith.DisplayText.Accepts(dstPort)).IsTrue();
        await Assert.That(demandWithout.DisplayText.IsNone).IsTrue();
        await Assert.That(demandWith.Watch.Accepts(dstPort)).IsTrue();
    }

    [Test]
    public async Task TheDemandIsATreeFreeParseAndAllFieldsWatchEverything()
    {
        using RowFieldStage stage = new();
        ExportContent all = new() { Shape = ExportShape.AllFields };

        ParseDemand demand = new ExportFieldCollector(all, stage).CreateDemand();

        await Assert.That(demand.Depth).IsEqualTo(ParseDepth.Packet);
        await Assert.That(demand.Observer.Watch.IsAll).IsTrue();
        await Assert.That(demand.Observer.DisplayText.IsAll).IsTrue();
    }

    [Test]
    public async Task TheHierarchyShapeIsRefusedByTheCollector()
    {
        using RowFieldStage stage = new();

        await Assert.That(() => new ExportFieldCollector(new(), stage)).Throws<ArgumentException>();
    }

    #endregion

    #region Allocation

    [Test]
    public async Task AWarmCollectorAndStageAddNoAllocationForScalarFieldsToTheParseItself()
    {
        Frame frame = TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(16));
        ExportContent content = new()
        {
            Shape = ExportShape.SelectedFields,
            Fields = _Select(new FieldRequest("udp.srcport"), new FieldRequest("udp.dstport")),
            CustomText = false,
            CustomRepresentation = false,
        };
        using RowFieldStage stage = new();
        ExportFieldCollector collector = new(content, stage);
        ParseObserver[] observers = [collector.CreateDemand().Observer];
        ParseOptions withCollector = new(FieldTreeMode.Skip, observers: observers);
        ParseOptions without = new(FieldTreeMode.Skip);

        // The first rounds grow the stage's lists and the pools of the parse; the harness and the parse itself allocate a little
        // per round (the packet id, the recycled packet's bookkeeping). That share is measured on the same frames without a
        // collector, so that only what the collector and the stage add is judged.
        long baseline = _MeasureRounds(frame, in without, commit: null);
        long measured = _MeasureRounds(frame, in withCollector, commit: stage);

        // Zero extra bytes: the staged entries live in lists that have grown during the warm-up, the values are copied by value.
        // A few KB of slack cover one-off lazy initialization and runtime noise (tiered JIT, event pipes); a real per-packet
        // allocation of even one small object would add tens of KB over the 1 000 measured rounds and fail.
        await Assert.That(measured).IsLessThanOrEqualTo(baseline + 8192);
    }

    /// <summary>
    /// Warms up for 50 rounds, then returns the fewest bytes the current thread allocates over one of up to five rounds of 1000 parses
    /// of the frame.
    /// </summary>
    /// <param name="frame">The frame that is parsed in every round.</param>
    /// <param name="options">The parse options; they decide whether a collector observes the parse.</param>
    /// <param name="commit">The stage that receives every parse, or <see langword="null"/> to measure the parse alone.</param>
    /// <returns>The smallest allocated byte count of a measured round.</returns>
    private static long _MeasureRounds(Frame frame, in ParseOptions options, RowFieldStage? commit)
    {
        Packet? recycle = null;
        for (int i = 0; i < 50; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            _ = commit?.TryCommit(recycle, out _, out _);
        }

        // The stage has no reset, so its lists keep growing across the rounds; the smallest round is one that no growth landed in.
        // A round that a one-off inflated is dropped the same way.
        long smallest = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                recycle = TestHarness.ParseFrame(frame, in options, recycle);
                _ = commit?.TryCommit(recycle, out _, out _);
            }

            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return smallest;
    }

    #endregion

    #region Sequence wrap and text demand

    [Test]
    public async Task AWrappedSequenceStartsEveryCounterAtZero()
    {
        using RowFieldStage stage = new();
        ExportFieldCollector collector = new(ExportContents.AllFields(), stage);
        const int fieldId = 3;
        FieldValue value = FieldValue.NewU64(1);

        // A packet at sequence 3 counts the field once. The sequence then wraps, and three packets later it would be back at 3.
        collector.RestartSequenceAt(2);
        _FeedPacket(collector, fieldId, in value);
        collector.RestartSequenceAt(-1);
        _FeedPacket(collector, null, in value);
        _FeedPacket(collector, null, in value);
        _FeedPacket(collector, null, in value);
        _FeedPacket(collector, fieldId, in value);

        await Assert.That(stage.Entries.Length).IsEqualTo(1);
        await Assert.That(stage.Entries[0].Occurrence).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TheCollectorWantsCustomTextsExactlyWhenTheContentWritesThem(bool customText)
    {
        using RowFieldStage stage = new();
        ExportFieldCollector collector = new(ExportContents.AllFields(customText: customText), stage);

        await Assert.That(collector.WantsCustomText).IsEqualTo(customText);
    }

    /// <summary>Runs one packet through the collector; the field is fed once when an id is given, otherwise the packet is empty.</summary>
    private static void _FeedPacket(ExportFieldCollector collector, int? fieldId, in FieldValue value)
    {
        collector.BeginPacket(new PacketId(0), Timestamp.FromNanos(0));
        if (fieldId is { } id)
        {
            collector.Accept(id, in value, default);
        }

        collector.EndPacket(new PacketId(0), complete: true);
    }

    #endregion
}
