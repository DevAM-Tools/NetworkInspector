// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Shared;

/// <summary>
/// The shared exporter contract for the seven kinds that run in the fast set: they finish in well under 100 ms, so the class
/// carries no <c>Slow</c> category. The tests are those of <see cref="ExporterContractTests"/>.
/// </summary>
/// <seealso cref="SlowExporterContractTests"/>
[InheritsTests]
internal sealed class FastExporterContractTests : ExporterContractTests
{
    /// <summary>The kinds without a writer thread or a native library.</summary>
    /// <returns>ASC, BLF, PCAPNG, PBF, JSON, text, and CSV.</returns>
    public override IEnumerable<ExporterKind> Kinds() =>
    [
        ExporterKind.Asc,
        ExporterKind.Blf,
        ExporterKind.Pcapng,
        ExporterKind.Pbf,
        ExporterKind.Json,
        ExporterKind.Text,
        ExporterKind.Csv,
    ];
}
