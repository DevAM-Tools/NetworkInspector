// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Measures the retained managed memory of an opened, fully scanned capture per work unit. One <see cref="Run"/> =
/// open, full scan, measure with the object alive, dispose. Throughput of these scenarios is not gated: it includes
/// forced full collections.
/// </summary>
/// <remarks>
/// <para>
/// The measurement is <c>GC.GetTotalMemory(true)</c> after the scan minus the same value before the open, both after
/// blocking compacting collections, so only live objects count. Memory-mapped files and unmanaged views are not part of
/// the managed heap; the scenarios open files without preloading so the value is the bookkeeping the source keeps
/// (index, tables) and not the file bytes.
/// </para>
/// <para><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</para>
/// </remarks>
internal abstract class FootprintScenarioBase : IProfilingScenario
{
    #region Fields

    private double _Retained = double.NaN;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public TimeSpan WarmupDuration => TimeSpan.FromSeconds(1);

    /// <inheritdoc/>
    public TimeSpan Duration => TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public abstract long WorkUnitsPerIteration { get; }

    /// <inheritdoc/>
    public abstract string WorkUnitName { get; }

    /// <inheritdoc/>
    public double RetainedBytesPerUnit => _Retained;

    /// <inheritdoc/>
    public abstract void Setup();

    /// <inheritdoc/>
    public void Run()
    {
        // Full blocking compacting collections so only live objects remain on both sides of the measurement.
        _CollectFully();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        // The opened object stays reachable until after the second measurement.
        IDisposable opened = OpenAndScan();
        _CollectFully();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(opened);
        opened.Dispose();

        _Retained = (after - before) / (double)WorkUnitsPerIteration;
    }

    /// <inheritdoc/>
    public abstract void Cleanup();

    #endregion

    #region Protected API

    /// <summary>Opens and fully scans the workload; returns the object whose footprint is measured.</summary>
    protected abstract IDisposable OpenAndScan();

    #endregion

    #region Private helpers

    private static void _CollectFully()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    #endregion
}
