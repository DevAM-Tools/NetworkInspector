// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Measures the raw hand-off between the filling thread and a writer thread: 1 000 000 exchanges of tiny batches through
/// <see cref="BatchHandOff{TBatch}"/>, with a writer that only resets the batch.
/// <para>
/// <b>Why:</b> the export scenarios hide the synchronization cost behind encoding and I/O. This scenario isolates it, so the
/// choice of the hand-off mechanism (a blocking collection against a single atomic slot with semaphores) rests on a number and
/// not on taste. The decision and both variants' numbers are recorded in <c>docs/decisions/batch-hand-off.md</c>.
/// </para>
/// <para><b>Thread safety:</b> the runner calls Setup, Run, Cleanup sequentially; Run starts and joins its own writer thread.</para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class WriterHandOffScenario : IProfilingScenario
{
    private const int _Exchanges = 1_000_000;

    /// <inheritdoc/>
    public string Name => "writer-handoff";

    /// <inheritdoc/>
    public string Description => "Batch hand-off between caller and writer thread (1 000 000 exchanges).";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _Exchanges;

    /// <inheritdoc/>
    public string WorkUnitName => "hand-offs";

    /// <inheritdoc/>
    public void Setup()
    {
    }

    /// <inheritdoc/>
    public void Run()
    {
        using HandOffProbeBatch first = new();
        using HandOffProbeBatch second = new();
        using BatchHandOff<HandOffProbeBatch> handOff = new(second);
        Thread writer = new(() =>
        {
            while (handOff.TryTakeFull(out WriteJob<HandOffProbeBatch> job))
            {
                job.Batch.Reset();
                handOff.ReturnEmpty(job.Batch);
            }
        })
        {
            Name = "writer-handoff probe",
            IsBackground = true,
        };
        writer.Start();

        HandOffProbeBatch current = first;
        for (int i = 0; i < _Exchanges; i++)
        {
            current = handOff.Exchange(new WriteJob<HandOffProbeBatch>(current, 1, i, 8));
        }

        handOff.Close();
        writer.Join();
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }

    /// <summary>A batch without content: the probe measures the hand-off only.</summary>
    private sealed class HandOffProbeBatch : IExportBatch
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
