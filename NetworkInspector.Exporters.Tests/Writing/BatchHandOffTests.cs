// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Writing;

/// <summary>
/// The hand-off between the filling thread and the writer thread, driven hard: many exchanges, the last batch published
/// without taking another one, then close. Every job must arrive exactly once and in order, the two batches must be the only ones
/// that ever exist, and the writer must end cleanly after the close.
/// </summary>
internal sealed class BatchHandOffTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(1000)]
    [Arguments(100_000)]
    [Category("Slow")]
    public async Task EveryJobArrivesOnceInOrderAndTheWriterEndsAfterClose(int exchanges)
    {
        using ProbeBatch first = new();
        using ProbeBatch second = new();
        using BatchHandOff<ProbeBatch> handOff = new(second);
        List<int> received = new(exchanges + 1);
        HashSet<ProbeBatch> seen = [];
        Exception? writerFailure = null;
        Thread writer = new(() =>
        {
            try
            {
                while (handOff.TryTakeFull(out WriteJob<ProbeBatch> job))
                {
                    received.Add(job.FirstItemIndex);
                    seen.Add(job.Batch);
                    handOff.ReturnEmpty(job.Batch);
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
        })
        {
            Name = "hand-off test writer",
            IsBackground = true,
        };
        writer.Start();

        // The caller exchanges like the pipeline does, then publishes the last batch without taking another one and closes.
        ProbeBatch current = first;
        for (int i = 0; i < exchanges; i++)
        {
            current = handOff.Exchange(new WriteJob<ProbeBatch>(current, 1, i, 8));
        }

        handOff.Publish(new WriteJob<ProbeBatch>(current, 1, exchanges, 8));
        handOff.Close();
        bool joined = writer.Join(TimeSpan.FromSeconds(30));

        await Assert.That(joined).IsTrue();
        await Assert.That(writerFailure).IsNull();
        await Assert.That(received.Count).IsEqualTo(exchanges + 1);
        await Assert.That(received.SequenceEqual(Enumerable.Range(0, exchanges + 1))).IsTrue();
        await Assert.That(seen.Count).IsEqualTo(2);
    }

    /// <summary>
    /// The same protocol under skewed speeds. A slow writer keeps the full slot busy (the caller must wait, not overwrite); a slow
    /// caller lets the writer return the next batch before the caller read the previous one, which is the interleaving that broke
    /// the single empty slot and led to the ring of two places. The skew is a spin, never a sleep.
    /// </summary>
    [Test]
    [Arguments(0, 0)]
    [Arguments(2000, 0)]
    [Arguments(0, 2000)]
    [Arguments(2000, 2000)]
    [Category("Slow")]
    public async Task EveryJobArrivesOnceInOrderUnderSkewedSpeeds(int writerSpin, int callerSpin)
    {
        const int exchanges = 4000;
        using ProbeBatch first = new();
        using ProbeBatch second = new();
        using BatchHandOff<ProbeBatch> handOff = new(second);
        List<int> received = new(exchanges + 1);
        HashSet<ProbeBatch> seen = [];
        Exception? writerFailure = null;
        Thread writer = new(() =>
        {
            try
            {
                while (handOff.TryTakeFull(out WriteJob<ProbeBatch> job))
                {
                    if (writerSpin > 0 && (job.FirstItemIndex & 7) == 0)
                    {
                        Thread.SpinWait(writerSpin);
                    }

                    received.Add(job.FirstItemIndex);
                    seen.Add(job.Batch);
                    handOff.ReturnEmpty(job.Batch);
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
        })
        {
            Name = "hand-off skew test writer",
            IsBackground = true,
        };
        writer.Start();

        ProbeBatch current = first;
        for (int i = 0; i < exchanges; i++)
        {
            if (callerSpin > 0 && (i & 7) == 0)
            {
                Thread.SpinWait(callerSpin);
            }

            current = handOff.Exchange(new WriteJob<ProbeBatch>(current, 1, i, 8));
        }

        handOff.Publish(new WriteJob<ProbeBatch>(current, 1, exchanges, 8));
        handOff.Close();
        bool joined = writer.Join(TimeSpan.FromSeconds(30));

        await Assert.That(joined).IsTrue();
        await Assert.That(writerFailure).IsNull();
        await Assert.That(received.SequenceEqual(Enumerable.Range(0, exchanges + 1))).IsTrue();
        await Assert.That(seen.Count).IsEqualTo(2);
    }

    /// <summary>A batch without content: only its identity matters.</summary>
    private sealed class ProbeBatch : IExportBatch
    {
        public int PacketCount => 1;

        public long EstimatedSizeBytes => 8;

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
