// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Defines a single column in a CSV export.
/// </summary>
/// <param name="Kind">The type of column.</param>
/// <param name="Header">The header text for this column.</param>
public readonly record struct CsvColumnDefinition(
    CsvColumnKind Kind,
    string Header);
