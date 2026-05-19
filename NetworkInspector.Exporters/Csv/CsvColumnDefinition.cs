// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Csv;

/// <summary>
/// Defines a single column in a CSV export.
/// </summary>
/// <param name="Kind">The type of column.</param>
/// <param name="Header">The header text for this column.</param>
/// <param name="FieldName">The protocol field name (only for <see cref="CsvColumnKind.Field"/>).</param>
/// <param name="FieldId">The resolved field ID (only for <see cref="CsvColumnKind.Field"/>).</param>
public readonly record struct CsvColumnDefinition(
    CsvColumnKind Kind,
    string Header,
    string? FieldName,
    FieldId? FieldId);
