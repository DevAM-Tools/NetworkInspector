// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Stores previously seen field values for same-as-previous detection.
/// Shared between the JSON Compact writer and the PBF Standard block builder.
/// <para>
/// For small field counts (≤ <see cref="DenseThreshold"/>), uses a dense <c>string?[]</c>
/// array indexed by field ID for O(1) access. For larger field counts, falls back
/// to a <see cref="Dictionary{TKey, TValue}"/>. The threshold balances memory usage
/// against lookup speed for typical protocol stacks.
/// </para>
/// </summary>
internal sealed class PreviousFieldStore
{
    /// <summary>Maximum field count for the dense (array-based) representation.</summary>
    private const int DenseThreshold = 2048;

    private readonly string?[]? _DenseValues;
    private readonly string?[]? _DenseCustomTexts;
    private readonly string?[]? _DenseValueCustomTexts;
    private readonly Dictionary<int, string?>? _SparseValues;
    private readonly Dictionary<int, string?>? _SparseCustomTexts;
    private readonly Dictionary<int, string?>? _SparseValueCustomTexts;
    // Dense-mode overflow store for field IDs that fall outside the dense
    // array. Allocated lazily so the common (in-range) hot path stays alloc-free.
    private Dictionary<int, string?>? _OverflowValues;
    private Dictionary<int, string?>? _OverflowCustomTexts;
    private Dictionary<int, string?>? _OverflowValueCustomTexts;

    /// <summary>Creates a store optimized for the given field count.</summary>
    /// <param name="fieldCount">Expected maximum field count. Determines dense vs sparse mode.</param>
    internal PreviousFieldStore(int fieldCount)
    {
        if (fieldCount <= DenseThreshold)
        {
            _DenseValues = new string?[fieldCount];
            _DenseCustomTexts = new string?[fieldCount];
            _DenseValueCustomTexts = new string?[fieldCount];
        }
        else
        {
            _SparseValues = new(256);
            _SparseCustomTexts = new(256);
            _SparseValueCustomTexts = new(256);
        }
    }

    /// <summary>
    /// Computes same-as-previous flags for a field and updates the stored values.
    /// </summary>
    /// <param name="fieldIdValue">Numeric field ID value.</param>
    /// <param name="currentValue">Current field value representation (may be null for None type).</param>
    /// <param name="currentValueCustomText">Current value custom representation text.</param>
    /// <param name="currentCustomText">Current field custom text.</param>
    /// <returns>Bitmask of <see cref="SameFlags"/> indicating which values are identical.</returns>
    internal uint CompareAndUpdate(
        int fieldIdValue, string? currentValue, string? currentValueCustomText, string? currentCustomText)
    {
        uint flags = 0;

        if (_DenseValues is not null)
        {
            if (fieldIdValue >= 0 && fieldIdValue < _DenseValues.Length)
            {
                // Dense mode — direct array indexing
                if (_DenseValues[fieldIdValue] is not null
                    && string.Equals(_DenseValues[fieldIdValue], currentValue, StringComparison.Ordinal))
                {
                    flags |= SameFlags.FieldSameValue;
                }
                _DenseValues[fieldIdValue] = currentValue;

                if (_DenseValueCustomTexts![fieldIdValue] is not null
                    && string.Equals(_DenseValueCustomTexts[fieldIdValue], currentValueCustomText, StringComparison.Ordinal))
                {
                    flags |= SameFlags.FieldSameCustomRepresentation;
                }
                _DenseValueCustomTexts[fieldIdValue] = currentValueCustomText;

                if (_DenseCustomTexts![fieldIdValue] is not null
                    && string.Equals(_DenseCustomTexts[fieldIdValue], currentCustomText, StringComparison.Ordinal))
                {
                    flags |= SameFlags.FieldSameCustomText;
                }
                _DenseCustomTexts[fieldIdValue] = currentCustomText;
            }
            else
            {

                _OverflowValues ??= new Dictionary<int, string?>(16);
                _OverflowValueCustomTexts ??= new Dictionary<int, string?>(16);
                _OverflowCustomTexts ??= new Dictionary<int, string?>(16);

                if (_OverflowValues.TryGetValue(fieldIdValue, out string? prevValue)
                    && string.Equals(prevValue, currentValue, StringComparison.Ordinal))
                {
                    flags |= SameFlags.FieldSameValue;
                }
                _OverflowValues[fieldIdValue] = currentValue;

                if (_OverflowValueCustomTexts.TryGetValue(fieldIdValue, out string? prevVct)
                    && string.Equals(prevVct, currentValueCustomText, StringComparison.Ordinal))
                {
                    flags |= SameFlags.FieldSameCustomRepresentation;
                }
                _OverflowValueCustomTexts[fieldIdValue] = currentValueCustomText;

                if (_OverflowCustomTexts.TryGetValue(fieldIdValue, out string? prevCt)
                    && string.Equals(prevCt, currentCustomText, StringComparison.Ordinal))
                {
                    flags |= SameFlags.FieldSameCustomText;
                }
                _OverflowCustomTexts[fieldIdValue] = currentCustomText;
            }
        }
        else
        {
            // Sparse mode — dictionary lookup
            if (_SparseValues!.TryGetValue(fieldIdValue, out string? prevValue)
                && string.Equals(prevValue, currentValue, StringComparison.Ordinal))
            {
                flags |= SameFlags.FieldSameValue;
            }
            _SparseValues[fieldIdValue] = currentValue;

            if (_SparseValueCustomTexts!.TryGetValue(fieldIdValue, out string? prevVct)
                && string.Equals(prevVct, currentValueCustomText, StringComparison.Ordinal))
            {
                flags |= SameFlags.FieldSameCustomRepresentation;
            }
            _SparseValueCustomTexts[fieldIdValue] = currentValueCustomText;

            if (_SparseCustomTexts!.TryGetValue(fieldIdValue, out string? prevCt)
                && string.Equals(prevCt, currentCustomText, StringComparison.Ordinal))
            {
                flags |= SameFlags.FieldSameCustomText;
            }
            _SparseCustomTexts[fieldIdValue] = currentCustomText;
        }

        return flags;
    }

    /// <summary>Resets all stored previous values (for block boundary resets).</summary>
    internal void Reset()
    {
        if (_DenseValues is not null)
        {
            Array.Clear(_DenseValues);
            Array.Clear(_DenseCustomTexts!);
            Array.Clear(_DenseValueCustomTexts!);
            _OverflowValues?.Clear();
            _OverflowCustomTexts?.Clear();
            _OverflowValueCustomTexts?.Clear();
        }
        else
        {
            _SparseValues!.Clear();
            _SparseCustomTexts!.Clear();
            _SparseValueCustomTexts!.Clear();
        }
    }
}
