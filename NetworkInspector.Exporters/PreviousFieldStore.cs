// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Stores previously seen field values for same-as-previous detection in the JSON Compact
/// writer. Value comparison uses typed <see cref="FieldValue"/> payloads; custom representation
/// / custom text use <see cref="LazyString"/> equality so deferred text is not materialized
/// solely for comparison.
/// Distinct from <see cref="PreviousFieldValueStore"/> used by the PBF Standard block builder.
/// </summary>
internal sealed class PreviousFieldStore
{
    private const int _DenseThreshold = 2048;

    private readonly FieldValue[]? _DenseValues;
    private readonly bool[]? _DenseHasValue;
    private readonly LazyString[]? _DenseCustomTexts;
    private readonly bool[]? _DenseHasCustomText;
    private readonly LazyString[]? _DenseValueCustomTexts;
    private readonly bool[]? _DenseHasValueCustomText;

    private readonly Dictionary<int, FieldValue>? _SparseValues;
    private readonly Dictionary<int, bool>? _SparseHasValue;
    private readonly Dictionary<int, LazyString>? _SparseCustomTexts;
    private readonly Dictionary<int, LazyString>? _SparseValueCustomTexts;

    private Dictionary<int, FieldValue>? _OverflowValues;
    private Dictionary<int, bool>? _OverflowHasValue;
    private Dictionary<int, LazyString>? _OverflowCustomTexts;
    private Dictionary<int, LazyString>? _OverflowValueCustomTexts;

    /// <summary>Creates a store optimized for the given field count.</summary>
    internal PreviousFieldStore(int fieldCount)
    {
        if (fieldCount <= _DenseThreshold)
        {
            _DenseValues = new FieldValue[fieldCount];
            _DenseHasValue = new bool[fieldCount];
            _DenseCustomTexts = new LazyString[fieldCount];
            _DenseHasCustomText = new bool[fieldCount];
            _DenseValueCustomTexts = new LazyString[fieldCount];
            _DenseHasValueCustomText = new bool[fieldCount];
        }
        else
        {
            _SparseValues = new(256);
            _SparseHasValue = new(256);
            _SparseCustomTexts = new(256);
            _SparseValueCustomTexts = new(256);
        }
    }

    /// <summary>
    /// Computes same-as-previous flags using typed value and <see cref="LazyString"/> custom text.
    /// </summary>
    internal uint CompareAndUpdate(
        int fieldIdValue,
        FieldValue currentValue,
        LazyString currentValueCustomText,
        LazyString currentCustomText)
    {
        if (_DenseValues is null)
        {
            return _CompareAndUpdateSparse(
                fieldIdValue, currentValue, currentValueCustomText, currentCustomText);
        }

        if (fieldIdValue < 0 || fieldIdValue >= _DenseValues.Length)
        {
            return _CompareAndUpdateOverflow(
                fieldIdValue, currentValue, currentValueCustomText, currentCustomText);
        }

        uint flags = 0;

        if (_DenseHasValue![fieldIdValue]
            && _DenseValues[fieldIdValue].Equals(currentValue))
        {
            flags |= SameFlags.FieldSameValue;
        }
        _DenseValues[fieldIdValue] = currentValue;
        _DenseHasValue[fieldIdValue] = true;

        if (_DenseHasValueCustomText![fieldIdValue]
            && _DenseValueCustomTexts![fieldIdValue].Equals(currentValueCustomText))
        {
            flags |= SameFlags.FieldSameCustomRepresentation;
        }
        _DenseValueCustomTexts![fieldIdValue] = currentValueCustomText;
        _DenseHasValueCustomText[fieldIdValue] = true;

        if (_DenseHasCustomText![fieldIdValue]
            && _DenseCustomTexts![fieldIdValue].Equals(currentCustomText))
        {
            flags |= SameFlags.FieldSameCustomText;
        }
        _DenseCustomTexts![fieldIdValue] = currentCustomText;
        _DenseHasCustomText[fieldIdValue] = true;

        return flags;
    }

    private uint _CompareAndUpdateOverflow(
        int fieldIdValue,
        FieldValue currentValue,
        LazyString currentValueCustomText,
        LazyString currentCustomText)
    {
        uint flags = 0;
        _OverflowValues ??= new Dictionary<int, FieldValue>(16);
        _OverflowHasValue ??= new Dictionary<int, bool>(16);
        _OverflowValueCustomTexts ??= new Dictionary<int, LazyString>(16);
        _OverflowCustomTexts ??= new Dictionary<int, LazyString>(16);

        if (_OverflowHasValue.TryGetValue(fieldIdValue, out bool had)
            && had
            && _OverflowValues.TryGetValue(fieldIdValue, out FieldValue prev)
            && prev.Equals(currentValue))
        {
            flags |= SameFlags.FieldSameValue;
        }
        _OverflowValues[fieldIdValue] = currentValue;
        _OverflowHasValue[fieldIdValue] = true;

        if (_OverflowValueCustomTexts.TryGetValue(fieldIdValue, out LazyString prevVct)
            && prevVct.Equals(currentValueCustomText))
        {
            flags |= SameFlags.FieldSameCustomRepresentation;
        }
        _OverflowValueCustomTexts[fieldIdValue] = currentValueCustomText;

        if (_OverflowCustomTexts.TryGetValue(fieldIdValue, out LazyString prevCt)
            && prevCt.Equals(currentCustomText))
        {
            flags |= SameFlags.FieldSameCustomText;
        }
        _OverflowCustomTexts[fieldIdValue] = currentCustomText;

        return flags;
    }

    private uint _CompareAndUpdateSparse(
        int fieldIdValue,
        FieldValue currentValue,
        LazyString currentValueCustomText,
        LazyString currentCustomText)
    {
        uint flags = 0;

        if (_SparseHasValue!.TryGetValue(fieldIdValue, out bool had)
            && had
            && _SparseValues!.TryGetValue(fieldIdValue, out FieldValue prev)
            && prev.Equals(currentValue))
        {
            flags |= SameFlags.FieldSameValue;
        }
        _SparseValues![fieldIdValue] = currentValue;
        _SparseHasValue[fieldIdValue] = true;

        if (_SparseValueCustomTexts!.TryGetValue(fieldIdValue, out LazyString prevVct)
            && prevVct.Equals(currentValueCustomText))
        {
            flags |= SameFlags.FieldSameCustomRepresentation;
        }
        _SparseValueCustomTexts[fieldIdValue] = currentValueCustomText;

        if (_SparseCustomTexts!.TryGetValue(fieldIdValue, out LazyString prevCt)
            && prevCt.Equals(currentCustomText))
        {
            flags |= SameFlags.FieldSameCustomText;
        }
        _SparseCustomTexts[fieldIdValue] = currentCustomText;

        return flags;
    }

    /// <summary>Resets all stored previous values.</summary>
    internal void Reset()
    {
        if (_DenseValues is not null)
        {
            Array.Clear(_DenseValues);
            Array.Clear(_DenseHasValue!);
            Array.Clear(_DenseCustomTexts!);
            Array.Clear(_DenseHasCustomText!);
            Array.Clear(_DenseValueCustomTexts!);
            Array.Clear(_DenseHasValueCustomText!);
            _OverflowValues?.Clear();
            _OverflowHasValue?.Clear();
            _OverflowCustomTexts?.Clear();
            _OverflowValueCustomTexts?.Clear();
        }
        else
        {
            _SparseValues!.Clear();
            _SparseHasValue!.Clear();
            _SparseCustomTexts!.Clear();
            _SparseValueCustomTexts!.Clear();
        }
    }
}
