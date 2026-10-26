// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Stores previously seen field values for same-as-previous detection in the JSON Compact
/// writer. Value comparison uses typed <see cref="FieldValue"/> payloads; custom representation
/// / custom text use <see cref="LazyString"/> equality so deferred text is not materialized
/// solely for comparison.
/// Distinct from <see cref="PreviousFieldValueStore"/> used by the PBF Standard block builder.
/// <para>
/// "Previous" means the last occurrence of the same field ID in write order, in any packet and at any tree position.
/// Value, custom representation and custom text are tracked independently, so each can repeat on its own.
/// </para>
/// <para>
/// <b>Storage choice:</b> a set of parallel arrays indexed by field ID (no hashing, no per-lookup allocation) when the
/// ID space is small, dictionaries when it is large; IDs outside the dense range (including negative ones) fall back
/// to overflow dictionaries so they are still compared correctly. The structure-of-arrays layout lets each of the
/// three comparisons touch only its own array.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one JSON exporter state and used from its single caller thread.
/// </para>
/// </summary>
internal sealed class PreviousFieldStore
{
    // Largest field count for which the dense arrays are used. Value 2048; constant. Beyond it the dictionaries only
    // hold field IDs that actually occurred instead of six arrays sized for the whole ID space. The reason for this
    // particular number is not recorded; PreviousFieldValueStore uses the same value.
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
    /// <param name="fieldCount">
    /// Number of field IDs expected (IDs <c>0</c> to <c>fieldCount - 1</c>); must not be negative. Selects the dense
    /// or the dictionary representation.
    /// </param>
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
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="currentValue">The field's value.</param>
    /// <param name="currentValueCustomText">The value's custom representation (deferred; not materialized here).</param>
    /// <param name="currentCustomText">The field's custom text (deferred; not materialized here).</param>
    /// <returns>
    /// A combination of <see cref="SameFlags.FieldSameValue"/>, <see cref="SameFlags.FieldSameCustomRepresentation"/>
    /// and <see cref="SameFlags.FieldSameCustomText"/>; each bit means "identical to the previous occurrence".
    /// </returns>
    internal uint CompareAndUpdate(
        int fieldIdValue,
        FieldValue currentValue,
        LazyString currentValueCustomText,
        LazyString currentCustomText)
    {
        // Pick the representation: dictionaries for a large ID space, overflow dictionaries for IDs outside the dense
        // arrays, otherwise the fast array path below.
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

        // Dense path: compare with the stored previous (only once one exists), then overwrite it with the current
        // one so the next occurrence compares against this write. The "has" flags distinguish "never seen" from a
        // stored default value.
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

    /// <summary>
    /// Same comparison as the dense path for field IDs outside the dense arrays; the dictionaries are created on
    /// first use because such IDs are not expected in normal operation.
    /// </summary>
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

    /// <summary>Same comparison as the dense path for the dictionary representation used with a large ID space.</summary>
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

    /// <summary>Resets all stored previous values, forgetting every earlier occurrence.</summary>
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
