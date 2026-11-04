// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Stores previously seen field values for same-as-previous detection in the JSON Compact
/// writer. Value comparison uses typed <see cref="FieldValue"/> payloads; custom representation and custom text compare with
/// <see cref="LazyString.Equals(LazyString)"/>, which builds both texts (ordinal comparison of the strings). The writer emits
/// every text that is not flagged, so building it here costs nothing extra.
/// Distinct from <see cref="PreviousFieldValueStore"/> used by the PBF Standard block builder.
/// <para>
/// "Previous" means the last occurrence of the same field ID in write order, in any packet and at any tree position.
/// Value, custom representation and custom text are tracked independently, so each can repeat on its own.
/// </para>
/// <para>
/// <b>Retention:</b> the store keeps only the built string of a text (see <c>_Settled</c>), never the deferred formatter: a
/// formatter keeps its captured packet state after building, and one stored copy per field id would keep that state alive for
/// the whole export.
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
/// <seealso cref="PreviousFieldValueStore"/>
/// <seealso cref="SameFlags"/>
internal sealed class PreviousFieldStore
{
    #region Configuration and state

    /// <summary>
    /// Largest field count for which the dense arrays are used: 2048, a constant sizing choice, not a limit. Perf: six dense
    /// arrays of 2048 entries cost about 2048 × (32 + 1 + 8 + 1 + 8 + 1) bytes ≈ 100 KiB per JSON exporter and give one array
    /// load per comparison; beyond that the dictionaries hold only the ids that occur. PreviousFieldValueStore uses the same value.
    /// The JSON exporter passes it as the field count of its state, so its stores take the dense path.
    /// </summary>
    internal const int DenseThreshold = 2048;

    /// <summary>Dense representation: the previous value per field id; null with the dictionary representation.</summary>
    private readonly FieldValue[]? _DenseValues;

    /// <summary>Dense representation: whether <see cref="_DenseValues"/> holds a value for the field id.</summary>
    private readonly bool[]? _DenseHasValue;

    /// <summary>Dense representation: the previous custom text per field id (string-backed).</summary>
    private readonly LazyString[]? _DenseCustomTexts;

    /// <summary>Dense representation: whether <see cref="_DenseCustomTexts"/> holds a text for the field id.</summary>
    private readonly bool[]? _DenseHasCustomText;

    /// <summary>Dense representation: the previous custom representation per field id (string-backed).</summary>
    private readonly LazyString[]? _DenseValueCustomTexts;

    /// <summary>Dense representation: whether <see cref="_DenseValueCustomTexts"/> holds a text for the field id.</summary>
    private readonly bool[]? _DenseHasValueCustomText;

    /// <summary>Dictionary representation: the previous value per field id; null with the dense representation.</summary>
    private readonly Dictionary<int, FieldValue>? _SparseValues;

    /// <summary>Dictionary representation: whether <see cref="_SparseValues"/> holds a value for the field id.</summary>
    private readonly Dictionary<int, bool>? _SparseHasValue;

    /// <summary>Dictionary representation: the previous custom text per field id (string-backed).</summary>
    private readonly Dictionary<int, LazyString>? _SparseCustomTexts;

    /// <summary>Dictionary representation: the previous custom representation per field id (string-backed).</summary>
    private readonly Dictionary<int, LazyString>? _SparseValueCustomTexts;

    /// <summary>Overflow of the dense representation: previous values of field ids outside the dense arrays; created on first use.</summary>
    private Dictionary<int, FieldValue>? _OverflowValues;

    /// <summary>Overflow of the dense representation: whether <see cref="_OverflowValues"/> holds a value for the field id.</summary>
    private Dictionary<int, bool>? _OverflowHasValue;

    /// <summary>Overflow of the dense representation: previous custom texts of field ids outside the dense arrays.</summary>
    private Dictionary<int, LazyString>? _OverflowCustomTexts;

    /// <summary>Overflow of the dense representation: previous custom representations of field ids outside the dense arrays.</summary>
    private Dictionary<int, LazyString>? _OverflowValueCustomTexts;

    /// <summary>Creates a store optimized for the given field count.</summary>
    /// <param name="fieldCount">
    /// Number of field IDs expected (IDs <c>0</c> to <c>fieldCount - 1</c>); must not be negative. Selects the dense
    /// or the dictionary representation.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldCount"/> is negative.</exception>
    internal PreviousFieldStore(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldCount);
        if (fieldCount <= DenseThreshold)
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

    #endregion

    #region Comparison and update

    /// <summary>
    /// Computes same-as-previous flags using typed value and <see cref="LazyString"/> custom text, then stores the current
    /// occurrence as the new previous one.
    /// </summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="currentValue">The field's value.</param>
    /// <param name="currentValueCustomText">The value's custom representation (built here by the comparison).</param>
    /// <param name="currentCustomText">The field's custom text (built here by the comparison).</param>
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
        _DenseValueCustomTexts![fieldIdValue] = _Settled(currentValueCustomText);
        _DenseHasValueCustomText[fieldIdValue] = true;

        if (_DenseHasCustomText![fieldIdValue]
            && _DenseCustomTexts![fieldIdValue].Equals(currentCustomText))
        {
            flags |= SameFlags.FieldSameCustomText;
        }
        _DenseCustomTexts![fieldIdValue] = _Settled(currentCustomText);
        _DenseHasCustomText[fieldIdValue] = true;

        return flags;
    }

    /// <summary>
    /// Same comparison as the dense path for field IDs outside the dense arrays; the dictionaries are created on
    /// first use because such IDs are not expected in normal operation.
    /// </summary>
    /// <param name="fieldIdValue">The field ID, negative or beyond the dense arrays.</param>
    /// <param name="currentValue">The field's value.</param>
    /// <param name="currentValueCustomText">The value's custom representation.</param>
    /// <param name="currentCustomText">The field's custom text.</param>
    /// <returns>The same-as-previous flags, as <see cref="CompareAndUpdate"/> returns them.</returns>
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
        _OverflowValueCustomTexts[fieldIdValue] = _Settled(currentValueCustomText);

        if (_OverflowCustomTexts.TryGetValue(fieldIdValue, out LazyString prevCt)
            && prevCt.Equals(currentCustomText))
        {
            flags |= SameFlags.FieldSameCustomText;
        }
        _OverflowCustomTexts[fieldIdValue] = _Settled(currentCustomText);

        return flags;
    }

    /// <summary>Same comparison as the dense path for the dictionary representation used with a large ID space.</summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="currentValue">The field's value.</param>
    /// <param name="currentValueCustomText">The value's custom representation.</param>
    /// <param name="currentCustomText">The field's custom text.</param>
    /// <returns>The same-as-previous flags, as <see cref="CompareAndUpdate"/> returns them.</returns>
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
        _SparseValueCustomTexts[fieldIdValue] = _Settled(currentValueCustomText);

        if (_SparseCustomTexts!.TryGetValue(fieldIdValue, out LazyString prevCt)
            && prevCt.Equals(currentCustomText))
        {
            flags |= SameFlags.FieldSameCustomText;
        }
        _SparseCustomTexts[fieldIdValue] = _Settled(currentCustomText);

        return flags;
    }

    /// <summary>
    /// The text as a string-backed value, so the store never keeps a deferred formatter (and its captured state) across packets.
    /// </summary>
    /// <param name="text">The text of the current occurrence; absent stays absent.</param>
    /// <returns><paramref name="text"/> itself when absent; otherwise a <see cref="LazyString"/> over its built string.</returns>
    /// <remarks>Perf: the build is shared with the writer (a deferred text builds once for all copies), so no second format runs.</remarks>
    private static LazyString _Settled(LazyString text) =>
        text.IsNull
            ? text
            : new LazyString(text.AsString);

    /// <summary>Reads back the stored previous texts of a field id (for tests that prove what the store retains).</summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="customRepresentation">The stored custom representation, or the absent text when none is stored.</param>
    /// <param name="customText">The stored custom text, or the absent text when none is stored.</param>
    /// <returns><see langword="true"/> when the field id has a stored previous occurrence.</returns>
    internal bool TryGetStoredTexts(int fieldIdValue, out LazyString customRepresentation, out LazyString customText)
    {
        customRepresentation = default;
        customText = default;
        if (_DenseValues is null)
        {
            return _SparseHasValue!.ContainsKey(fieldIdValue)
                && _SparseValueCustomTexts!.TryGetValue(fieldIdValue, out customRepresentation)
                && _SparseCustomTexts!.TryGetValue(fieldIdValue, out customText);
        }

        if (fieldIdValue < 0 || fieldIdValue >= _DenseValues.Length)
        {
            return _OverflowHasValue is not null
                && _OverflowHasValue.ContainsKey(fieldIdValue)
                && _OverflowValueCustomTexts!.TryGetValue(fieldIdValue, out customRepresentation)
                && _OverflowCustomTexts!.TryGetValue(fieldIdValue, out customText);
        }

        customRepresentation = _DenseValueCustomTexts![fieldIdValue];
        customText = _DenseCustomTexts![fieldIdValue];
        return _DenseHasValue![fieldIdValue];
    }

    #endregion

    #region Reset

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

    #endregion
}
