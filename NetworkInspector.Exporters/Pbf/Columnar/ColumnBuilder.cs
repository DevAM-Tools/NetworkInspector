// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Per-field column accumulator for the columnar block format.
/// Stores all values for a single field across all packets in the block.
/// </summary>
internal sealed class ColumnBuilder
{
    private readonly List<string?> _Values = new(256);
    private readonly List<string?> _CustomRepresentations = new(256);
    private readonly List<string?> _CustomTexts = new(256);
    private readonly DictionaryEncoder _ValueDict = new();
    private readonly DictionaryEncoder _CustomRepresentationDict = new();
    private readonly DictionaryEncoder _CustomTextDict = new();

    /// <summary>Field ID this column tracks.</summary>
    internal int FieldIdValue
    {
        get;
    }

    /// <summary>Creates a column builder for the specified field.</summary>
    internal ColumnBuilder(int fieldIdValue)
    {
        FieldIdValue = fieldIdValue;
    }

    /// <summary>Number of rows (values) accumulated.</summary>
    internal int RowCount => _Values.Count;

    /// <summary>Adds a row with the given value, custom representation, and custom text.</summary>
    internal void AddRow(string? value, string? customRepresentation, string? customText)
    {
        _Values.Add(value);
        _CustomRepresentations.Add(customRepresentation);
        _CustomTexts.Add(customText);

        // Try dictionary encoding
        if (value is not null)
        {
            _ValueDict.TryAdd(value);
        }
        if (customRepresentation is not null)
        {
            _CustomRepresentationDict.TryAdd(customRepresentation);
        }
        if (customText is not null)
        {
            _CustomTextDict.TryAdd(customText);
        }
    }

    /// <summary>Gets all values.</summary>
    internal IReadOnlyList<string?> Values => _Values;

    /// <summary>Gets all custom representations.</summary>
    internal IReadOnlyList<string?> CustomRepresentations => _CustomRepresentations;

    /// <summary>Gets all custom texts.</summary>
    internal IReadOnlyList<string?> CustomTexts => _CustomTexts;

    /// <summary>Gets the value dictionary encoder.</summary>
    internal DictionaryEncoder ValueDictionary => _ValueDict;

    /// <summary>Gets the custom representation dictionary encoder.</summary>
    internal DictionaryEncoder CustomRepresentationDictionary => _CustomRepresentationDict;

    /// <summary>Gets the custom text dictionary encoder.</summary>
    internal DictionaryEncoder CustomTextDictionary => _CustomTextDict;

    /// <summary>Resets the column for reuse.</summary>
    internal void Reset()
    {
        _Values.Clear();
        _CustomRepresentations.Clear();
        _CustomTexts.Clear();
        _ValueDict.Reset();
        _CustomRepresentationDict.Reset();
        _CustomTextDict.Reset();
    }
}
