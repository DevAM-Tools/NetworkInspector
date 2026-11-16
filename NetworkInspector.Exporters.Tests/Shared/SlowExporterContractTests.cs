// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Shared;

/// <summary>
/// The shared exporter contract for the two kinds that take longer than 100 ms per test: Parquet (about 0.14 s, many entries
/// and a writer thread) and DuckDB (about 0.33 s, a native library and a file). The tests are those of
/// <see cref="ExporterContractTests"/>.
/// </summary>
/// <seealso cref="FastExporterContractTests"/>
[Category("Slow")]
[InheritsTests]
internal sealed class SlowExporterContractTests : ExporterContractTests
{
    /// <summary>The kinds that need a writer thread or a native library.</summary>
    /// <returns>Parquet and DuckDB.</returns>
    public override IEnumerable<ExporterKind> Kinds() =>
    [
        ExporterKind.Parquet,
        ExporterKind.DuckDb,
    ];
}
