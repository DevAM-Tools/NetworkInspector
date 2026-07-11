// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Adaptive dictionary encoder for string and byte values.
/// Maps up to 256 unique values to single-byte indices for compact encoding.
/// Falls back to direct encoding when the dictionary overflows.
/// </summary>
internal sealed class DictionaryEncoder
{
    /// <summary>Maximum number of dictionary entries before overflow.</summary>
    private const int _MaxEntries = 256;

    private readonly Dictionary<string, byte> _Index = new(_MaxEntries);
    private readonly List<string> _Entries = new(_MaxEntries);
    private bool _Overflowed;

    /// <summary>
    /// Tries to add a value to the dictionary. Returns the index if within capacity,
    /// or -1 if the dictionary has overflowed (too many unique values).
    /// </summary>
    /// <param name="value">The string value to index.</param>
    /// <returns>Dictionary index (0–255), or -1 if overflowed.</returns>
    internal int TryAdd(string value)
    {
        if (_Overflowed)
        {
            return -1;
        }

        if (_Index.TryGetValue(value, out byte existing))
        {
            return existing;
        }

        if (_Entries.Count >= _MaxEntries)
        {
            _Overflowed = true;
            return -1;
        }

        byte index = (byte)_Entries.Count;
        _Index[value] = index;
        _Entries.Add(value);
        return index;
    }

    /// <summary>Gets the dictionary entries as a span.</summary>
    internal ReadOnlySpan<string> Entries => CollectionsMarshal.AsSpan(_Entries);

    /// <summary>Whether the dictionary is still active (not overflowed and has entries).</summary>
    internal bool IsActive => !_Overflowed && _Entries.Count > 0;

    /// <summary>Number of unique entries in the dictionary.</summary>
    internal int Count => _Entries.Count;

    /// <summary>Resets the dictionary for reuse.</summary>
    internal void Reset()
    {
        _Index.Clear();
        _Entries.Clear();
        _Overflowed = false;
    }
}
