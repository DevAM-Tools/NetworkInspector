// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Writing;

/// <summary>
/// The batch writer pipeline: overlap of writing and filling, the synchronous mode, the error timing of a failed batch, the
/// finish sequence, and the batch-size limits. The writer of the tests blocks on gates instead of sleeping, so every
/// ordering is deterministic. Every wait has a generous timeout so a broken pipeline fails the test instead of hanging it.
/// </summary>
internal sealed class BatchWriterPipelineTests
{
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(20);

    #region Overlap

    [Test]
    public async Task WriterWritesBatchOneWhileTheCallerFillsBatchTwo()
    {
        GatedWriter writer = new() { GateFirstWrite = true };
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer);
        pipeline.Current.Add(1);
        pipeline.Submit(firstItemIndex: 0);

        // The writer is inside Write(batch 1), blocked on its gate. The caller fills batch 2 meanwhile.
        writer.WaitUntilWriting();
        pipeline.Current.Add(2);
        long sequence = 0;
        Task<long> submitTwo = Task.Run(() =>
        {
            pipeline.Submit(firstItemIndex: 1);
            return Interlocked.Increment(ref sequence);
        });

        // Batch 1 has not been returned yet, so the second hand-off must wait for it. The proof is an order, not a timeout: the
        // gate is opened (sequence number taken) before the second submit can return (its own sequence number is taken after).
        long gateOpenedAt = Interlocked.Increment(ref sequence);
        writer.OpenGate();
        long submitReturnedAt = await submitTwo.WaitAsync(_Timeout);
        pipeline.Complete(firstItemIndex: 2);

        await Assert.That(submitReturnedAt).IsGreaterThan(gateOpenedAt);
        await Assert.That(writer.Written).IsEquivalentTo(new List<int> { 1, 2 });

        // Two batches were ever created: the one in flight and the one filling.
        await Assert.That(writer.DistinctBatches).IsEqualTo(2);
    }

    #endregion

    #region Synchronous mode

    [Test]
    public async Task SynchronousModeWritesOnTheCallerThreadWithTheSameOutput()
    {
        List<int> background = _Run(ExportWriteMode.Background, out _);
        List<int> synchronous = _Run(ExportWriteMode.Synchronous, out GatedWriter syncWriter);

        await Assert.That(synchronous).IsEquivalentTo(background);
        await Assert.That(syncWriter.ThreadIds.Distinct().Single()).IsEqualTo(Environment.CurrentManagedThreadId);
        await Assert.That(syncWriter.ThreadNames.Any(static n => n is not null && n.Contains("export writer", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task BackgroundModeWritesOnANamedWriterThread()
    {
        _ = _Run(ExportWriteMode.Background, out GatedWriter writer);

        // The last batch of a long export may be written inline by Complete; the earlier ones ran on the named thread.
        await Assert.That(writer.ThreadNames.Any(static n => n == "Test export writer")).IsTrue();
        await Assert.That(writer.ThreadIds.Any(static id => id != Environment.CurrentManagedThreadId)).IsTrue();
    }

    private static List<int> _Run(ExportWriteMode mode, out GatedWriter writer)
    {
        writer = new();
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", mode, static () => new TestBatch(), writer);
        int next = 0;
        for (int batch = 0; batch < 4; batch++)
        {
            int first = next;
            for (int i = 0; i < 3; i++)
            {
                pipeline.Current.Add(next++);
            }

            pipeline.Submit(firstItemIndex: first);
        }

        pipeline.Current.Add(next++);
        pipeline.Complete(firstItemIndex: next - 1);
        return [.. writer.Written];
    }

    #endregion

    #region Errors

    [Test]
    public async Task ABatchThatFailsInBackgroundModeIsReportedOnceAtTheLatestWhenTheExportCompletes()
    {
        GatedWriter writer = new() { FailBatchNumber = 2 };
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer);

        int next = 0;
        for (int batch = 0; batch < 4; batch++)
        {
            int first = next;
            pipeline.Current.Add(next++);
            pipeline.Current.Add(next++);
            pipeline.Submit(firstItemIndex: first);
        }

        pipeline.Complete(firstItemIndex: next);
        List<LostBatch> lost = _TakeAll(pipeline);

        // One report, with the packets and the first item index of batch 2; the other three batches were written.
        await Assert.That(lost.Count).IsEqualTo(1);
        await Assert.That(lost[0].PacketCount).IsEqualTo(2);
        await Assert.That(lost[0].FirstItemIndex).IsEqualTo(2);
        await Assert.That(lost[0].Message).Contains("Test batch write failed, 2 packets lost");
        await Assert.That(writer.Written).IsEquivalentTo(new List<int> { 0, 1, 4, 5, 6, 7 });
    }

    [Test]
    public async Task ABatchThatFailsInSynchronousModeIsReportedRightAfterItsSubmit()
    {
        GatedWriter writer = new() { FailBatchNumber = 2 };
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Synchronous, static () => new TestBatch(), writer);

        pipeline.Current.Add(0);
        pipeline.Submit(firstItemIndex: 0);
        bool afterFirst = pipeline.HasLost;
        pipeline.Current.Add(1);
        pipeline.Submit(firstItemIndex: 1);
        bool afterSecond = pipeline.HasLost;

        await Assert.That(afterFirst).IsFalse();
        await Assert.That(afterSecond).IsTrue();
        await Assert.That(pipeline.TryTakeLost(out LostBatch lost)).IsTrue();
        await Assert.That(lost.FirstItemIndex).IsEqualTo(1);
        await Assert.That(pipeline.TryTakeLost(out _)).IsFalse();
    }

    [Test]
    public async Task ABackgroundFailureIsVisibleAtTheSubmitAfterTheFailedBatch()
    {
        GatedWriter writer = new() { FailBatchNumber = 1 };
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer);
        pipeline.Current.Add(0);
        pipeline.Submit(firstItemIndex: 0);
        pipeline.Current.Add(1);

        // The second submit returns only after the writer gave batch 1 back, and the writer reports a failure before it gives
        // the batch back: the failure is therefore visible when this call returns, one batch after the failed one.
        pipeline.Submit(firstItemIndex: 1);
        bool visible = pipeline.HasLost;
        bool taken = pipeline.TryTakeLost(out LostBatch lost);

        await Assert.That(visible).IsTrue();
        await Assert.That(taken).IsTrue();
        await Assert.That(lost.FirstItemIndex).IsEqualTo(0);
    }

    [Test]
    public async Task AFailedLastBatchIsReportedAfterCompleteAndTheBatchIsEmptied()
    {
        GatedWriter writer = new() { FailBatchNumber = 2 };
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer);
        pipeline.Current.Add(0);
        pipeline.Submit(firstItemIndex: 0);
        pipeline.Current.Add(1);
        pipeline.Current.Add(2);

        pipeline.Complete(firstItemIndex: 1);
        List<LostBatch> lost = _TakeAll(pipeline);

        await Assert.That(lost.Count).IsEqualTo(1);
        await Assert.That(lost[0].PacketCount).IsEqualTo(2);
        await Assert.That(pipeline.IsCompleted).IsTrue();
        await Assert.That(pipeline.CurrentPacketCount).IsEqualTo(0);
    }

    private static List<LostBatch> _TakeAll(BatchWriterPipeline<TestBatch> pipeline)
    {
        List<LostBatch> lost = [];
        while (pipeline.TryTakeLost(out LostBatch item))
        {
            lost.Add(item);
        }

        return lost;
    }

    #endregion

    #region Finish

    [Test]
    public async Task CompleteWritesTheLastBatchJoinsTheThreadAndIsIdempotent()
    {
        GatedWriter writer = new();
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer);
        pipeline.Current.Add(1);
        pipeline.Submit(firstItemIndex: 0);
        pipeline.Current.Add(2);

        pipeline.Complete(firstItemIndex: 1);
        int writesAfterFirstComplete = writer.Written.Count;
        pipeline.Complete(firstItemIndex: 1);
        pipeline.Dispose();
        pipeline.Dispose();

        await Assert.That(writer.Written).IsEquivalentTo(new List<int> { 1, 2 });
        await Assert.That(writer.Written.Count).IsEqualTo(writesAfterFirstComplete);
        await Assert.That(() => _ = pipeline.Current).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AnExportShorterThanOneBatchWritesInlineAndStartsNoThread()
    {
        GatedWriter writer = new();
        using BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer);
        pipeline.Current.Add(7);

        pipeline.Complete(firstItemIndex: 0);

        await Assert.That(writer.Written).IsEquivalentTo(new List<int> { 7 });
        await Assert.That(writer.ThreadIds.Single()).IsEqualTo(Environment.CurrentManagedThreadId);
    }

    [Test]
    public async Task DisposeWithoutCompleteDoesNotWriteTheBatchThatIsStillFilling()
    {
        GatedWriter writer = new();
        TestBatch? filling;
        using (BatchWriterPipeline<TestBatch> pipeline = new("Test", ExportWriteMode.Background, static () => new TestBatch(), writer))
        {
            pipeline.Current.Add(1);
            pipeline.Submit(firstItemIndex: 0);
            pipeline.Current.Add(2);
            filling = pipeline.Current;
        }

        // The batch handed over before the dispose is still written (the thread ends after its queue is empty); the one that
        // was still filling is an aborted export and is dropped.
        await Assert.That(writer.Written).IsEquivalentTo(new List<int> { 1 });
        await Assert.That(filling.Disposed).IsTrue();
    }

    [Test]
    public async Task ThePipelineCreatesNothingBeforeTheFirstUse()
    {
        int created = 0;
        GatedWriter writer = new();
        using BatchWriterPipeline<TestBatch> pipeline = new(
            "Test", ExportWriteMode.Background, () => { created++; return new TestBatch(); }, writer);

        await Assert.That(created).IsEqualTo(0);
        await Assert.That(pipeline.CurrentPacketCount).IsEqualTo(0);
        await Assert.That(created).IsEqualTo(0);
    }

    #endregion

    #region Guards and limits

    [Test]
    public async Task ConstructorRefusesBadArguments()
    {
        GatedWriter writer = new();

        await Assert.That(() => new BatchWriterPipeline<TestBatch>("", ExportWriteMode.Background, static () => new TestBatch(), writer))
            .Throws<ArgumentException>();
        await Assert.That(() => new BatchWriterPipeline<TestBatch>("Test", ExportWriteMode.Background, null!, writer))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new BatchWriterPipeline<TestBatch>("Test", ExportWriteMode.Background, static () => new TestBatch(), null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new BatchWriterPipeline<TestBatch>("Test", (ExportWriteMode)7, static () => new TestBatch(), writer))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(int.MaxValue, true)]
    [Arguments(0, false)]
    [Arguments(-1, false)]
    public async Task PacketsPerBatchAcceptsTheRangeAndNamesItWhenRefused(int count, bool accepted)
    {
        if (accepted)
        {
            await Assert.That(BatchLimits.ValidatePacketsPerBatch(count, "count")).IsEqualTo(count);
            return;
        }

        await Assert.That(() => BatchLimits.ValidatePacketsPerBatch(count, "count"))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("Packets per batch must be between 1 and 2147483647.");
    }

    [Test]
    [Arguments(1L, true)]
    [Arguments(long.MaxValue, true)]
    [Arguments(0L, false)]
    public async Task BytesPerBatchAcceptsTheRangeAndNamesItWhenRefused(long bytes, bool accepted)
    {
        if (accepted)
        {
            await Assert.That(BatchLimits.ValidateBytesPerBatch(bytes, "bytes")).IsEqualTo(bytes);
            return;
        }

        await Assert.That(() => BatchLimits.ValidateBytesPerBatch(bytes, "bytes"))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("Estimated bytes per batch must be between 1 and");
    }

    [Test]
    public async Task DefaultsAreTheSecureDocumentedValues()
    {
        int packets = BatchLimits.DefaultPacketsPerBatch;
        long bytes = BatchLimits.DefaultBytesPerBatch;

        await Assert.That(packets).IsEqualTo(50_000);
        await Assert.That(bytes).IsEqualTo(16L * 1024 * 1024);
    }

    #endregion

    #region Test doubles

    /// <summary>A batch of integers; counts its disposal so a test can see that the pipeline released it.</summary>
    private sealed class TestBatch : IExportBatch
    {
        private readonly List<int> _Items = [];

        public int PacketCount => _Items.Count;

        public long EstimatedSizeBytes => _Items.Count * 8L;

        public IReadOnlyList<int> Items => _Items;

        public bool Disposed { get; private set; }

        public void Add(int value) => _Items.Add(value);

        public void Reset() => _Items.Clear();

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Records what it writes and on which thread. Optionally blocks inside the first write until the test opens the gate, and
    /// fails the batch with the given number (1-based).
    /// </summary>
    private sealed class GatedWriter : IBatchWriter<TestBatch>, IDisposable
    {
        private readonly ManualResetEventSlim _Writing = new(false);
        private readonly ManualResetEventSlim _Gate = new(false);
        private readonly List<TestBatch> _Seen = [];
        private int _WriteNumber;

        public bool GateFirstWrite { get; init; }

        public int FailBatchNumber { get; init; }

        public List<int> Written { get; } = [];

        public List<int> ThreadIds { get; } = [];

        public List<string?> ThreadNames { get; } = [];

        public int DistinctBatches => _Seen.Count;

        public void WaitUntilWriting()
        {
            if (!_Writing.Wait(_Timeout))
            {
                throw new TimeoutException("The writer never started writing.");
            }
        }

        public void OpenGate() => _Gate.Set();

        public void Dispose()
        {
            _Writing.Dispose();
            _Gate.Dispose();
        }

        public void Write(TestBatch batch)
        {
            int number = Interlocked.Increment(ref _WriteNumber);
            if (!_Seen.Contains(batch))
            {
                _Seen.Add(batch);
            }

            ThreadIds.Add(Environment.CurrentManagedThreadId);
            ThreadNames.Add(Thread.CurrentThread.Name);
            if (GateFirstWrite && number == 1)
            {
                _Writing.Set();
                if (!_Gate.Wait(_Timeout))
                {
                    throw new TimeoutException("The gate was never opened.");
                }
            }

            if (number == FailBatchNumber)
            {
                throw new IOException("test write failure");
            }

            Written.AddRange(batch.Items);
        }
    }

    #endregion
}
