// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Stores previously seen field payloads for same-as-previous detection in the PBF Standard
/// block builder. Compares <see cref="FieldValueData"/> (via
/// <see cref="FieldValueData.Equals(FieldValueData)"/>) and optional custom representation /
/// custom text strings — no string formatting of values.
/// Distinct from <see cref="PreviousFieldStore"/> (JSON Compact; typed <see cref="FieldValue"/>).
/// </summary>
internal sealed class PreviousFieldValueStore
{
    #region Constants

    private const int _DenseThreshold = 2048;

    #endregion

    #region Nested Types

    private readonly record struct Snapshot(
        FieldValueData Data,
        string? CustomRepresentation,
        string? CustomText,
        bool HasValue)
    {
        #region Construction

        internal Snapshot(FieldValueData data, string? customRepresentation, string? customText)
            : this(data, customRepresentation, customText, HasValue: true)
        {
        }

        #endregion
    }

    #endregion

    #region Fields

    private readonly Snapshot[]? _DenseValues;
    private readonly Dictionary<int, Snapshot>? _SparseValues;
    private Dictionary<int, Snapshot>? _OverflowValues;

    #endregion

    #region Constructor

    /// <summary>Creates a store optimized for the given field count.</summary>
    internal PreviousFieldValueStore(int fieldCount)
    {
        if (fieldCount <= _DenseThreshold)
        {
            _DenseValues = new Snapshot[fieldCount];
        }
        else
        {
            _SparseValues = new(256);
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Computes same-as-previous flags and updates the stored snapshot.
    /// Pass <paramref name="hasValue"/> = <see langword="false"/> for container fields with no payload.
    /// </summary>
    internal uint CompareAndUpdate(
        int fieldIdValue,
        bool hasValue,
        in FieldValueData data,
        string? customRepresentation,
        string? customText)
    {
        Snapshot previous = _GetPrevious(fieldIdValue);
        Snapshot current = hasValue
            ? new Snapshot(data, customRepresentation, customText)
            : default;
        _SetPrevious(fieldIdValue, current);

        if (!previous.HasValue || !hasValue)
        {
            return 0;
        }

        uint flags = 0;
        if (previous.Data.Equals(data))
        {
            flags |= SameFlags.FieldSameValue;
        }

        if (customRepresentation is not null
            && string.Equals(previous.CustomRepresentation, customRepresentation, StringComparison.Ordinal))
        {
            flags |= SameFlags.FieldSameCustomRepresentation;
        }

        if (customText is not null
            && string.Equals(previous.CustomText, customText, StringComparison.Ordinal))
        {
            flags |= SameFlags.FieldSameCustomText;
        }

        return flags;
    }

    /// <summary>Resets all stored previous values (for block boundary resets).</summary>
    internal void Reset()
    {
        if (_DenseValues is not null)
        {
            Array.Clear(_DenseValues);
            _OverflowValues?.Clear();
        }
        else
        {
            _SparseValues!.Clear();
        }
    }

    #endregion

    #region Private Helpers

    private Snapshot _GetPrevious(int fieldIdValue)
    {
        if (_DenseValues is not null)
        {
            if (fieldIdValue >= 0 && fieldIdValue < _DenseValues.Length)
            {
                return _DenseValues[fieldIdValue];
            }

            if (_OverflowValues is not null && _OverflowValues.TryGetValue(fieldIdValue, out Snapshot overflow))
            {
                return overflow;
            }

            return default;
        }

        return _SparseValues!.TryGetValue(fieldIdValue, out Snapshot sparse)
            ? sparse
            : default;
    }

    private void _SetPrevious(int fieldIdValue, Snapshot current)
    {
        if (_DenseValues is not null)
        {
            if (fieldIdValue >= 0 && fieldIdValue < _DenseValues.Length)
            {
                _DenseValues[fieldIdValue] = current;
                return;
            }

            _OverflowValues ??= new Dictionary<int, Snapshot>(16);
            _OverflowValues[fieldIdValue] = current;
            return;
        }

        _SparseValues![fieldIdValue] = current;
    }

    #endregion
}
