// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
