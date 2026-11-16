// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Shared;

/// <summary>
/// The behaviors every exporter shares, proved once for all nine kinds: cancellation, the target count, the end of the life
/// cycle, the UI name and the description, and the refusal to build without an output. A new exporter adds a row to
/// <see cref="ExporterKind"/>; a missing row is a gap this class makes visible.
/// <para>
/// The tests live here once and run in two derived classes, so the speed marker follows the kind: <see cref="FastExporterContractTests"/>
/// runs the kinds that finish in well under 100 ms in the fast set, <see cref="SlowExporterContractTests"/> carries the
/// <c>Slow</c> category for the kinds that need a writer thread or a native library. Every kind belongs to exactly one of the two.
/// </para>
/// </summary>
/// <seealso cref="FastExporterContractTests"/>
/// <seealso cref="SlowExporterContractTests"/>
internal abstract class ExporterContractTests
{
    #region Data

    /// <summary>The kinds this derived class runs, one row per test run.</summary>
    /// <returns>A subset of <see cref="ExporterKind"/>; the two derived classes together cover every kind.</returns>
    public abstract IEnumerable<ExporterKind> Kinds();

    /// <summary>The UI name each kind reports when none was set.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The default UI name.</returns>
    private static string _DefaultUiName(ExporterKind kind) => kind switch
    {
        ExporterKind.Asc => "ASC Exporter",
        ExporterKind.Blf => "BLF Exporter",
        ExporterKind.Pcapng => "PCAPNG Exporter",
        ExporterKind.Pbf => "PBF Exporter",
        ExporterKind.Json => "JSON Exporter",
        ExporterKind.Text => "Text Exporter",
        ExporterKind.Csv => "CSV Exporter",
        ExporterKind.Parquet => "Parquet Exporter",
        ExporterKind.DuckDb => "DuckDB Exporter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown exporter kind."),
    };

    #endregion

    #region Cancellation and target count

    [Test]
    [InstanceMethodDataSource(nameof(Kinds))]
    public async Task CancellationBeforeTheSecondItemStopsTheExport(ExporterKind kind)
    {
        using CancellationTokenSource cts = new();
        using ExporterHarness harness = ExporterHarness.Create(kind, cancellationToken: cts.Token);

        bool first = harness.Offer();
        await Assert.That(harness.IsFinished).IsFalse();
        await cts.CancelAsync().ConfigureAwait(false);

        // The exporter reports itself finished as soon as the token is cancelled, before the next item or OnFinish.
        bool finishedAfterCancel = harness.IsFinished;
        bool second = harness.Offer();
        harness.Finish();

        await Assert.That(first).IsTrue();
        await Assert.That(finishedAfterCancel).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(harness.IsFinished).IsTrue();
        await Assert.That(harness.WrittenCount).IsEqualTo(1);
    }

    [Test]
    [InstanceMethodDataSource(nameof(Kinds))]
    public async Task ATargetCountOfTwoStopsAfterTwoItems(ExporterKind kind)
    {
        using ExporterHarness harness = ExporterHarness.Create(kind, targetCount: 2);

        int accepted = 0;
        for (int offered = 0; offered < 5; offered++)
        {
            if (harness.Offer())
            {
                accepted++;
            }
        }

        // The target is reached without OnFinish: the exporter already reports itself finished.
        bool finishedAfterTarget = harness.IsFinished;
        harness.Finish();

        await Assert.That(accepted).IsEqualTo(2);
        await Assert.That(finishedAfterTarget).IsTrue();
        await Assert.That(harness.WrittenCount).IsEqualTo(2);
    }

    #endregion

    #region Life cycle

    [Test]
    [InstanceMethodDataSource(nameof(Kinds))]
    public async Task ACallAfterFinishIsRefusedAndASecondFinishIsANoOp(ExporterKind kind)
    {
        using ExporterHarness harness = ExporterHarness.Create(kind);
        await Assert.That(harness.IsFinished).IsFalse();

        bool first = harness.Offer();
        harness.Finish();
        bool afterFinish = harness.Offer();
        harness.Finish();

        await Assert.That(first).IsTrue();
        await Assert.That(afterFinish).IsFalse();
        await Assert.That(harness.IsFinished).IsTrue();
        await Assert.That(harness.WrittenCount).IsEqualTo(1);
    }

    #endregion

    #region Names

    [Test]
    [InstanceMethodDataSource(nameof(Kinds))]
    public async Task UiNameAndDescriptionDefaultAndOverride(ExporterKind kind)
    {
        using ExporterHarness defaults = ExporterHarness.Create(kind);
        using ExporterHarness custom = ExporterHarness.Create(kind, uiName: "My export", description: "run 7");

        await Assert.That(defaults.UiName).IsEqualTo(_DefaultUiName(kind));
        await Assert.That(defaults.Description).IsNull();
        await Assert.That(custom.UiName).IsEqualTo("My export");
        await Assert.That(custom.Description).IsEqualTo("run 7");
    }

    #endregion

    #region Builder

    [Test]
    [InstanceMethodDataSource(nameof(Kinds))]
    public async Task BuildWithoutAnOutputThrows(ExporterKind kind)
    {
        Action build = ExporterHarness.BuildWithoutOutput(kind);

        await Assert.That(build).Throws<InvalidOperationException>();
    }

    #endregion
}
