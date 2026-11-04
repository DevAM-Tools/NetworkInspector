// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// The description of an export that every self-describing format writes next to its data (the DuckDB table
/// <c>export_info</c>, the Parquet file <c>export_info.parquet</c>, the PBF file header): which shape the data has, which fields
/// were selected, and which layout version a reader must expect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one place:</b> the shape decides what the position column of the field tables means (node id in the hierarchy shape,
/// occurrence number in the flat shapes) and whether a topology exists. A reader checks the shape first, so the three formats
/// must spell it identically; deriving the rows here keeps them from drifting apart.
/// </para>
/// <para><b>Thread safety:</b> stateless; safe to call from any thread.</para>
/// </remarks>
internal static class ExportInfo
{
    /// <summary>
    /// Layout version of the columnar outputs. A reader refuses a higher version; a later change of a column type or meaning raises
    /// this number (a constant of the format, not a setting).
    /// </summary>
    /// <remarks>
    /// Version 1 comprises the shape marker, the <c>occurrence</c> position column, the presence-encoded texts, and the integer address
    /// types.
    /// Sync: the DuckDB and Parquet READMEs (<c>layout_version</c>) and EXPORTER_GUIDE.md § Rebuilding the display line.
    /// </remarks>
    internal const int LayoutVersion = 1;

    /// <summary>The value of the <c>shape</c> row: <c>selected_fields</c>, <c>all_fields</c>, or <c>hierarchy</c>.</summary>
    /// <param name="shape">The export shape.</param>
    /// <returns>The stable lower-case name; it is part of the file format.</returns>
    internal static string ShapeName(ExportShape shape) =>
        shape switch
        {
            ExportShape.SelectedFields => "selected_fields",
            ExportShape.AllFields => "all_fields",
            _ => "hierarchy",
        };

    /// <summary>
    /// The numeric shape code of the PBF file header: 0 = hierarchy, 1 = selected fields, 2 = all fields. Hierarchy is 0 so that
    /// a header without the field (a file of 0.19.0) reads as hierarchy.
    /// </summary>
    /// <param name="shape">The export shape.</param>
    /// <returns>The code, always below 128 (one varint byte).</returns>
    internal static int ShapeCode(ExportShape shape) =>
        shape switch
        {
            ExportShape.SelectedFields => 1,
            ExportShape.AllFields => 2,
            _ => 0,
        };

    /// <summary>The occurrence choice as it is spelled in <c>export_info</c>: <c>all</c>, <c>first</c>, or <c>last</c>.</summary>
    /// <param name="occurrences">The occurrence choice.</param>
    /// <returns>The stable lower-case name.</returns>
    internal static string OccurrencesName(Occurrences occurrences) =>
        occurrences switch
        {
            Occurrences.First => "first",
            Occurrences.Last => "last",
            _ => "all",
        };

    /// <summary>Creates the key/value rows of the export description for <paramref name="content"/>.</summary>
    /// <param name="content">The content of the export.</param>
    /// <returns>
    /// The rows <c>shape</c>, <c>fields</c> (selected names joined with <c>;</c>, empty otherwise), <c>occurrences</c> (per field
    /// <c>name=all|first|last</c>, joined with <c>;</c>, empty otherwise), and <c>layout_version</c>.
    /// </returns>
    internal static IReadOnlyList<KeyValuePair<string, string>> CreateRows(ExportContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string fields = string.Empty;
        string occurrences = string.Empty;
        if (content.Fields is not null)
        {
            // Names are registered field names (letters, digits, dots, underscores), so ';' and '=' cannot occur inside them.
            List<string> names = new(content.Fields.Fields.Count);
            List<string> choices = new(content.Fields.Fields.Count);
            foreach (SelectedField field in content.Fields.Fields)
            {
                names.Add(field.Name);
                choices.Add(field.Name + "=" + OccurrencesName(field.Occurrences));
            }

            fields = string.Join(';', names);
            occurrences = string.Join(';', choices);
        }

        return
        [
            new("shape", ShapeName(content.Shape)),
            new("fields", fields),
            new("occurrences", occurrences),
            new("layout_version", LayoutVersion.ToString(CultureInfo.InvariantCulture)),
        ];
    }
}
