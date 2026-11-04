// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// The staging path of <see cref="ColumnarPacketBatch"/> (the flat shapes' way in): fields are staged while a packet is parsed,
/// and only a closed staging of the committed packet becomes part of the batch. Everything else — a rejected packet, a muted
/// observer, a mismatched id, a value that does not fit — leaves the batch exactly as it was.
/// </summary>
internal sealed class ColumnarStagingTests
{
    #region Fixtures and builders

    /// <summary>The unsigned value (53) that most tests stage as a field value.</summary>
    private static readonly FieldValueData _Number = FieldValue.NewU64(53).Data;

    /// <summary>The id of a field in the test thread's parser stack.</summary>
    /// <param name="name">The field name, for example <c>udp.dstport</c>.</param>
    /// <returns>The field id value.</returns>
    private static int _FieldId(string name) => TestHarness.GetStack().GetFieldId(name)!.Value.Value;

    /// <summary>A parsed Ethernet, IPv4, UDP packet with the given id.</summary>
    /// <param name="id">The packet id.</param>
    /// <returns>The packet.</returns>
    private static Packet _Packet(int id = 0) =>
        PacketGenerators.CreateParsedPacket(id, FrameGenerators.BuildEthernetIpv4UdpFrame(4));

    /// <summary>A batch with no practical packet or size limit and sorted timestamps.</summary>
    /// <param name="content">The content the batch collects.</param>
    /// <returns>The batch; the caller disposes it.</returns>
    private static ColumnarPacketBatch _Batch(ExportContent content) =>
        new(content, maxPacketsPerBlock: 100, maxBlockSize: long.MaxValue, isTimestampSorted: true);

    /// <summary>A selected-fields content that selects every occurrence of the named fields.</summary>
    /// <param name="names">The field names to select.</param>
    /// <returns>The content.</returns>
    private static ExportContent _Selected(params string[] names) =>
        ExportContents.Selected(names.Select(static name => new FieldRequest(name)).ToArray());

    #endregion

    #region Commit

    /// <summary>Proves that a closed staging is committed as one packet with occurrence positions, info, and a catalog row completed from the stack.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AClosedStagingIsCommittedAsOnePacketWithOccurrencePositionsAndACompletedCatalog()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields(info: true, customRepresentation: true, customText: true));
        Packet packet = _Packet();
        int dstPort = _FieldId("udp.dstport");

        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(dstPort, 0, in _Number, new LazyString("rep"), new LazyString("text"));
        batch.AddValue(dstPort, 1, in _Number, default, default);
        batch.EndPacket(complete: true);
        bool committed = batch.TryCommit(packet, out _, out string? message);

        await Assert.That(committed).IsTrue();
        await Assert.That(message).IsNull();
        await Assert.That(batch.PacketCount).IsEqualTo(1);
        await Assert.That(batch.PacketIds[0]).IsEqualTo(packet.Id.Value);
        await Assert.That(batch.Infos.Count).IsEqualTo(1);
        await Assert.That(batch.StagedPacketId).IsEqualTo(-1);

        // Positions are the occurrence numbers; the catalog row was completed with the packet's stack.
        FieldColumnBag bag = batch.FieldBags[dstPort];
        await Assert.That(bag.Positions.Count).IsEqualTo(2);
        await Assert.That(bag.Positions[0]).IsEqualTo(0);
        await Assert.That(bag.Positions[1]).IsEqualTo(1);
        await Assert.That(bag.CustomRepresentations[0]).IsEqualTo("rep");
        await Assert.That(bag.CustomTexts[0]).IsEqualTo("text");
        await Assert.That(batch.Catalog[dstPort].Name).IsEqualTo("udp.dstport");
        await Assert.That(batch.Catalog[dstPort].TableName).IsEqualTo(Invariant($"field_{dstPort}"));
        await Assert.That(batch.Topology.Count).IsEqualTo(0);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(BatchSizes.SumOfBags(batch));
    }

    /// <summary>Proves that a deferred text completed after it was staged but before the end of the packet is evaluated at the end.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ADeferredTextCompletedBeforeTheEndOfThePacketIsEvaluatedAtTheEnd()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields(customText: true));
        Packet packet = _Packet();
        int dstPort = _FieldId("udp.dstport");
        int count = 0;
        LazyString deferred = LazyString.FormatLazy(
            new Func<int>(() => count), static read => string.Create(CultureInfo.InvariantCulture, $"items={read()}"));

        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(dstPort, 0, in _Number, default, deferred);
        count = 3; // the dissector completes the text after the append, before the end of the parse
        batch.EndPacket(complete: true);
        await Assert.That(batch.TryCommit(packet, out _, out _)).IsTrue();

        await Assert.That(batch.FieldBags[dstPort].CustomTexts[0]).IsEqualTo("items=3");
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(BatchSizes.SumOfBags(batch));
    }

    /// <summary>Proves that replacing the last staged value changes its row in place and keeps the size totals equal to the sum over the bags.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task LastReplacesTheStagedRowInPlaceAndKeepsTheSizeTotalsExact()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet packet = _Packet();
        int dstPort = _FieldId("udp.dstport");
        FieldValueData second = FieldValue.NewU64(80).Data;

        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(dstPort, 0, in _Number, default, default);
        batch.ReplaceLastValue(dstPort, in second, default, default);
        batch.EndPacket(complete: true);
        await Assert.That(batch.TryCommit(packet, out _, out _)).IsTrue();

        await Assert.That(batch.FieldBags[dstPort].RowCount).IsEqualTo(1);
        await Assert.That(batch.FieldBags[dstPort].U64Values[0]).IsEqualTo(80UL);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(BatchSizes.SumOfBags(batch));
    }

    /// <summary>Proves that replacing a value for which the current packet has no staged row adds a row and keeps the row of the committed packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ReplacingWithoutARowOfThisPacketAddsARowAndKeepsTheCommittedOne()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        int dstPort = _FieldId("udp.dstport");
        Packet first = _Packet(0);
        batch.BeginPacket(first.Id.Value, 0);
        batch.AddValue(dstPort, 0, in _Number, default, default);
        batch.EndPacket(complete: true);
        await Assert.That(batch.TryCommit(first, out _, out _)).IsTrue();

        Packet second = _Packet(1);
        FieldValueData other = FieldValue.NewU64(80).Data;
        batch.BeginPacket(second.Id.Value, 0);
        batch.ReplaceLastValue(dstPort, in other, default, default);
        batch.EndPacket(complete: true);
        await Assert.That(batch.TryCommit(second, out _, out _)).IsTrue();

        await Assert.That(batch.FieldBags[dstPort].RowCount).IsEqualTo(2);
        await Assert.That(batch.FieldBags[dstPort].U64Values[0]).IsEqualTo(53UL);
        await Assert.That(batch.FieldBags[dstPort].U64Values[1]).IsEqualTo(80UL);
    }

    /// <summary>Proves that discarding a staging after a replacement restores the size totals to zero and removes the bag.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ReplaceThenDiscardRestoresTheSizeTotals()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        int field = _FieldId("udp.dstport");
        FieldValueData first = FieldValue.NewBytes(new byte[] { 1, 2, 3 }).Data;
        FieldValueData second = FieldValue.NewBytes(new byte[] { 4, 5, 6, 7, 8 }).Data;

        batch.BeginPacket(0, 0);
        batch.AddValue(field, 0, in first, default, default);
        batch.ReplaceLastValue(field, in second, default, default);
        batch.Discard();

        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(0L);
        await Assert.That(batch.FieldBags.ContainsKey(field)).IsFalse();
        await Assert.That(batch.StagedPacketId).IsEqualTo(-1);
    }

    #endregion

    #region Refusals leave the batch as it was

    /// <summary>Proves that an info text that throws makes the commit fail with a serialization error and leaves the batch empty.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AThrowingInfoTextRefusesTheCommitAndLeavesTheBatchAsItWas()
    {
        using CustomFieldPacketFactory factory = new();
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields(info: true));
        Packet packet = factory.Create(number: 7, text: null, faultyInfo: true);
        FieldValueData number = FieldValue.NewI64(7).Data;
        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(factory.NumberFieldId.Value, 0, in number, default, default);
        batch.EndPacket(complete: true);

        bool committed = batch.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("info boom");
        await Assert.That(batch.PacketCount).IsEqualTo(0);
        await Assert.That(batch.Timestamps.Count).IsEqualTo(0);
        await Assert.That(batch.Infos.Count).IsEqualTo(0);
        await Assert.That(batch.FieldBags.Count).IsEqualTo(0);
        await Assert.That(batch.StagedPacketId).IsEqualTo(-1);
    }

    /// <summary>Proves that a staging that was never committed is undone, rows and catalog row included, when the next packet begins.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AStagingThatIsNeverCommittedIsUndoneByTheNextBeginPacket()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet rejected = _Packet();
        Packet next = _Packet();
        int dstPort = _FieldId("udp.dstport");
        int srcPort = _FieldId("udp.srcport");

        batch.BeginPacket(rejected.Id.Value, 0);
        batch.AddValue(dstPort, 0, in _Number, default, default);
        batch.EndPacket(complete: true);

        // No commit for the rejected packet (the filter said no); the next BeginPacket undoes its rows.
        batch.BeginPacket(next.Id.Value, 0);
        batch.AddValue(srcPort, 0, in _Number, default, default);
        batch.EndPacket(complete: true);
        bool committed = batch.TryCommit(next, out _, out _);

        await Assert.That(committed).IsTrue();
        await Assert.That(batch.PacketCount).IsEqualTo(1);
        await Assert.That(batch.FieldBags.ContainsKey(dstPort)).IsFalse();
        await Assert.That(batch.Catalog.ContainsKey(dstPort)).IsFalse();
        await Assert.That(batch.FieldBags[srcPort].RowCount).IsEqualTo(1);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(BatchSizes.SumOfBags(batch));
    }

    /// <summary>Proves that a staging of a packet that was not received completely is refused with a message that names the cause and adds nothing.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AMutedStagingIsRefusedWithAMessageThatNamesTheCauseAndNothingIsAdded()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet packet = _Packet();

        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(_FieldId("udp.dstport"), 0, in _Number, default, default);
        batch.EndPacket(complete: false);
        bool committed = batch.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("not received completely");
        await Assert.That(batch.PacketCount).IsEqualTo(0);
        await Assert.That(batch.FieldBags.Count).IsEqualTo(0);
        await Assert.That(batch.StagedPacketId).IsEqualTo(-1);
    }

    /// <summary>
    /// Proves that a commit for a packet other than the staged one is refused, and the staging can be committed afterwards for
    /// its own packet.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ACommitForAnotherPacketIsRefusedAndTheStagingStaysForItsOwnPacket()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet staged = _Packet();
        Packet other = _Packet();

        batch.BeginPacket(staged.Id.Value, 0);
        batch.AddValue(_FieldId("udp.dstport"), 0, in _Number, default, default);
        batch.EndPacket(complete: true);
        bool refused = batch.TryCommit(other, out _, out string? message);
        bool committed = batch.TryCommit(staged, out _, out _);

        await Assert.That(refused).IsFalse();
        await Assert.That(message).Contains("while fields of packet " + staged.Id.Value.ToString(CultureInfo.InvariantCulture) + " are staged");
        await Assert.That(committed).IsTrue();
        await Assert.That(batch.PacketCount).IsEqualTo(1);
    }

    /// <summary>Proves that a second commit of the same packet and a commit with nothing staged are both refused with the same message.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ASecondCommitOfTheSamePacketAndAnEmptyStageAreRefused()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet packet = _Packet();

        bool none = batch.TryCommit(packet, out _, out string? noneMessage);
        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(_FieldId("udp.dstport"), 0, in _Number, default, default);
        batch.EndPacket(complete: true);
        bool first = batch.TryCommit(packet, out _, out _);
        bool second = batch.TryCommit(packet, out _, out string? secondMessage);

        await Assert.That(none).IsFalse();
        await Assert.That(noneMessage).Contains("neither a field tree nor staged fields");
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(secondMessage).Contains("neither a field tree nor staged fields");
        await Assert.That(batch.PacketCount).IsEqualTo(1);
    }

    /// <summary>Proves that a value whose type does not fit the column seeded for its field fails the packet and rolls it back.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AValueThatDoesNotFitItsSeededColumnFailsThePacketAndRollsItBack()
    {
        using ColumnarPacketBatch batch = _Batch(_Selected("udp.dstport"));
        Packet packet = _Packet();
        int dstPort = _FieldId("udp.dstport");
        FieldValueData text = FieldValueData.NewString("not a number");

        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(dstPort, 0, in text, default, default);
        batch.EndPacket(complete: true);
        bool committed = batch.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("expected U64");
        await Assert.That(batch.PacketCount).IsEqualTo(0);
        await Assert.That(batch.FieldBags[dstPort].RowCount).IsEqualTo(0);
        await Assert.That(batch.FieldBagsEstimatedSizeBytes).IsEqualTo(0L);
    }

    /// <summary>
    /// Stages a number and then a string for one field id (a dissector that reuses the id with another type), commits, and counts the
    /// exceptions that were thrown on this thread while it ran. Synchronous because the exceptions are counted per thread and the
    /// test method continues on other threads after an await.
    /// </summary>
    /// <param name="message">The refusal of the commit.</param>
    /// <param name="committed">Whether the commit succeeded.</param>
    /// <param name="rowCount">The rows of the field's bag after the failed commit.</param>
    /// <returns>The number of exceptions thrown on this thread (first-chance, caught or not).</returns>
    private static int _StageAMismatchAndCountExceptions(out string? message, out bool committed, out int rowCount)
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet packet = _Packet();
        int dstPort = _FieldId("udp.dstport");
        FieldValueData text = FieldValueData.NewString("not a number");
        int threadId = Environment.CurrentManagedThreadId;
        int thrown = 0;
        void CountOnThisThread(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == threadId)
            {
                thrown++;
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += CountOnThisThread;
        try
        {
            batch.BeginPacket(packet.Id.Value, 0);
            batch.AddValue(dstPort, 0, in _Number, default, default);
            batch.AddValue(dstPort, 1, in text, default, default);
            batch.EndPacket(complete: true);
            committed = batch.TryCommit(packet, out _, out message);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= CountOnThisThread;
        }

        rowCount = batch.FieldBags.TryGetValue(dstPort, out FieldColumnBag? bag)
            ? bag.RowCount
            : 0;
        return thrown;
    }

    /// <summary>
    /// A value of another type than its column fails the packet at commit with the message that names the field and both types, and
    /// no exception is thrown on the way: the type is compared before the bag is touched.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AValueOfAnotherTypeFailsTheStagingWithoutAnException()
    {
        int dstPort = _FieldId("udp.dstport");

        int thrown = _StageAMismatchAndCountExceptions(out string? message, out bool committed, out int rowCount);

        await Assert.That(thrown).IsEqualTo(0);
        await Assert.That(committed).IsFalse();
        await Assert.That(message).Contains(Invariant($"Field {dstPort}: expected U64 but value type was String."));
        await Assert.That(rowCount).IsEqualTo(0);
    }

    /// <summary>
    /// A commit that fails after the packet columns (and the additional buffers) were appended, here because the type of a column the
    /// commit creates does not match the declared type of its field, rolls the buffer rows back with the rest of the packet.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ACommitThatFailsAfterTheBuffersWereCopiedLeavesNoBufferRows()
    {
        using BufferProbePacketFactory factory = new();
        using ColumnarPacketBatch batch = _Batch(new ExportContent { Shape = ExportShape.AllFields, Info = false, Buffers = true });
        Packet packet = factory.CreatePacket([1, 2, 3], [4, 5, 6, 7]);
        int fieldId = factory.Stack.GetFieldId("bfp.count")!.Value.Value;
        FieldValueData text = FieldValueData.NewString("not a number");

        // The staged value is a string for a field that the stack declares as U64: the bag is created with the staged type and the
        // commit finds the mismatch only when it completes the catalog, after it copied the buffers.
        batch.BeginPacket(packet.Id.Value, 0);
        batch.AddValue(fieldId, 0, in text, default, default);
        batch.EndPacket(complete: true);
        bool committed = batch.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("declared type");
        await Assert.That(batch.PacketCount).IsEqualTo(0);
        await Assert.That(batch.BufferRowCount).IsEqualTo(0);
        await Assert.That(batch.BufferBytesList.Count).IsEqualTo(0);
        await Assert.That(batch.EstimatedSizeBytes).IsEqualTo(0L);
    }

    /// <summary>Proves that adding a whole packet while fields are staged throws <see cref="InvalidOperationException"/>.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AddPacketIsRefusedWhileFieldsAreStaged()
    {
        using ColumnarPacketBatch batch = _Batch(ExportContents.AllFields());
        Packet packet = _Packet();

        batch.BeginPacket(packet.Id.Value, 0);

        await Assert.That(() => batch.AddPacket(packet)).Throws<InvalidOperationException>();
    }

    #endregion

    #region Seeding and allocation

    /// <summary>Proves that a selected-fields batch has a catalog row and an empty bag for every selected field before any packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ASelectedFieldsBatchListsEverySelectedFieldFromTheStart()
    {
        using ColumnarPacketBatch batch = _Batch(_Selected("ip.src", "dns.qry.name"));

        await Assert.That(batch.Catalog.Count).IsEqualTo(2);
        await Assert.That(batch.FieldBags.Count).IsEqualTo(2);
        await Assert.That(batch.Catalog[_FieldId("ip.src")].FieldType).IsEqualTo(FieldType.IPv4Address);
        await Assert.That(batch.FieldBags[_FieldId("dns.qry.name")].RowCount).IsEqualTo(0);
    }

    /// <summary>Proves that, once warm, the batch and the collector allocate nothing for scalar fields beyond what the parse itself allocates.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AWarmBatchAndCollectorAddNoAllocationForScalarFieldsToTheParseItself()
    {
        Frame frame = TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(16));
        ExportContent content = new()
        {
            Shape = ExportShape.SelectedFields,
            Fields = ExportContents.Selected(new FieldRequest("udp.srcport"), new FieldRequest("udp.dstport")).Fields,
            Info = false,
            FrameBytes = false,
            CustomText = false,
            CustomRepresentation = false,
        };
        using ColumnarPacketBatch batch = new(content, maxPacketsPerBlock: int.MaxValue, maxBlockSize: long.MaxValue, isTimestampSorted: true);
        ExportFieldCollector collector = new(content, batch);
        ParseObserver[] observers = [collector.CreateDemand().Observer];
        ParseOptions withCollector = new(FieldTreeMode.Skip, observers: observers);
        ParseOptions without = new(FieldTreeMode.Skip);

        // The parse and the test harness allocate a little per round (the packet id, the recycled packet's bookkeeping); that
        // share is measured on the same frames without a collector, so that only what the collector and the batch add is judged.
        long baseline = _MeasureRounds(frame, in without, batch: null);
        long measured = _MeasureRounds(frame, in withCollector, batch);

        // A few KB of slack cover one-off lazy initialization and runtime noise (tiered JIT, event pipes); a real per-packet
        // allocation of even one small object would add tens of KB over the 1 000 measured rounds and fail.
        await Assert.That(measured).IsLessThanOrEqualTo(baseline + 8192);
    }

    /// <summary>One warm batch (2 000 packets, then <c>Reset</c>), then the bytes allocated by 1 000 parses and commits.</summary>
    /// <param name="frame">The frame that is parsed in every round.</param>
    /// <param name="options">The parse options; they decide whether a collector observes the parse.</param>
    /// <param name="batch">The batch that commits each packet, or <see langword="null"/> to measure the parse alone.</param>
    /// <returns>The bytes the measured rounds allocated on this thread.</returns>
    /// <exception cref="InvalidOperationException">The batch refused a committed packet.</exception>
    private static long _MeasureRounds(Frame frame, in ParseOptions options, ColumnarPacketBatch? batch)
    {
        Packet? recycle = null;
        for (int i = 0; i < 2000; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            if (batch is not null && !batch.TryCommit(recycle, out _, out _))
            {
                throw new InvalidOperationException("The batch refused the committed packet of the measured round.");
            }
        }

        // The lists keep their capacity across Reset: the measured rounds below add rows without regrowing them.
        batch?.Reset();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            recycle = TestHarness.ParseFrame(frame, in options, recycle);
            if (batch is not null && !batch.TryCommit(recycle, out _, out _))
            {
                throw new InvalidOperationException("The batch refused the committed packet of the measured round.");
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    #endregion
}
